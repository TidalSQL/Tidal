namespace ParqBaseTests
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using ParqBaseLib;

    /// <summary>
    /// Verifies the streaming aggregate fast path (projection pushdown + no row materialization)
    /// produces results identical to the general materializing path, including NULL handling,
    /// integer-vs-decimal SUM typing, multi-aggregate queries, multi-part tables, and correct
    /// fall-back when a WHERE clause is present.
    /// </summary>
    [TestClass]
    public class AggregatePushdownTests
    {
        private string dbName = null!;
        private string dbPath = null!;

        [TestInitialize]
        public void TestInitialize()
        {
            this.dbName = "AggPush_" + Guid.NewGuid().ToString("N")[..8];
            var session = new ParqBase();
            Assert.IsTrue(session.ExecuteQuery($"create database {this.dbName}").Success);
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);
            this.dbPath = session.CurrentDatabasePath!;

            Assert.IsTrue(session.ExecuteQuery(
                "CREATE TABLE dbo.Sales (Id INT NOT NULL, Amount DECIMAL(10,2) NULL, Qty INT NULL);").Success);
            Assert.IsTrue(session.ExecuteQuery(
                "INSERT INTO Sales (Id, Amount, Qty) VALUES (1, 10.50, 2), (2, 20.00, 3), (3, NULL, NULL), (4, 5.25, 1);").Success);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try { Directory.Delete(this.dbPath, true); } catch { }
        }

        private ParqBase Session()
        {
            var session = new ParqBase();
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);
            return session;
        }

        private object? Scalar(string sql, string column)
        {
            var result = this.Session().ExecuteQuery(sql);
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(1, result.Rows.Count);
            return result.Rows[0][column];
        }

        [TestMethod]
        public void CountStar_CountsAllRows()
        {
            Assert.AreEqual(4, Convert.ToInt32(this.Scalar("SELECT COUNT(*) AS C FROM Sales;", "C")));
        }

        [TestMethod]
        public void CountColumn_IgnoresNulls()
        {
            Assert.AreEqual(3, Convert.ToInt32(this.Scalar("SELECT COUNT(Amount) AS C FROM Sales;", "C")));
        }

        [TestMethod]
        public void SumDecimal_SkipsNulls()
        {
            Assert.AreEqual(35.75m, Convert.ToDecimal(this.Scalar("SELECT SUM(Amount) AS S FROM Sales;", "S")));
        }

        [TestMethod]
        public void SumInteger_ReturnsIntegralType()
        {
            var value = this.Scalar("SELECT SUM(Qty) AS S FROM Sales;", "S");
            Assert.IsInstanceOfType(value, typeof(long));
            Assert.AreEqual(6L, value);
        }

        [TestMethod]
        public void Avg_DividesByNonNullCount()
        {
            var value = Convert.ToDecimal(this.Scalar("SELECT AVG(Amount) AS A FROM Sales;", "A"));
            Assert.AreEqual(Math.Round(35.75m / 3m, 6), Math.Round(value, 6));
        }

        [TestMethod]
        public void MinMax_SkipNulls()
        {
            Assert.AreEqual(5.25m, Convert.ToDecimal(this.Scalar("SELECT MIN(Amount) AS M FROM Sales;", "M")));
            Assert.AreEqual(20.00m, Convert.ToDecimal(this.Scalar("SELECT MAX(Amount) AS M FROM Sales;", "M")));
        }

        [TestMethod]
        public void MultipleAggregates_ComputedInOnePass()
        {
            var result = this.Session().ExecuteQuery(
                "SELECT COUNT(*) AS Cnt, SUM(Amount) AS Total, MIN(Qty) AS MinQ, MAX(Qty) AS MaxQ FROM Sales;");
            Assert.IsTrue(result.Success, result.Message);
            var row = result.Rows[0];
            Assert.AreEqual(4, Convert.ToInt32(row["Cnt"]));
            Assert.AreEqual(35.75m, Convert.ToDecimal(row["Total"]));
            Assert.AreEqual(1, Convert.ToInt32(row["MinQ"]));
            Assert.AreEqual(3, Convert.ToInt32(row["MaxQ"]));
        }

        [TestMethod]
        public void EmptyTable_MatchesSqlSemantics()
        {
            var session = this.Session();
            Assert.IsTrue(session.ExecuteQuery("CREATE TABLE dbo.Empty (V INT NULL);").Success);

            var result = session.ExecuteQuery("SELECT COUNT(*) AS C, SUM(V) AS S, MIN(V) AS Mn, AVG(V) AS A FROM Empty;");
            Assert.IsTrue(result.Success, result.Message);
            var row = result.Rows[0];
            Assert.AreEqual(0, Convert.ToInt32(row["C"]));
            Assert.IsNull(row["S"]);
            Assert.IsNull(row["Mn"]);
            Assert.IsNull(row["A"]);
        }

        [TestMethod]
        public void WhereClause_FallsBackAndFilters()
        {
            // A WHERE clause is not handled by the fast path; the general path must still be correct.
            var value = Convert.ToDecimal(this.Scalar("SELECT SUM(Amount) AS S FROM Sales WHERE Qty >= 2;", "S"));
            Assert.AreEqual(30.50m, value);
        }

        [TestMethod]
        public void GroupBy_StillUsesGeneralPath()
        {
            var result = this.Session().ExecuteQuery(
                "SELECT Qty, COUNT(*) AS C FROM Sales GROUP BY Qty ORDER BY Qty;");
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(4, result.Rows.Count); // Qty values: NULL, 1, 2, 3
        }

        [TestMethod]
        public void MultiPartTable_AggregatesAcrossParts()
        {
            // Build a 3-part directory table from copies of the single-file Sales table.
            var seed = Path.Combine(this.dbPath, "tables", "Sales.parquet");
            Assert.IsTrue(File.Exists(seed));
            var partsDir = Path.Combine(this.dbPath, "tables", "SalesParts");
            Directory.CreateDirectory(partsDir);
            foreach (var idx in new[] { 0, 1, 2 })
            {
                File.Copy(seed, Path.Combine(partsDir, $"part-{idx}.parquet"), overwrite: true);
            }

            var result = this.Session().ExecuteQuery(
                "SELECT COUNT(*) AS C, COUNT(Amount) AS Ca, SUM(Amount) AS S FROM SalesParts;");
            Assert.IsTrue(result.Success, result.Message);
            var row = result.Rows[0];
            Assert.AreEqual(12, Convert.ToInt32(row["C"]));        // 4 rows × 3 parts
            Assert.AreEqual(9, Convert.ToInt32(row["Ca"]));        // 3 non-null × 3 parts
            Assert.AreEqual(35.75m * 3, Convert.ToDecimal(row["S"]));
        }
    }
}
