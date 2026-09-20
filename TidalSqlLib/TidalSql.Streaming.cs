namespace TidalSqlLib
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Parquet;
    using TidalSqlLib.Security;

    /// <summary>One column of a streamed result set.</summary>
    public sealed record TableStreamColumn(string Name, string Type);

    /// <summary>
    /// A contiguous block of streamed rows. Rows are positional (aligned to
    /// <see cref="TableStreamResult.Columns"/>) so large result sets stay compact on the wire.
    /// </summary>
    public sealed record TableRowBatch(long StartIndex, IReadOnlyList<object?[]> Rows);

    /// <summary>
    /// A lazily-streamed table read. <see cref="Columns"/> and <see cref="TotalRows"/> are known up
    /// front (from the Parquet footer); <see cref="Batches"/> yields rows a row group at a time so a
    /// consumer never has to hold the whole table in memory.
    /// </summary>
    public sealed class TableStreamResult
    {
        public required IReadOnlyList<TableStreamColumn> Columns { get; init; }

        public required long TotalRows { get; init; }

        public required IAsyncEnumerable<TableRowBatch> Batches { get; init; }
    }

    public partial class TidalSql
    {
        /// <summary>
        /// Opens a memory-bounded, thread-safe stream over a table's rows for the UI / API to consume
        /// incrementally. Columns and the total row count are resolved immediately; the actual rows are
        /// produced lazily, one Parquet row group at a time, under a shared read lock so any number of
        /// readers run concurrently while writers are serialized (the same per-table
        /// <see cref="TableLock"/> that guards atomic writes). This is the streaming/batching model that
        /// makes large tables (e.g. the 500k-row sample) usable without materializing every row.
        /// </summary>
        /// <param name="database">Database name.</param>
        /// <param name="table">Table name.</param>
        /// <param name="schema">Schema (defaults to dbo).</param>
        /// <param name="batchSize">Maximum rows per emitted batch (clamped to a sane range).</param>
        /// <param name="offset">Number of leading rows to skip (server-side paging).</param>
        /// <param name="limit">Maximum rows to return across all batches, or null for all remaining.</param>
        public TableStreamResult OpenTableStream(
            string database,
            string table,
            string schema = "dbo",
            int batchSize = 10_000,
            long offset = 0,
            long? limit = null)
        {
            if (string.IsNullOrWhiteSpace(database))
            {
                throw new ArgumentException("A database name is required.", nameof(database));
            }

            if (string.IsNullOrWhiteSpace(table))
            {
                throw new ArgumentException("A table name is required.", nameof(table));
            }

            schema = string.IsNullOrWhiteSpace(schema) ? SecurityCatalog.DboSchema : schema;
            batchSize = Math.Clamp(batchSize, 1, 100_000);
            offset = Math.Max(0, offset);
            if (limit is < 0)
            {
                limit = 0;
            }

            var databasePath = Path.Combine(DatabasesRoot, database);
            if (!Directory.Exists(databasePath))
            {
                throw new Exception($"Database [{database}] does not exist.");
            }

            var source = TidalSqlStatementVisitor.ResolveTableSource(databasePath, schema, table);
            if (!source.Exists)
            {
                throw new Exception($"Table [{schema}].[{table}] does not exist in database [{database}].");
            }

            // Resolve the schema and total row count up front from the Parquet footers (cheap: no
            // column data is read). Done under a read lock so it cannot race a writer's atomic swap.
            // For a multi-part table the schema comes from the first part; the row count sums parts.
            List<TableStreamColumn> columns;
            long totalRows;
            using (TableLock.Read(source.LockKey))
            {
                using (Stream footerStream = File.OpenRead(source.PrimaryFile))
                using (var reader = ParquetReader.CreateAsync(footerStream).GetAwaiter().GetResult())
                {
                    columns = new List<TableStreamColumn>();
                    foreach (var field in reader.Schema.DataFields)
                    {
                        columns.Add(new TableStreamColumn(field.Name, TidalSqlStatementVisitor.MapClrTypeName(field.ClrType)));
                    }
                }

                totalRows = TidalSqlStatementVisitor.CountRows(source.Files);
            }

            var order = columns.ConvertAll(c => c.Name);

            return new TableStreamResult
            {
                Columns = columns,
                TotalRows = totalRows,
                Batches = TidalSqlStatementVisitor.StreamTableAsync(source.LockKey, source.Files, order, batchSize, offset, limit),
            };
        }
    }

    internal partial class TidalSqlStatementVisitor
    {
        /// <summary>
        /// Streams a table's rows a Parquet row group at a time. Only one row group is decoded into
        /// memory at once, so peak memory is bounded by the row-group size regardless of table size.
        /// The whole scan runs under a single shared read lock, giving a consistent snapshot (files
        /// cannot be swapped mid-scan) while still allowing concurrent readers. A table may span
        /// several part files; they are read in order and their row groups treated as one continuous
        /// sequence. Row groups that fall entirely outside the requested [offset, offset+limit)
        /// window are skipped without decoding.
        /// </summary>
        internal static async IAsyncEnumerable<TableRowBatch> StreamTableAsync(
            string lockKey,
            IReadOnlyList<string> files,
            IReadOnlyList<string> order,
            int batchSize,
            long offset,
            long? limit,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var lockScope = TableLock.Read(lockKey);

            long produced = 0;
            long globalIndex = 0;         // absolute row index of the next row to consider
            long emittedStart = offset;   // StartIndex of the batch currently being built
            var buffer = new List<object?[]>(batchSize);
            var stop = limit is { } lim ? offset + lim : long.MaxValue;

            foreach (var filePath in files)
            {
                if (limit is not null && produced >= limit)
                {
                    break;
                }

                using Stream readStream = File.OpenRead(filePath);
                using var reader = await ParquetReader.CreateAsync(readStream);
                var fields = reader.Schema.DataFields;

                for (var g = 0; g < reader.RowGroupCount && (limit is null || produced < limit); g++)
                {
                    using var groupReader = reader.OpenRowGroupReader(g);
                    var groupRows = groupReader.RowCount;
                    var groupStart = globalIndex;
                    var groupEnd = globalIndex + groupRows;

                    // Skip whole groups that end before the window starts or begin after it ends.
                    if (groupEnd <= offset || groupStart >= stop)
                    {
                        globalIndex = groupEnd;
                        continue;
                    }

                    // Decode this group's columns (columnar read), then walk only the rows in-window.
                    var groupData = new object?[fields.Length][];
                    for (var c = 0; c < fields.Length; c++)
                    {
                        var dataColumn = await groupReader.ReadColumnAsync(fields[c]);
                        var src = dataColumn.Data;
                        var col = new object?[src.Length];
                        Array.Copy(src, col, src.Length);
                        groupData[c] = col;
                    }

                    for (var i = 0; i < groupRows; i++)
                    {
                        var abs = groupStart + i;
                        if (abs < offset)
                        {
                            continue;
                        }

                        if (abs >= stop)
                        {
                            break;
                        }

                        var row = new object?[fields.Length];
                        for (var c = 0; c < fields.Length; c++)
                        {
                            row[c] = groupData[c][i];
                        }

                        if (buffer.Count == 0)
                        {
                            emittedStart = abs;
                        }

                        buffer.Add(row);
                        produced++;

                        if (buffer.Count >= batchSize)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            yield return new TableRowBatch(emittedStart, buffer);
                            buffer = new List<object?[]>(batchSize);
                        }
                    }

                    globalIndex = groupEnd;
                }
            }

            if (buffer.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new TableRowBatch(emittedStart, buffer);
            }
        }

        /// <summary>Public-facing name of the physical storage type for a CLR type (int/string/etc).</summary>
        internal static string MapClrTypeName(Type clrType) => MapClrType(clrType);
    }
}
