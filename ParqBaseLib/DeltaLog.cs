using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Parquet;

namespace ParqBaseLib
{
    /// <summary>
    /// A single active data file in a Delta snapshot: the resolved absolute path to a Parquet file
    /// plus the partition column values that apply to every row in it (partition columns are stored
    /// in the transaction log, not inside the Parquet file).
    /// </summary>
    internal sealed class DeltaFile
    {
        public required string AbsolutePath { get; init; }

        /// <summary>Partition column name → value (string form, as recorded in the log); null = SQL NULL.</summary>
        public required IReadOnlyDictionary<string, string?> PartitionValues { get; init; }
    }

    /// <summary>One column of a Delta table's logical schema, in declaration order.</summary>
    internal sealed class DeltaColumn
    {
        public required string Name { get; init; }

        /// <summary>The Delta primitive type name (e.g. "long", "string", "date", "decimal(18,2)").</summary>
        public required string DeltaType { get; init; }

        public required bool IsPartition { get; init; }
    }

    /// <summary>
    /// The reconstructed state of a Delta table at a specific version: the set of active data files
    /// (tombstoned/removed files excluded), the partition columns, and the logical column schema.
    /// </summary>
    internal sealed class DeltaSnapshot
    {
        public required long Version { get; init; }

        public required IReadOnlyList<DeltaFile> Files { get; init; }

        public required IReadOnlyList<string> PartitionColumns { get; init; }

        /// <summary>Full logical schema (data + partition columns) in declaration order.</summary>
        public required IReadOnlyList<DeltaColumn> Schema { get; init; }

        public bool IsPartitioned => this.PartitionColumns.Count > 0;
    }

    /// <summary>
    /// Read-only reader for the Delta Lake transaction log (<c>_delta_log</c>). Reconstructs the
    /// current (or a historical) table snapshot by replaying the ordered JSON commit files, optionally
    /// seeded from a checkpoint, applying <c>add</c>/<c>remove</c> actions so that files logically
    /// deleted by UPDATE/DELETE/overwrite are excluded — the guarantee plain Parquet-directory reads
    /// cannot give. Writing to Delta tables is not supported.
    /// </summary>
    internal static class DeltaLog
    {
        private const string LogDirName = "_delta_log";

        /// <summary>Reader features this engine can honour; any other feature forces a clear error.</summary>
        private static readonly HashSet<string> SupportedReaderFeatures =
            new(StringComparer.OrdinalIgnoreCase) { "timestampNtz", "appendOnly", "invariants" };

        private static readonly Regex CommitFileRegex = new(@"^(\d{20})\.json$", RegexOptions.Compiled);
        private static readonly Regex CheckpointRegex =
            new(@"^(\d{20})\.checkpoint(?:\.\d+\.of\.\d+)?\.parquet$", RegexOptions.Compiled);

        /// <summary>True when <paramref name="tableDir"/> is the root of a Delta table.</summary>
        public static bool IsDeltaTable(string tableDir)
            => !string.IsNullOrEmpty(tableDir) && Directory.Exists(Path.Combine(tableDir, LogDirName));

