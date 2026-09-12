namespace ParqBaseTests
{
    using System;
    using System.IO;
    using System.Linq;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using ParqBaseLib;

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
            var session = new ParqBase();
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
            var session = new ParqBase();
            session.ExecuteQuery($"create database {this.dbName}");
            session.ExecuteQuery($"use {this.dbName}");
            this.dbPath = session.CurrentDatabasePath!;

            Assert.ThrowsException<Exception>(() => session.GetTableInfo(this.dbName, "NoSuchTable"));
        }
    }
}
