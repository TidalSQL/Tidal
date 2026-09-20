namespace TidalSqlTests
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using TidalSqlLib;

    [TestClass]
    public class ConcurrencyTests
    {
        private string dbName = null!;
        private string dbPath = null!;

        [TestInitialize]
        public void TestInitialize()
        {
            this.dbName = "ConcTest_" + Guid.NewGuid().ToString("N")[..8];
            var session = new TidalSql();
            Assert.IsTrue(session.ExecuteQuery($"create database {this.dbName}").Success);
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);
            this.dbPath = session.CurrentDatabasePath!;
            Assert.IsTrue(session.ExecuteQuery(
                "CREATE TABLE dbo.Counter (Id INT IDENTITY(1,1) NOT NULL, Val INT NOT NULL);").Success);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try { Directory.Delete(this.dbPath, true); } catch { }
        }

        [TestMethod]
        public void ConcurrentInserts_FromSeparateSessions_DoNotLoseRows()
        {
            const int writers = 8;
            const int perWriter = 25;

            // Each task uses its own TidalSql instance (as a web request would). Without per-table
            // write locking these read-modify-write inserts would clobber each other.
            Parallel.For(0, writers, w =>
            {
                var session = new TidalSql();
                Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);
                for (var i = 0; i < perWriter; i++)
                {
                    var r = session.ExecuteQuery($"INSERT INTO Counter (Val) VALUES ({w * 1000 + i});");
                    Assert.IsTrue(r.Success, r.Message);
                }
            });

            var reader = new TidalSql();
            Assert.IsTrue(reader.ExecuteQuery($"use {this.dbName}").Success);
            var count = reader.ExecuteQuery("SELECT Val FROM Counter;");
            Assert.IsTrue(count.Success, count.Message);
            Assert.AreEqual(writers * perWriter, count.RowCount);

            // Identity values must be unique (no two inserts reused the same seed).
            var ids = reader.ExecuteQuery("SELECT Id FROM Counter;");
            var idValues = ids.Rows.Select(row => Convert.ToInt32(row["Id"])).ToList();
            Assert.AreEqual(writers * perWriter, idValues.Distinct().Count(), "Duplicate identity values were assigned.");
        }

        [TestMethod]
        public void ConcurrentReadsAndWrites_ReadsNeverSeeTornData()
        {
            var writer = Task.Run(() =>
            {
                var session = new TidalSql();
                session.ExecuteQuery($"use {this.dbName}");
                for (var i = 0; i < 60; i++)
                {
                    session.ExecuteQuery($"INSERT INTO Counter (Val) VALUES ({i});");
                }
            });

            var readers = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
            {
                var session = new TidalSql();
                session.ExecuteQuery($"use {this.dbName}");
                for (var i = 0; i < 40; i++)
                {
                    var r = session.ExecuteQuery("SELECT Val FROM Counter;");
                    // A read must always succeed and never observe a half-written/corrupt file.
                    Assert.IsTrue(r.Success, r.Message);
                }
            })).ToArray();

            Task.WaitAll(readers.Append(writer).ToArray());

            var reader = new TidalSql();
            reader.ExecuteQuery($"use {this.dbName}");
            var final = reader.ExecuteQuery("SELECT Val FROM Counter;");
            Assert.AreEqual(60, final.RowCount);
        }
    }
}
