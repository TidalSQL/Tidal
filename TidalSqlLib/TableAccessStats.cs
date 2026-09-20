namespace TidalSqlLib
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text.Json;

    /// <summary>
    /// Best-effort per-table usage statistics that are not part of the Parquet data itself — most
    /// importantly the time a table was last read by a query. Stats are persisted as a small JSON
    /// map (<c>.access-stats.json</c>) in the database directory, keyed by <c>schema.table</c>. All
    /// operations are guarded so they never fail a query: if the file cannot be read or written the
    /// statistic is simply skipped.
    /// </summary>
    internal static class TableAccessStats
    {
        private const string FileName = ".access-stats.json";

        // One lock object per database directory so concurrent read-modify-write of the stats file
        // (from separate sessions) does not corrupt it.
        private static readonly ConcurrentDictionary<string, object> Gates =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Records that (schema, table) was just queried, stamping the current UTC time.</summary>
        internal static void RecordQuery(string databasePath, string schema, string table)
        {
            try
            {
                var path = Path.Combine(databasePath, FileName);
                lock (GateFor(databasePath))
                {
                    var map = Read(path);
                    map[Key(schema, table)] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    File.WriteAllText(path, JsonSerializer.Serialize(map));
                }
            }
            catch
            {
                // Usage statistics are advisory; never let a failure here break the actual query.
            }
        }

        /// <summary>Returns the last time (schema, table) was queried, or null if never recorded.</summary>
        internal static DateTime? GetLastQuery(string databasePath, string schema, string table)
        {
            try
            {
                var path = Path.Combine(databasePath, FileName);
                lock (GateFor(databasePath))
                {
                    var map = Read(path);
                    if (map.TryGetValue(Key(schema, table), out var iso) &&
                        DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                    {
                        return dt;
                    }
                }
            }
            catch
            {
                // Advisory only.
            }

            return null;
        }

        private static object GateFor(string databasePath) =>
            Gates.GetOrAdd(databasePath, _ => new object());

        private static string Key(string schema, string table) =>
            $"{schema}.{table}".ToLowerInvariant();

        private static Dictionary<string, string> Read(string path)
        {
            if (!File.Exists(path))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
