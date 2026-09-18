namespace ParqBaseTests
{
    using System.Linq;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using ParqBaseLib;

    /// <summary>
    /// Small-scale regression tests for the SQL features added for TPC-H support:
    /// BETWEEN, LIKE / NOT LIKE, NOT EXISTS, comma (implicit) joins with predicate
    /// pushdown, HAVING, and TOP applied to GROUP BY / aggregate results. Uses tiny
    /// synthetic tables so it never touches the multi-GB tpch data.
    /// </summary>
    [TestClass]
    public class TpchFeatureTests
    {
        private ParqBase db = null!;
        private string dbName = null!;
        private string dbPath = null!;
        private string originalDir = null!;

        [TestInitialize]
        public void TestInitialize()
        {
            this.originalDir = Directory.GetCurrentDirectory();
            this.dbName = "TpchFeat_" + Guid.NewGuid().ToString("N")[..8];

            this.db = new ParqBase();
            this.db.ExecuteStatement($"create database {this.dbName}");
            this.db.ExecuteStatement($"use {this.dbName}");
            this.dbPath = this.db.CurrentDatabasePath!;

            this.db.ExecuteStatement(
                "CREATE TABLE c (c_custkey INT, c_name NVARCHAR(50), c_mktsegment NVARCHAR(20));");
            this.db.ExecuteStatement(
                "INSERT INTO c (c_custkey, c_name, c_mktsegment) VALUES " +
                "(1, 'Customer#001', 'BUILDING'), (2, 'Customer#002', 'AUTOMOBILE'), (3, 'Customer#003', 'BUILDING');");

            this.db.ExecuteStatement(
                "CREATE TABLE o (o_orderkey INT, o_custkey INT, o_totalprice MONEY, o_orderdate DATE);");
            this.db.ExecuteStatement(
                "INSERT INTO o (o_orderkey, o_custkey, o_totalprice, o_orderdate) VALUES " +
                "(10, 1, 100, '1995-01-15'), (11, 1, 250, '1996-06-01'), (12, 2, 50, '1995-03-10'), (13, 3, 400, '1994-12-31');");
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try { Directory.SetCurrentDirectory(this.originalDir); } catch { }
            try { Directory.Delete(this.dbPath, true); } catch { }
        }

        [TestMethod]
        public void Between_FiltersInclusiveRange()
        {
            var result = this.db.ExecuteQuery(
                "SELECT o_orderkey FROM o WHERE o_totalprice BETWEEN 100 AND 250 ORDER BY o_orderkey;");
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(2, result.RowCount);
            Assert.AreEqual(10, (int)result.Rows[0]["o_orderkey"]!);
            Assert.AreEqual(11, (int)result.Rows[1]["o_orderkey"]!);
        }

        [TestMethod]
        public void NotBetween_ExcludesRange()
        {
            var result = this.db.ExecuteQuery(
                "SELECT o_orderkey FROM o WHERE o_totalprice NOT BETWEEN 100 AND 250 ORDER BY o_orderkey;");
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(2, result.RowCount);
            Assert.AreEqual(12, (int)result.Rows[0]["o_orderkey"]!);
            Assert.AreEqual(13, (int)result.Rows[1]["o_orderkey"]!);
        }

        [TestMethod]
        public void Between_OnDate_FiltersByYearBoundaries()
        {
            var result = this.db.ExecuteQuery(
                "SELECT o_orderkey FROM o WHERE o_orderdate BETWEEN '1995-01-01' AND '1995-12-31' ORDER BY o_orderkey;");
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(2, result.RowCount);
        }

        [TestMethod]
        public void Like_PrefixWildcard_Matches()
        {
            var result = this.db.ExecuteQuery(
                "SELECT c_custkey FROM c WHERE c_mktsegment LIKE 'BUILD%' ORDER BY c_custkey;");
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(2, result.RowCount);
        }

        [TestMethod]
        public void NotLike_ExcludesMatches()
        {
            var result = this.db.ExecuteQuery(
                "SELECT c_custkey FROM c WHERE c_mktsegment NOT LIKE 'BUILD%';");
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(1, result.RowCount);
            Assert.AreEqual(2, (int)result.Rows[0]["c_custkey"]!);
        }

        [TestMethod]
        public void Like_SingleCharWildcard_Matches()
        {
            var result = this.db.ExecuteQuery(
                "SELECT c_custkey FROM c WHERE c_name LIKE 'Customer#00_' ORDER BY c_custkey;");
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(3, result.RowCount);
        }

        [TestMethod]
        public void NotExists_FiltersCorrelatedRows()
        {
            // Customers with no orders: customer 2 has one order, customers 1 and 3 have orders.
            // Insert a customer with no orders to prove NOT EXISTS excludes those with orders.
            this.db.ExecuteStatement(
                "INSERT INTO c (c_custkey, c_name, c_mktsegment) VALUES (4, 'Customer#004', 'MACHINERY');");

            var result = this.db.ExecuteQuery(
                "SELECT c_custkey FROM c WHERE NOT EXISTS " +
                "(SELECT * FROM o WHERE o.o_custkey = c.c_custkey) ORDER BY c_custkey;");
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(1, result.RowCount);
            Assert.AreEqual(4, (int)result.Rows[0]["c_custkey"]!);
        }

        [TestMethod]
        public void ImplicitJoin_WithPushdown_ProducesMatchedRows()
        {
            var result = this.db.ExecuteQuery(
                "SELECT c_name, o_orderkey FROM c, o " +
                "WHERE c_custkey = o_custkey AND c_mktsegment = 'BUILDING' ORDER BY o_orderkey;");
            Assert.IsTrue(result.Success, result.Message);

            // Customer 1 (BUILDING) has orders 10, 11; Customer 3 (BUILDING) has order 13.
            Assert.AreEqual(3, result.RowCount);
            CollectionAssert.AreEqual(
                new[] { 10, 11, 13 },
                result.Rows.Select(r => (int)r["o_orderkey"]!).ToArray());
        }

        [TestMethod]
        public void GroupBy_Having_FiltersAggregatedGroups()
        {
            var result = this.db.ExecuteQuery(
                "SELECT o_custkey, SUM(o_totalprice) AS total FROM o " +
                "GROUP BY o_custkey HAVING SUM(o_totalprice) > 100 ORDER BY o_custkey;");
            Assert.IsTrue(result.Success, result.Message);

            // Totals: cust1=350, cust2=50, cust3=400. Only 1 and 3 exceed 100.
            Assert.AreEqual(2, result.RowCount);
            Assert.AreEqual(1, (int)result.Rows[0]["o_custkey"]!);
            Assert.AreEqual(3, (int)result.Rows[1]["o_custkey"]!);
        }

        [TestMethod]
        public void GroupBy_OrderBy_Top_LimitsAndOrdersAggregateResult()
        {
            var result = this.db.ExecuteQuery(
                "SELECT TOP 1 o_custkey, SUM(o_totalprice) AS total FROM o " +
                "GROUP BY o_custkey ORDER BY total DESC;");
            Assert.IsTrue(result.Success, result.Message);

            // Highest total is customer 3 (400); TOP 1 must return exactly that one group.
            Assert.AreEqual(1, result.RowCount);
            Assert.AreEqual(3, (int)result.Rows[0]["o_custkey"]!);
        }

        [TestMethod]
        public void Aggregate_RatioOfSums_EvaluatesArithmeticOverGroup()
        {
            // Whole-result aggregate whose SELECT is arithmetic over two SUMs (TPC-H Q14/Q8 shape).
            // Sum of orders 100 and 250 (custkey 1) over total of all four orders (800): 350/800*100 = 43.75.
            var result = this.db.ExecuteQuery(
                "SELECT 100.0 * SUM(CASE WHEN o_custkey = 1 THEN o_totalprice ELSE 0 END) / SUM(o_totalprice) " +
                "AS pct FROM o;");
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(1, result.RowCount);
            Assert.AreEqual(43.75m, (decimal)result.Rows[0]["pct"]!);
        }
    }
}

