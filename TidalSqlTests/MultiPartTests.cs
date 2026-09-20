namespace TidalSqlTests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using TidalSqlLib;

    /// <summary>
    /// Tests for multi-part (directory-of-parts) tables: a table stored as a directory of
    /// <c>part-0.parquet</c> … <c>part-n.parquet</c> files that together form one logical table.
    /// </summary>
    [TestClass]
    public class MultiPartTests
    {
        private string dbName = null!;
        private string dbPath = null!;

        [TestInitialize]
        public void TestInitialize()
        {
            this.dbName = "MultiPart_" + Guid.NewGuid().ToString("N")[..8];
            var session = new TidalSql();
            Assert.IsTrue(session.ExecuteQuery($"create database {this.dbName}").Success);
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);
            this.dbPath = session.CurrentDatabasePath!;
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try { Directory.Delete(this.dbPath, true); } catch { }
        }

        /// <summary>
        /// Builds a single-file table, then makes a directory-shaped table from copies of that file
        /// so we get a deterministic, self-contained multi-part table (rows = parts × source rows).
        /// </summary>
        private (string tableName, int totalRows) CreateThreePartTable(string container)
        {
            var session = new TidalSql();
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);
            Assert.IsTrue(session.ExecuteQuery(
                "CREATE TABLE dbo.Seed (Id INT NOT NULL, N INT NOT NULL);").Success);
            for (var i = 1; i <= 10; i++)
            {
                Assert.IsTrue(session.ExecuteQuery($"INSERT INTO Seed (Id, N) VALUES ({i}, {i * 100});").Success);
            }

            var seedFile = Path.Combine(this.dbPath, "tables", "Seed.parquet");
            Assert.IsTrue(File.Exists(seedFile));

            var partsDir = Path.Combine(this.dbPath, container, "Parts");
            Directory.CreateDirectory(partsDir);
            foreach (var idx in new[] { 0, 1, 2 })
            {
                File.Copy(seedFile, Path.Combine(partsDir, $"part-{idx}.parquet"), overwrite: true);
            }

            return ("Parts", 30);
        }

        [TestMethod]
        public void Select_ReadsAllPartsAsOneTable()
        {
            var (tableName, totalRows) = this.CreateThreePartTable("tables");

            var session = new TidalSql();
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);

            var result = session.ExecuteQuery($"SELECT * FROM {tableName};");
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(totalRows, result.Rows.Count);
        }

        [TestMethod]
        public void Count_SumsAcrossParts()
        {
            var (tableName, totalRows) = this.CreateThreePartTable("tables");

            var session = new TidalSql();
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);

            var result = session.ExecuteQuery($"SELECT COUNT(*) AS C FROM {tableName};");
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual((long)totalRows, Convert.ToInt64(result.Rows[0]["C"]));
        }

        [TestMethod]
        public void ParquetContainer_IsDiscoveredAsTable()
        {
            var (tableName, _) = this.CreateThreePartTable("parquet");

            var session = new TidalSql();
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);

            var listing = session.ExecuteQuery("SELECT name FROM sys.tables;");
            Assert.IsTrue(listing.Success, listing.Message);
            var names = listing.Rows.Select(r => (string)r["name"]!).ToList();
            CollectionAssert.Contains(names, tableName);

            // And it is queryable from the parquet/ container too.
            var read = session.ExecuteQuery($"SELECT * FROM {tableName};");
            Assert.IsTrue(read.Success, read.Message);
            Assert.AreEqual(30, read.Rows.Count);
        }

        [TestMethod]
        public void GetTableInfo_ReportsAggregateRowCount()
        {
            var (tableName, totalRows) = this.CreateThreePartTable("tables");

            var session = new TidalSql();
            var info = session.GetTableInfo(this.dbName, tableName);
            Assert.AreEqual(totalRows, info.RowCount);
            Assert.AreEqual(2, info.Columns.Count);
        }

        [TestMethod]
        public async Task Stream_ReadsEveryRowAcrossParts()
        {
            var (tableName, totalRows) = this.CreateThreePartTable("tables");

            var session = new TidalSql();
            var stream = session.OpenTableStream(this.dbName, tableName, batchSize: 7);
            Assert.AreEqual(totalRows, stream.TotalRows);

            long streamed = 0;
            await foreach (var batch in stream.Batches)
            {
                streamed += batch.Rows.Count;
            }

            Assert.AreEqual(totalRows, streamed);
        }

        [TestMethod]
        public void Insert_IntoMultiPartTable_IsRejected()
        {
            var (tableName, _) = this.CreateThreePartTable("tables");

            var session = new TidalSql();
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);

            var result = session.ExecuteQuery($"INSERT INTO {tableName} (Id, N) VALUES (999, 999);");
            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.Message, "multi-part");
        }

        [TestMethod]
        public void SingleFileTable_StillWorks()
        {
            var session = new TidalSql();
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);
            Assert.IsTrue(session.ExecuteQuery(
                "CREATE TABLE dbo.Solo (Id INT NOT NULL);").Success);
            Assert.IsTrue(session.ExecuteQuery("INSERT INTO Solo (Id) VALUES (1),(2),(3);").Success);

            var result = session.ExecuteQuery("SELECT * FROM Solo;");
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(3, result.Rows.Count);
        }
    }

    /// <summary>
    /// Integration tests against the bundled TPC-H data set (a real multi-part layout under
    /// <c>databases/tpch/parquet/&lt;table&gt;/part-*.parquet</c>). Skipped when the data is absent.
    /// </summary>
    [TestClass]
    public class TpchMultiPartTests
    {
        private static bool TpchAvailable(out string region)
        {
            region = Path.Combine(TidalSqlLib.TidalSql.DatabasesRoot, "tpch", "parquet", "region");
            return Directory.Exists(region) && Directory.GetFiles(region, "part-*.parquet").Length > 0;
        }

        [TestMethod]
        public void Tpch_Region_ReadsAcrossParts()
        {
            if (!TpchAvailable(out _))
            {
                Assert.Inconclusive("TPC-H data set not present.");
                return;
            }

            var session = new TidalSql();
            Assert.IsTrue(session.ExecuteQuery("use tpch").Success);

            var count = session.ExecuteQuery("SELECT COUNT(*) AS C FROM region;");
            Assert.IsTrue(count.Success, count.Message);
            var rowCount = Convert.ToInt64(count.Rows[0]["C"]);
            Assert.IsTrue(rowCount > 0, "region should have rows");

            var info = session.GetTableInfo("tpch", "region");
            Assert.AreEqual(rowCount, info.RowCount);

            var full = session.ExecuteQuery("SELECT * FROM region;");
            Assert.IsTrue(full.Success, full.Message);
            Assert.AreEqual((int)rowCount, full.Rows.Count);
        }

        [TestMethod]
        public void Tpch_Tables_AppearInListing()
        {
            if (!TpchAvailable(out _))
            {
                Assert.Inconclusive("TPC-H data set not present.");
                return;
            }

            var session = new TidalSql();
            Assert.IsTrue(session.ExecuteQuery("use tpch").Success);

            var listing = session.ExecuteQuery("SELECT name FROM sys.tables;");
            Assert.IsTrue(listing.Success, listing.Message);
            var names = listing.Rows.Select(r => ((string)r["name"]!).ToLowerInvariant()).ToList();
            CollectionAssert.Contains(names, "region");
            CollectionAssert.Contains(names, "nation");
        }
    }
}
