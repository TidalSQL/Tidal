namespace TidalSqlTests
{
    using System;
    using System.IO;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    /// <summary>
    /// Isolates the entire test run in a throwaway data root so tests never touch — or depend on —
    /// the machine's real TidalSql catalog under <c>%ProgramData%\TidalSql</c>.
    ///
    /// <para>Server logins, roles, and databases are process-wide state persisted under the data root
    /// (see <see cref="TidalSqlLib.TidalSql"/>). Without isolation, tests that call
    /// <c>InitializeServer("admin")</c> and then sign in as <c>admin/admin</c> fail whenever the real
    /// catalog already contains an <c>admin</c> login whose password isn't literally "admin" — because
    /// <c>InitializeServer</c> only creates the default admin when none exists and never resets an
    /// existing one. Redirecting the data root to a fresh temp directory makes every run start from an
    /// empty catalog, so the tests are hermetic and repeatable regardless of the box's real logins.</para>
    ///
    /// <para>This must run before any test touches <see cref="TidalSqlLib.TidalSql"/>, because the data
    /// root is resolved once and cached. <c>[AssemblyInitialize]</c> is guaranteed to run before any
    /// test method or <c>[TestInitialize]</c>, and no test type reads the data root at type-load, so
    /// setting the environment variable here takes effect for the whole assembly.</para>
    /// </summary>
    [TestClass]
    public static class TestBootstrap
    {
        private const string DataRootVariable = "TIDALSQL_DATA_ROOT";

        private static string? dataRoot;

        [AssemblyInitialize]
        public static void Initialize(TestContext context)
        {
            dataRoot = Path.Combine(Path.GetTempPath(), "TidalSqlTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataRoot);
            Environment.SetEnvironmentVariable(DataRootVariable, dataRoot);
        }

        [AssemblyCleanup]
        public static void Cleanup()
        {
            try
            {
                if (dataRoot != null && Directory.Exists(dataRoot))
                {
                    Directory.Delete(dataRoot, recursive: true);
                }
            }
            catch
            {
                // Best-effort: a leftover temp directory must never fail the run.
            }
        }
    }
}