        /// <summary>
        /// Reconstructs the snapshot of the Delta table at <paramref name="tableDir"/>. When
        /// <paramref name="asOfVersion"/> is null the latest version is returned; otherwise the state
        /// as of that commit version (time travel by version).
        /// </summary>
        public static DeltaSnapshot ReadSnapshot(string tableDir, long? asOfVersion = null)
        {
            var logDir = Path.Combine(tableDir, LogDirName);
            if (!Directory.Exists(logDir))
            {
                throw new InvalidOperationException($"'{tableDir}' is not a Delta table (no {LogDirName} folder).");
            }

            // Discover commit and checkpoint versions present in the log.
            var commits = new SortedDictionary<long, string>();
            var checkpointVersions = new SortedSet<long>();
            foreach (var file in Directory.GetFiles(logDir))
            {
                var name = Path.GetFileName(file);
                var cm = CommitFileRegex.Match(name);
                if (cm.Success)
                {
                    commits[long.Parse(cm.Groups[1].Value, CultureInfo.InvariantCulture)] = file;
                    continue;
                }

                var kp = CheckpointRegex.Match(name);
                if (kp.Success)
                {
                    checkpointVersions.Add(long.Parse(kp.Groups[1].Value, CultureInfo.InvariantCulture));
                }
            }

            if (commits.Count == 0 && checkpointVersions.Count == 0)
            {
                throw new InvalidOperationException($"Delta log at '{logDir}' contains no commits.");
            }

            var target = asOfVersion ?? (commits.Count > 0 ? commits.Keys.Max() : checkpointVersions.Max());
            if (asOfVersion.HasValue && !commits.ContainsKey(asOfVersion.Value) && !checkpointVersions.Contains(asOfVersion.Value))
            {
                throw new InvalidOperationException(
                    $"Delta version {asOfVersion.Value} does not exist for table '{Path.GetFileName(tableDir)}'.");
            }

            var active = new Dictionary<string, DeltaFile>(StringComparer.Ordinal);
            string? schemaString = null;
            var partitionColumns = new List<string>();
            long baseVersion = -1;

            // Choose a checkpoint at or below the target as the replay base (required if early commit
            // JSON has been cleaned up; an optimization otherwise).
            var checkpoint = checkpointVersions.Where(v => v <= target).DefaultIfEmpty(-1).Max();
            var earliestCommit = commits.Count > 0 ? commits.Keys.Min() : long.MaxValue;
            var needCheckpoint = earliestCommit > 0; // commit 0 missing → log was truncated, must seed from checkpoint

            if (checkpoint >= 0 && (needCheckpoint || checkpoint > 0))
            {
                if (TrySeedFromCheckpoint(logDir, checkpoint, active))
                {
                    baseVersion = checkpoint;
                }
                else if (needCheckpoint)
                {
                    throw new NotSupportedException(
                        $"Delta table '{Path.GetFileName(tableDir)}' has a truncated log and its checkpoint " +
                        "format is not supported by this reader. Provide the full commit history.");
                }
            }

            if (baseVersion < 0 && earliestCommit > 0)
            {
                throw new NotSupportedException(
                    $"Delta table '{Path.GetFileName(tableDir)}' is missing commit 0 and has no readable checkpoint.");
            }

            // Replay commit JSON files after the checkpoint base, up to and including the target.
            foreach (var (version, path) in commits)
            {
                if (version <= baseVersion || version > target)
                {
                    continue;
                }

                ApplyCommit(path, tableDir, active, ref schemaString, partitionColumns);
            }

            var schema = ParseSchema(schemaString, partitionColumns);

            // Partition columns may only be known once the metaData action is seen; back-fill any
            // add whose partition values were parsed from its path.
            return new DeltaSnapshot
            {
                Version = target,
                Files = active.Values
                    .OrderBy(f => f.AbsolutePath, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                PartitionColumns = partitionColumns,
                Schema = schema,
            };
        }

        /// <summary>Applies every action line of one commit JSON file to the running state.</summary>
        private static void ApplyCommit(
            string commitPath,
            string tableDir,
            Dictionary<string, DeltaFile> active,
            ref string? schemaString,
            List<string> partitionColumns)
        {
            foreach (var line in File.ReadLines(commitPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.TryGetProperty("protocol", out var protocol))
                {
                    GuardProtocol(protocol, tableDir);
                }

                if (root.TryGetProperty("metaData", out var metaData))
                {
                    if (metaData.TryGetProperty("schemaString", out var ss) && ss.ValueKind == JsonValueKind.String)
                    {
                        schemaString = ss.GetString();
                    }

                    partitionColumns.Clear();
                    if (metaData.TryGetProperty("partitionColumns", out var pc) && pc.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var col in pc.EnumerateArray())
                        {
                            if (col.ValueKind == JsonValueKind.String)
                            {
                                partitionColumns.Add(col.GetString()!);
                            }
                        }
                    }
                }

                if (root.TryGetProperty("add", out var add) && add.TryGetProperty("path", out var addPath))
                {
                    var rel = addPath.GetString()!;
                    var abs = ResolvePath(tableDir, rel);
                    active[rel] = new DeltaFile
                    {
                        AbsolutePath = abs,
                        PartitionValues = ReadPartitionValues(add, rel),
                    };
                }

                if (root.TryGetProperty("remove", out var remove) && remove.TryGetProperty("path", out var removePath))
                {
                    active.Remove(removePath.GetString()!);
                }
            }
        }

        /// <summary>
        /// Rejects tables whose protocol requires reader features this engine does not implement
        /// (e.g. deletion vectors, column mapping), so we never return silently wrong rows.
        /// </summary>
        private static void GuardProtocol(JsonElement protocol, string tableDir)
        {
            var minReader = protocol.TryGetProperty("minReaderVersion", out var mr) && mr.ValueKind == JsonValueKind.Number
                ? mr.GetInt32()
                : 1;

            if (minReader <= 2)
            {
                return; // versions 1 and 2 use no table features beyond what a full-file read handles
            }

            if (protocol.TryGetProperty("readerFeatures", out var features) && features.ValueKind == JsonValueKind.Array)
            {
                var unsupported = features.EnumerateArray()
                    .Where(f => f.ValueKind == JsonValueKind.String)
                    .Select(f => f.GetString()!)
                    .Where(f => !SupportedReaderFeatures.Contains(f))
                    .ToList();

                if (unsupported.Count == 0)
                {
                    return;
                }

                throw new NotSupportedException(
                    $"Delta table '{Path.GetFileName(tableDir)}' requires reader feature(s) " +
                    $"[{string.Join(", ", unsupported)}] that this engine does not support (read-only interop).");
            }

            throw new NotSupportedException(
                $"Delta table '{Path.GetFileName(tableDir)}' declares minReaderVersion {minReader}, which this engine does not support.");
        }

