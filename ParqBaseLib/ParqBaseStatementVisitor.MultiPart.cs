using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet;

namespace ParqBaseLib
{
    /// <summary>
    /// Multi-part table support. A table is normally a single Parquet file
    /// (<c>tables/&lt;name&gt;.parquet</c>). It may instead be a <i>directory</i> of parts
    /// (<c>part-0.parquet</c> … <c>part-n.parquet</c>) that together form one logical table — the
    /// layout produced by tools such as the TPC-H generator. A directory-shaped table is read by
    /// concatenating every part in numeric order; all parts are assumed to share one schema.
    /// Directory tables live under either the standard <c>tables/</c> container or a <c>parquet/</c>
    /// container (the location the TPC-H data set uses).
    /// </summary>
    internal partial class ParqBaseStatementVisitor
    {
        /// <summary>Containers under a database directory that may hold table data.</summary>
        private static readonly string[] TableContainers = { "tables", "parquet" };

        /// <summary>
        /// Describes where a table's data lives on disk: either a single Parquet file or an ordered
        /// set of part files inside a directory. Callers read <see cref="Files"/> for the data and
        /// take a lock on <see cref="LockKey"/> (the file for single-file tables, the directory for
        /// multi-part tables).
        /// </summary>
        internal sealed class TableSource
        {
            public required bool Exists { get; init; }

            public required bool IsMultiPart { get; init; }

            /// <summary>Ordered Parquet files that make up the table (one for a single-file table).</summary>
            public required IReadOnlyList<string> Files { get; init; }

            /// <summary>Path used for read/write locking and (for single-file tables) the metadata sidecar.</summary>
            public required string LockKey { get; init; }

            /// <summary>The canonical single-file path, used by write paths (INSERT/UPDATE/DELETE).</summary>
            public required string SingleFilePath { get; init; }

            /// <summary>
            /// When non-null, this table is a read-only Delta Lake table; <see cref="Files"/> holds the
            /// active data files from the transaction-log snapshot and <see cref="Delta"/> carries the
            /// partition columns/schema needed to project partition values that live only in the log.
            /// </summary>
            public DeltaSnapshot? Delta { get; init; }

            /// <summary>First data file, used for schema inference. Empty string when the table is absent.</summary>
            public string PrimaryFile => this.Files.Count > 0 ? this.Files[0] : this.SingleFilePath;
        }

        /// <summary>Resolves a table in the current database to its on-disk source.</summary>
        private TableSource ResolveTableSource(string schema, string table)
            => ResolveTableSource(this.RequireDatabasePath(), schema, table);

        /// <summary>
        /// Resolves a (schema, table) pair in <paramref name="databasePath"/> to a
        /// <see cref="TableSource"/>. Prefers an existing single-file table (backward compatible);
        /// otherwise looks for a directory of parts under any known container.
        /// </summary>
        internal static TableSource ResolveTableSource(string databasePath, string schema, string table)
        {
            var isDbo = string.Equals(schema, Security.SecurityCatalog.DboSchema, StringComparison.OrdinalIgnoreCase);
            var baseName = isDbo ? table : $"{schema}.{table}";
            var singleFile = Path.Combine(databasePath, "tables", $"{baseName}.parquet");

            if (File.Exists(singleFile))
            {
                return new TableSource
                {
                    Exists = true,
                    IsMultiPart = false,
                    Files = new[] { singleFile },
                    LockKey = singleFile,
                    SingleFilePath = singleFile,
                };
            }

            foreach (var container in TableContainers)
            {
                var dir = Path.Combine(databasePath, container, baseName);
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                // A Delta table is a directory containing a _delta_log; its active file set must come
                // from the transaction log (not a raw glob, which would include tombstoned files).
                if (DeltaLog.IsDeltaTable(dir))
                {
                    var snapshot = DeltaLog.ReadSnapshot(dir);
                    return new TableSource
                    {
                        Exists = true,
                        IsMultiPart = true,
                        Files = snapshot.Files.Select(f => f.AbsolutePath).ToList(),
                        LockKey = dir,
                        SingleFilePath = singleFile,
                        Delta = snapshot,
                    };
                }

                var parts = EnumerateParts(dir);
                if (parts.Count > 0)
                {
                    return new TableSource
                    {
                        Exists = true,
                        IsMultiPart = true,
                        Files = parts,
                        LockKey = dir,
                        SingleFilePath = singleFile,
                    };
                }
            }

            return new TableSource
            {
                Exists = false,
                IsMultiPart = false,
                Files = Array.Empty<string>(),
                LockKey = singleFile,
                SingleFilePath = singleFile,
            };
        }

