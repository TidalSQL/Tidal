namespace TidalSqlTests
{
    using System;
    using System.IO;
    using System.Linq;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using TidalSqlLib;

    [TestClass]
    public class TableInfoTests
    {
        private string dbName = null!;
        private string dbPath = null!;

        [TestInitialize]
        public void TestInitialize()
        {
            this.dbName = "TableInfoTest_" + Guid.NewGuid().ToString("N")[..8];
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try { Directory.Delete(this.dbPath, true); } catch { }
        }

        [TestMethod]
        public void GetTableInfo_ReportsRowCountAndColumnTypeFacts()
        {
            var session = new TidalSql();
            Assert.IsTrue(session.ExecuteQuery($"create database {this.dbName}").Success);
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);
            Assert.IsTrue(session.ExecuteQuery(
                @"CREATE TABLE dbo.Widgets (
                    Id INT IDENTITY(1,1) NOT NULL,
                    Name VARCHAR(100) NOT NULL,
                    CreatedAt DATETIME2(6) NOT NULL,
                    Price DECIMAL(12,2) NULL);").Success);
            Assert.IsTrue(session.ExecuteQuery(
                "INSERT INTO Widgets (Name, CreatedAt, Price) VALUES ('A', '2026-01-01', 9.99);").Success);
            Assert.IsTrue(session.ExecuteQuery(
                "INSERT INTO Widgets (Name, CreatedAt, Price) VALUES ('B', '2026-02-02', NULL);").Success);

            this.dbPath = session.CurrentDatabasePath!;

            var info = session.GetTableInfo(this.dbName, "Widgets");

            Assert.AreEqual(this.dbName, info.Database);
            Assert.AreEqual("dbo", info.Schema);
            Assert.AreEqual("Widgets", info.Table);
            Assert.AreEqual(2, info.RowCount);
            Assert.AreEqual(4, info.ColumnCount);
            Assert.IsTrue(info.SizeBytes > 0);

            // Detailed summary facts.
            Assert.IsFalse(info.IsMultiPart);
            Assert.AreEqual(1, info.PartCount);
            Assert.IsTrue(info.CreatedAt > DateTime.MinValue);
            Assert.IsTrue(info.LastDataChange >= info.CreatedAt);
            Assert.IsNotNull(info.LastSchemaChange, "A CREATE TABLE table should have a schema-change timestamp.");
            Assert.IsNull(info.LastQueryRun, "No query has read the table yet.");
            Assert.AreEqual(0, info.Permissions.Count, "No explicit grants were made.");

            var id = info.Columns[0];
            Assert.AreEqual("Id", id.Name);
            Assert.AreEqual("int", id.DataType);
            Assert.IsFalse(id.Nullable);
            Assert.IsTrue(id.IsIdentity);
            Assert.AreEqual(10, id.Precision);

            var name = info.Columns.Single(c => c.Name == "Name");
            Assert.AreEqual("varchar(100)", name.DataType);
            Assert.AreEqual(100, name.MaxLength);
            Assert.AreEqual(0, name.Precision);

            var createdAt = info.Columns.Single(c => c.Name == "CreatedAt");
            Assert.AreEqual("datetime2(6)", createdAt.DataType);
            Assert.AreEqual(8, createdAt.MaxLength);
            Assert.AreEqual(26, createdAt.Precision);
            Assert.AreEqual(6, createdAt.Scale);

            var price = info.Columns.Single(c => c.Name == "Price");
            Assert.AreEqual("decimal(12,2)", price.DataType);
            Assert.AreEqual(12, price.Precision);
            Assert.AreEqual(2, price.Scale);
            Assert.IsTrue(price.Nullable);
        }

        [TestMethod]
        public void GetTableInfo_MissingTable_Throws()
        {
            var session = new TidalSql();
            session.ExecuteQuery($"create database {this.dbName}");
            session.ExecuteQuery($"use {this.dbName}");
            this.dbPath = session.CurrentDatabasePath!;

            Assert.ThrowsException<Exception>(() => session.GetTableInfo(this.dbName, "NoSuchTable"));
        }

        [TestMethod]
        public void GetTableInfo_SurfacesPermissionsAndLastQueryRun()
        {
            var session = new TidalSql();
            Assert.IsTrue(session.ExecuteQuery($"create database {this.dbName}").Success);
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);
            this.dbPath = session.CurrentDatabasePath!;

            Assert.IsTrue(session.ExecuteQuery("CREATE TABLE dbo.Orders (Id INT NOT NULL);").Success);
            Assert.IsTrue(session.ExecuteQuery("INSERT INTO Orders (Id) VALUES (1), (2), (3);").Success);

            // Grant a table-scoped permission to a role and verify it is surfaced.
            Assert.IsTrue(session.ExecuteQuery("CREATE ROLE Analysts;").Success);
            Assert.IsTrue(session.ExecuteQuery("GRANT SELECT ON Orders TO Analysts;").Success);

            // Before any read the last-query time is unset.
            var before = session.GetTableInfo(this.dbName, "Orders");
            Assert.IsNull(before.LastQueryRun);
            var grant = before.Permissions.Single(p =>
                p.Grantee == "Analysts" && p.Permission == "SELECT");
            Assert.AreEqual("GRANT", grant.State);
            Assert.AreEqual("Table", grant.Scope);

            // Running a query against the table records a last-query-run timestamp.
            Assert.IsTrue(session.ExecuteQuery("SELECT COUNT(*) AS C FROM Orders;").Success);
            var after = session.GetTableInfo(this.dbName, "Orders");
            Assert.IsNotNull(after.LastQueryRun, "A SELECT should stamp the last-query-run time.");
            Assert.IsTrue(
                after.LastQueryRun!.Value.ToUniversalTime() >= before.CreatedAt.ToUniversalTime().AddSeconds(-5));
        }
    }
}
