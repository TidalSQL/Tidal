namespace TidalSqlTests
{
    using System;
    using System.IO;
    using System.Linq;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using TidalSqlLib;

    /// <summary>
    /// End-to-end regression test for the demo script <c>TidalScripts/build.sql</c>, which exercises
    /// IDENTITY, DEFAULT, computed persisted columns, CTEs, ROW_NUMBER, sys.all_objects, INSERT ...
    /// SELECT, aggregates + GROUP BY, UPDATE ... FROM ... JOIN and UNION ALL.
    /// </summary>
    [TestClass]
    public class BuildScriptTests
    {
        private string repoRoot = null!;

        [TestInitialize]
        public void TestInitialize()
        {
            this.repoRoot = FindRepoRoot();
            this.DeleteOrdersDb();
        }

        [TestCleanup]
        public void TestCleanup() => this.DeleteOrdersDb();

        [TestMethod]
        public void BuildScript_RunsEndToEnd_WithExpectedRowCounts()
        {
            var scriptPath = Path.Combine(this.repoRoot, "TidalScripts", "build.sql");
            Assert.IsTrue(File.Exists(scriptPath), $"Script not found: {scriptPath}");

            var db = new TidalSql();
            var results = db.ExecuteScriptFile(scriptPath);

            var failures = results.Where(r => !r.Success).Select(r => r.Message).ToList();
            Assert.AreEqual(0, failures.Count, "Failed statements: " + string.Join(" | ", failures));

            db.ExecuteQuery("USE OrdersDB;");
            Assert.AreEqual(50, this.Count(db, "Customers"));
            Assert.AreEqual(1000, this.Count(db, "Products"));
            Assert.AreEqual(10000, this.Count(db, "Invoices"));
            Assert.AreEqual(30000, this.Count(db, "InvoiceItems"));

            // The UPDATE ... FROM populated invoice totals from the line-item subtotals.
            var totals = db.ExecuteQuery("SELECT TotalAmount FROM Invoices WHERE InvoiceId = 1;");
            Assert.IsTrue(totals.Success, totals.Message);
            Assert.AreEqual(1, totals.RowCount);
            Assert.IsTrue(Convert.ToDecimal(totals.Rows[0]["TotalAmount"]) > 0m, "TotalAmount should be > 0 after UPDATE.");
        }

        private int Count(TidalSql db, string table)
        {
            var result = db.ExecuteQuery($"SELECT COUNT(*) AS N FROM {table};");
            Assert.IsTrue(result.Success, result.Message);
            return Convert.ToInt32(result.Rows[0]["N"]);
        }

        private void DeleteOrdersDb()
        {
            var dbDir = Path.Combine(TidalSqlLib.TidalSql.DatabasesRoot, "OrdersDB");
            if (Directory.Exists(dbDir))
            {
                Directory.Delete(dbDir, true);
            }
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TidalSql.sln")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root (TidalSql.sln).");
        }
    }
}
