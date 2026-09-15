namespace ParqBaseLib
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
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
    /// <para>Unlike <see cref="ReaderWriterLockSlim"/> this lock is NOT thread-affine: it is a
    /// counting monitor, so a lock acquired on one thread can be released on another. That is
    /// essential for the streaming reader, whose <c>async</c> row-group iterator resumes on
    /// arbitrary thread-pool threads between batches while still holding a shared read lock for a
    /// consistent snapshot.</para>
    ///
    /// <para>Reentrancy: a write operation that internally reads the same table (for example
    /// <c>INSERT ... SELECT FROM</c> the same table) does not deadlock. Write holds are tracked per
    /// thread; a nested read on a table this thread is already writing is granted immediately. The
    /// engine's nested reads are always synchronous and same-thread, so this tracking is safe.</para>
    ///
    /// <para>Scope/limitations: coordination is in-process only. It does NOT guard against a second
    /// OS process writing the same files; atomic replace still prevents torn reads in that case, but
    /// cross-process lost updates remain possible. Acquisition uses a timeout to surface a clear
    /// error instead of hanging on a rare lock-ordering cycle (e.g. two writers each reading the
    /// other's target table).</para>
    /// </summary>
    internal static class TableLock
    {
        private static readonly TimeSpan AcquireTimeout = TimeSpan.FromSeconds(30);

        private static readonly ConcurrentDictionary<string, LockState> Locks =
            new(StringComparer.OrdinalIgnoreCase);

        // Tables this thread is currently writing (path -> nesting depth). Used only to grant
        // reentrant access for the engine's synchronous, same-thread nested reads/writes.
        [ThreadStatic]
        private static Dictionary<string, int>? threadWrites;

        /// <summary>Acquires a shared read lock for the table; dispose to release.</summary>
        public static IDisposable Read(string filePath) => Acquire(filePath, write: false);

        /// <summary>Acquires an exclusive write lock for the table; dispose to release.</summary>
        public static IDisposable Write(string filePath) => Acquire(filePath, write: true);

        private static IDisposable Acquire(string filePath, bool write)
        {
            var key = NormalizeKey(filePath);
            var state = Locks.GetOrAdd(key, _ => new LockState());
            var writes = threadWrites ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var alreadyWriting = writes.TryGetValue(key, out var depth) && depth > 0;

            if (write)
            {
                if (alreadyWriting)
                {
                    writes[key] = depth + 1;
                    return new Scope(state, key, write: true, reentrant: true);
                }

                state.EnterWrite(AcquireTimeout);
                writes[key] = 1;
                return new Scope(state, key, write: true, reentrant: false);
            }

            // A read requested while this same thread already holds the write lock is granted
            // immediately (the writer is reading its own in-progress table); no global change.
            if (alreadyWriting)
            {
                return new Scope(state, key, write: false, reentrant: true);
            }

            state.EnterRead(AcquireTimeout);
            return new Scope(state, key, write: false, reentrant: false);
        }

        private static void ReleaseWrite(LockState state, string key)
        {
            var writes = threadWrites;
            if (writes != null && writes.TryGetValue(key, out var depth))
            {
                if (depth > 1)
                {
                    writes[key] = depth - 1;
                    return;
                }

                writes.Remove(key);
            }

            state.ExitWrite();
        }

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

        /// <summary>Counting reader/writer state for one table, guarded by its own monitor.</summary>
        private sealed class LockState
        {
            private readonly object sync = new();
            private int readers;
            private bool writer;

            public void EnterRead(TimeSpan timeout)
            {
                lock (this.sync)
                {
                    WaitFor(this.sync, () => !this.writer, timeout, "read");
                    this.readers++;
                }
            }

            public void ExitRead()
            {
                lock (this.sync)
                {
                    this.readers--;
                    if (this.readers == 0)
                    {
                        Monitor.PulseAll(this.sync);
                    }
                }
            }

            public void EnterWrite(TimeSpan timeout)
            {
                lock (this.sync)
                {
                    WaitFor(this.sync, () => !this.writer && this.readers == 0, timeout, "write");
                    this.writer = true;
                }
            }

            public void ExitWrite()
            {
                lock (this.sync)
                {
                    this.writer = false;
                    Monitor.PulseAll(this.sync);
                }
            }

            private static void WaitFor(object sync, Func<bool> condition, TimeSpan timeout, string kind)
            {
                var watch = Stopwatch.StartNew();
                while (!condition())
                {
                    var remaining = timeout - watch.Elapsed;
                    if (remaining <= TimeSpan.Zero || !Monitor.Wait(sync, remaining))
                    {
                        throw new TimeoutException(
                            $"Timed out acquiring a {kind} lock on the table after {timeout.TotalSeconds:N0}s. " +
                            "This usually means another operation is holding it, or two writers are contending for each other's tables.");
                    }
                }
            }
        }

        private sealed class Scope : IDisposable
        {
            private readonly LockState state;
            private readonly string key;
            private readonly bool write;
            private readonly bool reentrant;
            private bool released;

            public Scope(LockState state, string key, bool write, bool reentrant)
            {
                this.state = state;
                this.key = key;
                this.write = write;
                this.reentrant = reentrant;
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
                    ReleaseWrite(this.state, this.key);
                }
                else if (!this.reentrant)
                {
                    this.state.ExitRead();
                }
            }
        }
    }
}
