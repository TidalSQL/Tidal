namespace TidalSqlTests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using TidalSqlLib;

    /// <summary>
    /// Read-only Delta Lake interop tests. Delta tables are synthesized on disk by generating real
    /// Parquet data files (via the engine's own writer) and then hand-authoring a <c>_delta_log</c>
    /// of newline-delimited JSON commit actions — the format Spark/Databricks/delta-rs produce.
    /// These tests assert the transaction-log semantics that a raw Parquet-directory read cannot give:
    /// tombstoned files are excluded, partition columns (which live only in the log) are projected,
    /// the table is discoverable and read-only, and unsupported protocol features fail cleanly.
    /// </summary>
    [TestClass]
    public class DeltaTableTests
    {
        private string dbName = null!;
        private string dbPath = null!;

        [TestInitialize]
        public void TestInitialize()
        {
            this.dbName = "Delta_" + Guid.NewGuid().ToString("N")[..8];
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
        /// Produces a Parquet data file with columns (Id INT, N INT) holding <paramref name="rows"/>
        /// rows, by round-tripping through a throwaway single-file engine table.
        /// </summary>
        private string MakeDataFile(int rows, int idStart = 1)
        {
            var session = new TidalSql();
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);
            var seed = "Seed_" + Guid.NewGuid().ToString("N")[..8];
            Assert.IsTrue(session.ExecuteQuery($"CREATE TABLE dbo.{seed} (Id INT NOT NULL, N INT NOT NULL);").Success);
            for (var i = 0; i < rows; i++)
            {
                var id = idStart + i;
                Assert.IsTrue(session.ExecuteQuery($"INSERT INTO {seed} (Id, N) VALUES ({id}, {id * 100});").Success);
            }

            var seedFile = Path.Combine(this.dbPath, "tables", $"{seed}.parquet");
            Assert.IsTrue(File.Exists(seedFile));

            // Move it out of tables/ so it is not itself discovered as a table.
            var staged = Path.Combine(this.dbPath, $"{seed}.parquet");
            File.Move(seedFile, staged);
            return staged;
        }

        private string CreateDeltaDir(string tableName)
        {
            var dir = Path.Combine(this.dbPath, "tables", tableName);
            Directory.CreateDirectory(Path.Combine(dir, "_delta_log"));
            return dir;
        }

        private static void WriteCommit(string tableDir, long version, IEnumerable<object> actions)
        {
            var lines = actions.Select(a => JsonSerializer.Serialize(a));
            var path = Path.Combine(tableDir, "_delta_log", $"{version:D20}.json");
            File.WriteAllLines(path, lines);
        }

        private static object Protocol(int minReader = 1, int minWriter = 2)
            => new { protocol = new { minReaderVersion = minReader, minWriterVersion = minWriter } };

        private static object MetaData(IReadOnlyList<(string Name, string Type)> columns, IReadOnlyList<string> partitionColumns)
        {
            var schema = new
            {
                type = "struct",
                fields = columns.Select(c => new
                {
                    name = c.Name,
                    type = c.Type,
                    nullable = true,
                    metadata = new { },
                }).ToArray(),
            };

            return new
            {
                metaData = new
                {
                    id = Guid.NewGuid().ToString(),
                    format = new { provider = "parquet", options = new { } },
                    schemaString = JsonSerializer.Serialize(schema),
                    partitionColumns = partitionColumns.ToArray(),
                    configuration = new { },
                    createdTime = 0L,
                },
            };
        }

        private static object Add(string path, IReadOnlyDictionary<string, string?>? partitionValues = null)
            => new
            {
                add = new
                {
                    path,
                    partitionValues = partitionValues ?? new Dictionary<string, string?>(),
                    size = 1024L,
                    modificationTime = 0L,
                    dataChange = true,
                },
            };

        private static object Remove(string path)
            => new { remove = new { path, deletionTimestamp = 0L, dataChange = true } };

        [TestMethod]
        public void Select_UnpartitionedDeltaTable_ReadsRows()
        {
            var dir = this.CreateDeltaDir("Events");
            var file = this.MakeDataFile(10);
            File.Move(file, Path.Combine(dir, "data-0.parquet"));

            WriteCommit(dir, 0, new object[]
            {
                Protocol(),
                MetaData(new[] { ("Id", "integer"), ("N", "integer") }, Array.Empty<string>()),
                Add("data-0.parquet"),
            });

            var session = new TidalSql();
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);
            var result = session.ExecuteQuery("SELECT * FROM Events;");
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(10, result.Rows.Count);
        }

        [TestMethod]
        public void Select_ExcludesTombstonedFiles()
        {
            var dir = this.CreateDeltaDir("Ledger");
            var f0 = this.MakeDataFile(10, idStart: 1);
            var f1 = this.MakeDataFile(10, idStart: 100);
            File.Move(f0, Path.Combine(dir, "data-0.parquet"));
            File.Move(f1, Path.Combine(dir, "data-1.parquet"));

            // v0 adds both files (20 rows on disk); v1 removes data-1 (10 rows logically deleted).
            WriteCommit(dir, 0, new object[]
            {
                Protocol(),
                MetaData(new[] { ("Id", "integer"), ("N", "integer") }, Array.Empty<string>()),
                Add("data-0.parquet"),
                Add("data-1.parquet"),
            });
            WriteCommit(dir, 1, new object[] { Remove("data-1.parquet") });

            var session = new TidalSql();
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);

            // Both files still exist on disk...
            Assert.IsTrue(File.Exists(Path.Combine(dir, "data-1.parquet")));
            // ...but the log says only data-0 is active: a raw glob would wrongly return 20.
            var result = session.ExecuteQuery("SELECT COUNT(*) AS C FROM Ledger;");
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(10L, Convert.ToInt64(result.Rows[0]["C"]));
        }

        [TestMethod]
        public void Select_PartitionedTable_ProjectsPartitionColumns()
        {
            var dir = this.CreateDeltaDir("Sales");
            var us = this.MakeDataFile(10);
            var eu = this.MakeDataFile(5);
            Directory.CreateDirectory(Path.Combine(dir, "region=US"));
            Directory.CreateDirectory(Path.Combine(dir, "region=EU"));
            File.Move(us, Path.Combine(dir, "region=US", "data.parquet"));
            File.Move(eu, Path.Combine(dir, "region=EU", "data.parquet"));

            WriteCommit(dir, 0, new object[]
            {
                Protocol(),
                MetaData(
                    new[] { ("Id", "integer"), ("N", "integer"), ("region", "string") },
                    new[] { "region" }),
                Add("region=US/data.parquet", new Dictionary<string, string?> { ["region"] = "US" }),
                Add("region=EU/data.parquet", new Dictionary<string, string?> { ["region"] = "EU" }),
            });

            var session = new TidalSql();
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);

            var all = session.ExecuteQuery("SELECT * FROM Sales;");
            Assert.IsTrue(all.Success, all.Message);
            Assert.AreEqual(15, all.Rows.Count);
            CollectionAssert.Contains(all.Columns.ToList(), "region");

            var grouped = session.ExecuteQuery(
                "SELECT region, COUNT(*) AS C FROM Sales GROUP BY region ORDER BY region;");
            Assert.IsTrue(grouped.Success, grouped.Message);
            Assert.AreEqual(2, grouped.Rows.Count);
            Assert.AreEqual("EU", grouped.Rows[0]["region"]);
            Assert.AreEqual(5L, Convert.ToInt64(grouped.Rows[0]["C"]));
            Assert.AreEqual("US", grouped.Rows[1]["region"]);
            Assert.AreEqual(10L, Convert.ToInt64(grouped.Rows[1]["C"]));

            var filtered = session.ExecuteQuery("SELECT COUNT(*) AS C FROM Sales WHERE region = 'US';");
            Assert.IsTrue(filtered.Success, filtered.Message);
            Assert.AreEqual(10L, Convert.ToInt64(filtered.Rows[0]["C"]));
        }

        [TestMethod]
        public void DeltaTable_AppearsInTableListing()
        {
            var dir = this.CreateDeltaDir("Catalog");
            var file = this.MakeDataFile(3);
            File.Move(file, Path.Combine(dir, "data-0.parquet"));
            WriteCommit(dir, 0, new object[]
            {
                Protocol(),
                MetaData(new[] { ("Id", "integer"), ("N", "integer") }, Array.Empty<string>()),
                Add("data-0.parquet"),
            });

            var session = new TidalSql();
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);
            var listing = session.ExecuteQuery("SELECT name FROM sys.tables;");
            Assert.IsTrue(listing.Success, listing.Message);
            var names = listing.Rows.Select(r => (string)r["name"]!).ToList();
            CollectionAssert.Contains(names, "Catalog");
        }

        [TestMethod]
        public void Insert_IntoDeltaTable_IsRejected()
        {
            var dir = this.CreateDeltaDir("ReadOnly");
            var file = this.MakeDataFile(2);
            File.Move(file, Path.Combine(dir, "data-0.parquet"));
            WriteCommit(dir, 0, new object[]
            {
                Protocol(),
                MetaData(new[] { ("Id", "integer"), ("N", "integer") }, Array.Empty<string>()),
                Add("data-0.parquet"),
            });

            var session = new TidalSql();
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);
            var result = session.ExecuteQuery("INSERT INTO ReadOnly (Id, N) VALUES (1, 2);");
            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.Message, "Delta");
        }

        [TestMethod]
        public void UnsupportedReaderFeature_FailsWithClearError()
        {
            var dir = this.CreateDeltaDir("Modern");
            var file = this.MakeDataFile(2);
            File.Move(file, Path.Combine(dir, "data-0.parquet"));

            // minReaderVersion 3 + deletionVectors: rows may be logically deleted via a side file we
            // do not read, so returning the raw Parquet rows would be wrong — must error instead.
            WriteCommit(dir, 0, new object[]
            {
                new { protocol = new { minReaderVersion = 3, minWriterVersion = 7, readerFeatures = new[] { "deletionVectors" }, writerFeatures = new[] { "deletionVectors" } } },
                MetaData(new[] { ("Id", "integer"), ("N", "integer") }, Array.Empty<string>()),
                Add("data-0.parquet"),
            });

            var session = new TidalSql();
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);
            var result = session.ExecuteQuery("SELECT * FROM Modern;");
            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.Message, "deletionVectors");
        }
    }
}
