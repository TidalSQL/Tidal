namespace TidalSqlTests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using TidalSqlLib;

    [TestClass]
    public class StreamingTests
    {
        private string dbName = null!;
        private string dbPath = null!;

        [TestInitialize]
        public void TestInitialize()
        {
            this.dbName = "StreamTest_" + Guid.NewGuid().ToString("N")[..8];
            var session = new TidalSql();
            Assert.IsTrue(session.ExecuteQuery($"create database {this.dbName}").Success);
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);
            this.dbPath = session.CurrentDatabasePath!;
        }

        [TestCleanup]
        public void TestCleanup()
        {
            TidalSqlStatementVisitor.RowGroupSize = 50_000;
            try { Directory.Delete(this.dbPath, true); } catch { }
        }

        private void CreateNums(TidalSql session, int count, int chunk = 500)
        {
            Assert.IsTrue(session.ExecuteQuery(
                "CREATE TABLE dbo.Nums (Id INT IDENTITY(1,1) NOT NULL, N INT NOT NULL);").Success);
            InsertNums(session, 0, count, chunk);
        }

        private static void InsertNums(TidalSql session, int startValue, int count, int chunk = 500)
        {
            for (var offset = 0; offset < count; offset += chunk)
            {
                var take = Math.Min(chunk, count - offset);
                var tuples = string.Join(",", Enumerable.Range(startValue + offset, take).Select(n => $"({n})"));
                var r = session.ExecuteQuery($"INSERT INTO Nums (N) VALUES {tuples};");
                Assert.IsTrue(r.Success, r.Message);
            }
        }

        private static async Task<List<TableRowBatch>> DrainAsync(TableStreamResult stream)
        {
            var batches = new List<TableRowBatch>();
            await foreach (var batch in stream.Batches)
            {
                batches.Add(batch);
            }

            return batches;
        }

        private static List<int> AllN(IEnumerable<TableRowBatch> batches, int nColumnIndex)
        {
            return batches.SelectMany(b => b.Rows).Select(r => Convert.ToInt32(r[nColumnIndex])).ToList();
        }

        [TestMethod]
        public async Task Stream_ReturnsAllRowsInOrder_InBatches()
        {
            var session = new TidalSql();
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);
            this.CreateNums(session, 120);

            var reader = new TidalSql();
            var stream = reader.OpenTableStream(this.dbName, "Nums", batchSize: 50);

            Assert.AreEqual(120, stream.TotalRows);
            CollectionAssert.AreEqual(new[] { "Id", "N" }, stream.Columns.Select(c => c.Name).ToArray());

            var batches = await DrainAsync(stream);
            CollectionAssert.AreEqual(new[] { 50, 50, 20 }, batches.Select(b => b.Rows.Count).ToArray());
            CollectionAssert.AreEqual(new long[] { 0, 50, 100 }, batches.Select(b => b.StartIndex).ToArray());

            var nIndex = stream.Columns.ToList().FindIndex(c => c.Name == "N");
            CollectionAssert.AreEqual(Enumerable.Range(0, 120).ToArray(), AllN(batches, nIndex).ToArray());
        }

        [TestMethod]
        public async Task Stream_HonorsOffsetAndLimit()
        {
            var session = new TidalSql();
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);
            this.CreateNums(session, 100);

            var reader = new TidalSql();
            var stream = reader.OpenTableStream(this.dbName, "Nums", batchSize: 1000, offset: 10, limit: 25);
            var batches = await DrainAsync(stream);

            var nIndex = stream.Columns.ToList().FindIndex(c => c.Name == "N");
            var values = AllN(batches, nIndex);
            Assert.AreEqual(25, values.Count);
            Assert.AreEqual(10, values.First());
            Assert.AreEqual(34, values.Last());
            Assert.AreEqual(10, batches.First().StartIndex);
        }

        [TestMethod]
        public async Task Stream_EmptyTable_YieldsNoBatchesButKnowsColumns()
        {
            var session = new TidalSql();
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);
            Assert.IsTrue(session.ExecuteQuery(
                "CREATE TABLE dbo.Nums (Id INT IDENTITY(1,1) NOT NULL, N INT NOT NULL);").Success);

            var reader = new TidalSql();
            var stream = reader.OpenTableStream(this.dbName, "Nums");
            Assert.AreEqual(0, stream.TotalRows);
            Assert.AreEqual(2, stream.Columns.Count);

            var batches = await DrainAsync(stream);
            Assert.AreEqual(0, batches.Count);
        }

        [TestMethod]
        public async Task Stream_AcrossMultipleRowGroups_IsCorrect_IncludingCrossGroupOffset()
        {
            // Force small row groups so a modest table spans several groups and exercises the
            // whole-group skip plus the cross-group boundary in the streaming reader.
            TidalSqlStatementVisitor.RowGroupSize = 100;

            var session = new TidalSql();
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);
            this.CreateNums(session, 250, chunk: 250);

            var reader = new TidalSql();

            // Full scan returns every row in order across all groups.
            var full = reader.OpenTableStream(this.dbName, "Nums", batchSize: 60);
            Assert.AreEqual(250, full.TotalRows);
            var fullBatches = await DrainAsync(full);
            var nIndex = full.Columns.ToList().FindIndex(c => c.Name == "N");
            CollectionAssert.AreEqual(Enumerable.Range(0, 250).ToArray(), AllN(fullBatches, nIndex).ToArray());

            // A window that straddles the first/second group boundary (rows 95..114).
            var window = reader.OpenTableStream(this.dbName, "Nums", batchSize: 1000, offset: 95, limit: 20);
            var windowValues = AllN(await DrainAsync(window), nIndex);
            CollectionAssert.AreEqual(Enumerable.Range(95, 20).ToArray(), windowValues.ToArray());
        }

        [TestMethod]
        public async Task ConcurrentStreams_WhileWriting_EachSeeAConsistentSnapshot()
        {
            var writerSession = new TidalSql();
            Assert.IsTrue(writerSession.ExecuteQuery($"use {this.dbName}").Success);
            this.CreateNums(writerSession, 200, chunk: 200);

            using var stop = new CancellationTokenSource();
            var writer = Task.Run(() =>
            {
                var s = new TidalSql();
                s.ExecuteQuery($"use {this.dbName}");
                var next = 200;
                while (!stop.IsCancellationRequested && next < 400)
                {
                    InsertNums(s, next, 10, 10);
                    next += 10;
                }
            });

            var readers = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
            {
                var s = new TidalSql();
                for (var iter = 0; iter < 15; iter++)
                {
                    var stream = s.OpenTableStream(this.dbName, "Nums", batchSize: 64);
                    var batches = await DrainAsync(stream);
                    var nIndex = stream.Columns.ToList().FindIndex(c => c.Name == "N");
                    var values = AllN(batches, nIndex);

                    // Append-only table: a consistent snapshot is exactly 0..count-1 in order.
                    Assert.AreEqual(stream.TotalRows, values.Count, "Batch total did not match reported row count.");
                    CollectionAssert.AreEqual(Enumerable.Range(0, values.Count).ToArray(), values.ToArray(),
                        "Reader observed a torn / inconsistent snapshot.");
                }
            })).ToArray();

            await Task.WhenAll(readers);
            stop.Cancel();
            await writer;
        }
    }
}
