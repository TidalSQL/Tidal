namespace ParqBaseLib
{
    using System;
    using System.Collections.Concurrent;
    using System.IO;
    using System.Threading;

    /// <summary>
    /// Process-wide reader/writer coordination for on-disk tables, keyed by the table's full file
    /// path. Because <see cref="ParqBase"/> is registered transient (one instance per request) and
    /// all instances share the same data folder, a per-instance lock is not enough: this static
    /// registry lets concurrent callers safely share the files.
    ///
    /// <para>Semantics: many concurrent readers, a single exclusive writer per table. Writers hold
    /// the lock across the whole read-modify-write (load -> mutate -> atomic replace) so concurrent
    /// writers cannot lose each other's updates, and readers never observe a half-written file.</para>
    ///
    /// <para>Recursion is supported so a write operation that internally reads the same table (for
    /// example <c>INSERT ... SELECT FROM</c> the same table) does not deadlock against itself.</para>
    ///
    /// <para>Scope/limitations: coordination is in-process only. It does NOT guard against a second
    /// OS process (for example the console and the web API running at once) writing the same files;
    /// atomic replace still prevents torn reads in that case, but cross-process lost updates remain
    /// possible. Acquisition uses a timeout to surface a clear error instead of hanging on the rare
    /// lock-ordering cycle (e.g. two writers each reading the other's target table).</para>
    /// </summary>
    internal static class TableLock
    {
        private static readonly TimeSpan AcquireTimeout = TimeSpan.FromSeconds(30);

        private static readonly ConcurrentDictionary<string, ReaderWriterLockSlim> Locks =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Acquires a shared read lock for the table; dispose to release.</summary>
        public static IDisposable Read(string filePath) => new Scope(For(filePath), write: false);

        /// <summary>Acquires an exclusive write lock for the table; dispose to release.</summary>
        public static IDisposable Write(string filePath) => new Scope(For(filePath), write: true);

        private static ReaderWriterLockSlim For(string filePath) =>
            Locks.GetOrAdd(NormalizeKey(filePath), _ => new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion));

        private static string NormalizeKey(string filePath)
        {
            try
            {
                return Path.GetFullPath(filePath);
            }
            catch
            {
                return filePath;
            }
        }

        private sealed class Scope : IDisposable
        {
            private readonly ReaderWriterLockSlim gate;
            private readonly bool write;
            private bool released;

            public Scope(ReaderWriterLockSlim gate, bool write)
            {
                this.gate = gate;
                this.write = write;

                var acquired = write
                    ? gate.TryEnterWriteLock(AcquireTimeout)
                    : gate.TryEnterReadLock(AcquireTimeout);

                if (!acquired)
                {
                    throw new TimeoutException(
                        $"Timed out acquiring a {(write ? "write" : "read")} lock on the table after {AcquireTimeout.TotalSeconds:N0}s. " +
                        "This usually means another operation is holding it, or two writers are contending for each other's tables.");
                }
            }

            public void Dispose()
            {
                if (this.released)
                {
                    return;
                }

                this.released = true;
                if (this.write)
                {
                    this.gate.ExitWriteLock();
                }
                else
                {
                    this.gate.ExitReadLock();
                }
            }
        }
    }
}