        /// <summary>
        /// Reads a file's partition values from the commit's <c>partitionValues</c> map, falling back
        /// to parsing Hive-style <c>col=value</c> segments from the file path when the map is absent.
        /// </summary>
        private static IReadOnlyDictionary<string, string?> ReadPartitionValues(JsonElement add, string relativePath)
        {
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            if (add.TryGetProperty("partitionValues", out var pv) && pv.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in pv.EnumerateObject())
                {
                    values[prop.Name] = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.GetString();
                }

                if (values.Count > 0)
                {
                    return values;
                }
            }

            foreach (var segment in relativePath.Split('/', '\\'))
            {
                var eq = segment.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                var key = Uri.UnescapeDataString(segment[..eq]);
                var raw = Uri.UnescapeDataString(segment[(eq + 1)..]);
                values[key] = string.Equals(raw, "__HIVE_DEFAULT_PARTITION__", StringComparison.Ordinal) ? null : raw;
            }

            return values;
        }

        /// <summary>Resolves a log-relative (URL-encoded) file path to an absolute path on disk.</summary>
        private static string ResolvePath(string tableDir, string relative)
        {
            var decoded = Uri.UnescapeDataString(relative).Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(tableDir, decoded));
        }

        /// <summary>
        /// Best-effort seed of the active file set from a checkpoint Parquet file's flat
        /// <c>add.path</c>/<c>remove.path</c> leaf columns. Returns false if the checkpoint cannot be
        /// read in a form we understand (caller then decides whether that is fatal).
        /// </summary>
        private static bool TrySeedFromCheckpoint(string logDir, long version, Dictionary<string, DeltaFile> active)
        {
            try
            {
                var parts = Directory.GetFiles(logDir, $"{version:D20}.checkpoint*.parquet")
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (parts.Count == 0)
                {
                    return false;
                }

                var tableDir = Directory.GetParent(logDir)!.FullName;
                var seeded = false;

                foreach (var part in parts)
                {
                    using Stream stream = File.OpenRead(part);
                    using var reader = ParquetReader.CreateAsync(stream).GetAwaiter().GetResult();
                    var fields = reader.Schema.GetDataFields();
                    var addPathField = fields.FirstOrDefault(f => PathEquals(f.Path.ToList(), "add", "path"));
                    var removePathField = fields.FirstOrDefault(f => PathEquals(f.Path.ToList(), "remove", "path"));
                    if (addPathField == null)
                    {
                        continue;
                    }

                    for (var g = 0; g < reader.RowGroupCount; g++)
                    {
                        using var group = reader.OpenRowGroupReader(g);
                        var addPaths = group.ReadColumnAsync(addPathField).GetAwaiter().GetResult().Data;
                        var removePaths = removePathField != null
                            ? group.ReadColumnAsync(removePathField).GetAwaiter().GetResult().Data
                            : null;

                        for (var i = 0; i < addPaths.Length; i++)
                        {
                            if (addPaths.GetValue(i) is string ap && ap.Length > 0)
                            {
                                active[ap] = new DeltaFile
                                {
                                    AbsolutePath = ResolvePath(tableDir, ap),
                                    PartitionValues = ReadPartitionValuesFromPath(ap),
                                };
                                seeded = true;
                            }
                            else if (removePaths?.GetValue(i) is string rp && rp.Length > 0)
                            {
                                active.Remove(rp);
                            }
                        }
                    }
                }

                return seeded;
            }
            catch
            {
                return false;
            }
        }

        private static IReadOnlyDictionary<string, string?> ReadPartitionValuesFromPath(string relativePath)
        {
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var segment in relativePath.Split('/', '\\'))
            {
                var eq = segment.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                var key = Uri.UnescapeDataString(segment[..eq]);
                var raw = Uri.UnescapeDataString(segment[(eq + 1)..]);
                values[key] = string.Equals(raw, "__HIVE_DEFAULT_PARTITION__", StringComparison.Ordinal) ? null : raw;
            }

            return values;
        }

        private static bool PathEquals(IReadOnlyList<string> path, string a, string b)
            => path.Count == 2
               && string.Equals(path[0], a, StringComparison.OrdinalIgnoreCase)
               && string.Equals(path[1], b, StringComparison.OrdinalIgnoreCase);

        /// <summary>Parses the Delta <c>schemaString</c> (a JSON struct) into ordered columns.</summary>
        private static IReadOnlyList<DeltaColumn> ParseSchema(string? schemaString, IReadOnlyList<string> partitionColumns)
        {
            var columns = new List<DeltaColumn>();
            if (string.IsNullOrWhiteSpace(schemaString))
            {
                return columns;
            }

            var partitionSet = new HashSet<string>(partitionColumns, StringComparer.OrdinalIgnoreCase);

            using var doc = JsonDocument.Parse(schemaString);
            if (!doc.RootElement.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
            {
                return columns;
            }

            foreach (var field in fields.EnumerateArray())
            {
                if (!field.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var name = nameEl.GetString()!;
                var type = "string";
                if (field.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
                {
                    type = typeEl.GetString()!;
                }

                columns.Add(new DeltaColumn
                {
                    Name = name,
                    DeltaType = type,
                    IsPartition = partitionSet.Contains(name),
                });
            }

            return columns;
        }
    }
}
