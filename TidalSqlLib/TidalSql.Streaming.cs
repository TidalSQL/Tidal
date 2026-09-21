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
    ///
    /// <para>The result pins a consistent snapshot of the underlying Parquet file(s) at open time, so
    /// <see cref="TotalRows"/> and every streamed row come from the same version even if a writer
    /// atomically swaps the table in mid-read. The pinned file handles are released when the
    /// <see cref="Batches"/> enumeration completes (or is cancelled); callers that read the metadata
    /// but never enumerate should <see cref="Dispose"/> the result to release them.</para>
    /// </summary>
    public sealed class TableStreamResult : IDisposable
    {
        public required IReadOnlyList<TableStreamColumn> Columns { get; init; }

        public required long TotalRows { get; init; }

        public required IAsyncEnumerable<TableRowBatch> Batches { get; init; }

        /// <summary>The pinned file snapshot backing this read; disposed with the result (idempotent).</summary>
        internal IDisposable? Snapshot { get; init; }

        public void Dispose() => this.Snapshot?.Dispose();
    }

    /// <summary>
    /// A pinned, point-in-time snapshot of a (possibly multi-part) table's Parquet files. The files are
    /// opened once — under a shared read lock, with <see cref="FileShare.Delete"/> so a writer's atomic
    /// <c>File.Move</c> swap still succeeds — and the open handles keep serving the original bytes even
    /// after the on-disk file is replaced. Both the row count and the streamed rows are derived from
    /// these same handles, giving true snapshot isolation without holding the table lock for the whole
    /// (consumer-paced) drain. Disposal is idempotent and safe to call from the enumerator and the
    /// owning <see cref="TableStreamResult"/>.
    /// </summary>
    internal sealed class PinnedTableSnapshot : IDisposable
    {
        private readonly List<Stream> streams;
        private int disposed;

        private PinnedTableSnapshot(
            List<Stream> streams,
            List<ParquetReader> readers,
            List<TableStreamColumn> columns,
            long totalRows)
        {
            this.streams = streams;
            this.Readers = readers;
            this.Columns = columns;
            this.TotalRows = totalRows;
        }

        public IReadOnlyList<ParquetReader> Readers { get; }

        public IReadOnlyList<TableStreamColumn> Columns { get; }

        public long TotalRows { get; }

        /// <summary>
        /// Opens and pins every part of a table under a single read lock, resolving its columns and
        /// total row count from the pinned handles so they can never disagree with what is streamed.
        /// </summary>
        public static PinnedTableSnapshot Open(string lockKey, IReadOnlyList<string> files)
        {
            var streams = new List<Stream>();
            var readers = new List<ParquetReader>();
            try
            {
                // Open all parts under one read lock so we capture a single, non-torn version. Sharing
                // Delete lets a concurrent writer replace the file on disk while our handle keeps the
                // original bytes alive for the life of this snapshot.
                using (TableLock.Read(lockKey))
                {
                    foreach (var filePath in files)
                    {
                        var stream = new FileStream(
                            filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                        streams.Add(stream);
                        readers.Add(ParquetReader.CreateAsync(stream, leaveStreamOpen: true).GetAwaiter().GetResult());
                    }
                }

                var columns = new List<TableStreamColumn>();
                foreach (var field in readers[0].Schema.DataFields)
                {
                    columns.Add(new TableStreamColumn(
                        field.Name, TidalSqlStatementVisitor.MapClrTypeName(field.ClrType)));
                }

                long totalRows = 0;
                foreach (var reader in readers)
                {
                    for (var g = 0; g < reader.RowGroupCount; g++)
                    {
                        using var groupReader = reader.OpenRowGroupReader(g);
                        totalRows += groupReader.RowCount;
                    }
                }

                return new PinnedTableSnapshot(streams, readers, columns, totalRows);
            }
            catch
            {
                foreach (var reader in readers)
                {
                    reader.Dispose();
                }

                foreach (var stream in streams)
                {
                    stream.Dispose();
                }

                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            {
                return;
            }

            foreach (var reader in this.Readers)
            {
                try { reader.Dispose(); } catch { /* best-effort */ }
            }

            foreach (var stream in this.streams)
            {
                try { stream.Dispose(); } catch { /* best-effort */ }
            }
        }
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

            // Pin a consistent snapshot of the table's file(s) up front: columns and the total row
            // count are resolved from the pinned handles (under a read lock so they cannot race a
            // writer's atomic swap), and the same handles feed the lazy stream — so TotalRows and the
            // streamed rows are guaranteed to come from one version. For a multi-part table the schema
            // comes from the first part; the row count sums parts.
            var snapshot = PinnedTableSnapshot.Open(source.LockKey, source.Files);

            return new TableStreamResult
            {
                Columns = snapshot.Columns,
                TotalRows = snapshot.TotalRows,
                Batches = TidalSqlStatementVisitor.StreamPinnedAsync(snapshot, batchSize, offset, limit),
                Snapshot = snapshot,
            };
        }
    }

    internal partial class TidalSqlStatementVisitor
    {
        /// <summary>
        /// Streams a pinned table snapshot a Parquet row group at a time. Only one row group is decoded
        /// into memory at once, so peak memory is bounded by the row-group size regardless of table
        /// size. The rows come from handles pinned at open (see <see cref="PinnedTableSnapshot"/>), so
        /// the scan sees a single consistent version without holding the table lock for the whole
        /// (consumer-paced) drain. A table may span several part files; they are read in order and their
        /// row groups treated as one continuous sequence. Row groups that fall entirely outside the
        /// requested [offset, offset+limit) window are skipped without decoding. The snapshot is
        /// released when the enumeration completes or is cancelled.
        /// </summary>
        internal static async IAsyncEnumerable<TableRowBatch> StreamPinnedAsync(
            PinnedTableSnapshot snapshot,
            int batchSize,
            long offset,
            long? limit,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                long produced = 0;
                long globalIndex = 0;         // absolute row index of the next row to consider
                long emittedStart = offset;   // StartIndex of the batch currently being built
                var buffer = new List<object?[]>(batchSize);
                var stop = limit is { } lim ? offset + lim : long.MaxValue;

                foreach (var reader in snapshot.Readers)
                {
                    if (limit is not null && produced >= limit)
                    {
                        break;
                    }

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
            finally
            {
                snapshot.Dispose();
            }
        }

        /// <summary>Public-facing name of the physical storage type for a CLR type (int/string/etc).</summary>
        internal static string MapClrTypeName(Type clrType) => MapClrType(clrType);
    }
}
