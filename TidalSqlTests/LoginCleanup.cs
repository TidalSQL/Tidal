namespace TidalSqlTests
{
    using System;
    using System.Collections.Generic;
    using TidalSqlLib;

    /// <summary>
    /// Test utility that keeps the shared server login store clean. Server logins are process-wide
    /// (stored under system/logins.json), so any login a test creates would otherwise linger after the
    /// run. Tests snapshot the existing logins before creating any, then <see cref="DropCreatedSince"/>
    /// removes the difference in [TestCleanup] — which MSTest runs whether the test passed or failed.
    /// </summary>
    internal static class LoginCleanup
    {
        // Permanent logins that must never be dropped, even if they were created during a test run.
        private static readonly HashSet<string> Permanent =
            new(StringComparer.OrdinalIgnoreCase) { "admin", "doreamey", "sales_user" };

        /// <summary>Captures the set of server logins that exist before a test creates any.</summary>
        public static HashSet<string> Snapshot() => CurrentLogins();

        /// <summary>
        /// Drops every server login that exists now but was not present in <paramref name="before"/>,
        /// i.e. the logins the test created. Best-effort: failures are swallowed so cleanup never masks
        /// the test's own result. The permanent logins are always preserved.
        /// </summary>
        public static void DropCreatedSince(HashSet<string> before)
        {
            // A fresh session with no authenticated login acts as the implicit administrator, so it can
            // drop logins regardless of any impersonation the test left on its own session.
            var admin = new TidalSql();
            foreach (var name in CurrentLogins())
            {
                if (before.Contains(name) || Permanent.Contains(name))
                {
                    continue;
                }

                try
                {
                    // Note: this engine's parser does not support "DROP LOGIN IF EXISTS", so drop
                    // plainly. The name came from the live login list, so it is guaranteed to exist.
                    admin.ExecuteQuery($"DROP LOGIN [{name}];");
                }
                catch
                {
                    // Ignore — cleanup is best-effort and must not throw out of [TestCleanup].
                }
            }
        }

        private static HashSet<string> CurrentLogins()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var result = new TidalSql().ExecuteQuery(
                    "SELECT name FROM sys.server_principals WHERE type = 'SQL_LOGIN';");
                if (result.Success)
                {
                    foreach (var row in result.Rows)
                    {
                        if (row.TryGetValue("name", out var value) && value is string name)
                        {
                            set.Add(name);
                        }
                    }
                }
            }
            catch
            {
                // Ignore — an empty snapshot simply means nothing is dropped.
            }

            return set;
        }
    }
}