        /// <summary>
        /// Returns the Parquet part files in <paramref name="directory"/> ordered by their numeric
        /// suffix (<c>part-0</c>, <c>part-1</c>, …, <c>part-10</c>) so a lexical sort does not place
        /// <c>part-10</c> before <c>part-2</c>. Files without a numeric suffix sort last, by name.
        /// </summary>
        internal static List<string> EnumerateParts(string directory)
            => Directory.GetFiles(directory, "*.parquet")
                .OrderBy(PartIndex)
                .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

        /// <summary>Parses the trailing integer of a <c>part-N.parquet</c> name; unmatched names sort last.</summary>
        private static long PartIndex(string file)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var dash = name.LastIndexOf('-');
            if (dash >= 0 && dash < name.Length - 1 && long.TryParse(name.AsSpan(dash + 1), out var n))
            {
                return n;
            }

            return long.MaxValue;
        }

        /// <summary>
        /// Loads a table that spans one or more Parquet files, concatenating their rows into a single
        /// in-memory <see cref="TableData"/>. The schema is taken from the first file; later parts are
        /// assumed to share it (extra columns in a later part are ignored, missing columns throw).
        /// </summary>
        private async Task<TableData> LoadTableAsync(IReadOnlyList<string> files)
        {
            if (files.Count == 1)
            {
                return await this.LoadTableAsync(files[0]);
            }

            var data = new Dictionary<string, List<object?>>();
            var order = new List<string>();
            var types = new Dictionary<string, string>();

            foreach (var filePath in files)
            {
                using Stream readStream = File.OpenRead(filePath);
                using var reader = await ParquetReader.CreateAsync(readStream);

                if (order.Count == 0)
                {
                    foreach (var field in reader.Schema.DataFields)
                    {
                        data[field.Name] = new List<object?>();
                        order.Add(field.Name);
                        types[field.Name] = MapClrType(field.ClrType);
                    }
                }

                for (var g = 0; g < reader.RowGroupCount; g++)
                {
                    this.ThrowIfCancelled();
                    using var groupReader = reader.OpenRowGroupReader(g);
                    foreach (var field in reader.Schema.DataFields)
                    {
                        if (!data.TryGetValue(field.Name, out var column))
                        {
                            continue;
                        }

                        var dc = await groupReader.ReadColumnAsync(field);
                        foreach (var val in dc.Data)
                        {
                            column.Add(val);
                        }
                    }
                }
            }

            var rowCount = order.Count > 0 ? data[order[0]].Count : 0;
            return new TableData(data, order, types, rowCount);
        }

        /// <summary>
        /// Enumerates every table in a database — single-file tables in <c>tables/</c> plus
        /// directory-shaped (multi-part) tables in any container — de-duplicated by name.
        /// </summary>
        private static void CollectTables(string dbPath, List<(string Name, string Type)> sink)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var tablesDir = Path.Combine(dbPath, "tables");
            if (Directory.Exists(tablesDir))
            {
                foreach (var file in Directory.GetFiles(tablesDir, "*.parquet"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (seen.Add(name))
                    {
                        sink.Add((name, "table"));
                    }
                }
            }

            foreach (var container in TableContainers)
            {
                var dir = Path.Combine(dbPath, container);
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                foreach (var sub in Directory.GetDirectories(dir))
                {
                    if (!DeltaLog.IsDeltaTable(sub) && EnumerateParts(sub).Count == 0)
                    {
                        continue;
                    }

                    var name = Path.GetFileName(sub);
                    if (seen.Add(name))
                    {
                        sink.Add((name, "table"));
                    }
                }
            }
        }

        /// <summary>Sums the row counts of every part of a (possibly multi-part) table.</summary>
        internal static long CountRows(IReadOnlyList<string> files)
        {
            long total = 0;
            foreach (var file in files)
            {
                total += CountRows(file);
            }

            return total;
        }

        /// <summary>
        /// Validates that a write target is a writable single-file table. Multi-part (directory)
        /// tables are read-only; a clear error is raised rather than the generic "does not exist".
        /// </summary>
        private void GuardSingleFileWrite(string schema, string table, string filePath)
        {
            if (File.Exists(filePath))
            {
                return;
            }

            var source = this.ResolveTableSource(schema, table);
            if (source.Exists && source.Delta != null)
            {
                throw new NotSupportedException(
                    $"Table [{table}] is a read-only Delta Lake table; writes to Delta tables are not supported.");
            }

            if (source.Exists && source.IsMultiPart)
            {
                throw new NotSupportedException(
                    $"Table [{table}] is a multi-part (directory) table and is read-only; writes to multi-part tables are not supported.");
            }

            throw new Exception($"Table [{table}] does not exist.");
        }
    }
}
