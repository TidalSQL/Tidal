namespace TidalSqlTests
{
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using TidalSqlLib;

    [TestClass]
    public class SelectStatementTests
    {
        private TidalSql db = null!;
        private string dbName = null!;
        private string dbPath = null!;
        private string originalDir = null!;

        [TestInitialize]
        public void TestInitialize()
        {
            this.originalDir = Directory.GetCurrentDirectory();
            this.dbName = "SelectTest_" + Guid.NewGuid().ToString("N")[..8];

            this.db = new TidalSql();
            this.db.ExecuteStatement($"create database {this.dbName}");
            this.db.ExecuteStatement($"use {this.dbName}");

            this.dbPath = this.db.CurrentDatabasePath!;
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try { Directory.SetCurrentDirectory(this.originalDir); } catch { }
            try { Directory.Delete(this.dbPath, true); } catch { }
        }

        [TestMethod]
        public void SelectStar_ReturnsAllColumnsAndRows()
        {
            this.db.ExecuteStatement(
                "CREATE TABLE Employees (EmployeeID INT, FirstName NVARCHAR(50), LastName NVARCHAR(50));");
            this.db.ExecuteStatement(
                "INSERT INTO Employees (EmployeeID, FirstName, LastName) VALUES (1, 'John', 'Doe');");
            this.db.ExecuteStatement(
                "INSERT INTO Employees (EmployeeID, FirstName, LastName) VALUES (2, 'Jane', 'Smith');");

            var result = this.db.ExecuteQuery("SELECT * FROM Employees;");

            Assert.IsTrue(result.Success);
            CollectionAssert.AreEqual(
                new[] { "EmployeeID", "FirstName", "LastName" }, result.Columns);
            Assert.AreEqual(2, result.RowCount);
            Assert.AreEqual("John", result.Rows[0]["FirstName"]);
            Assert.AreEqual("Doe", result.Rows[0]["LastName"]);
            Assert.AreEqual("Jane", result.Rows[1]["FirstName"]);
            Assert.AreEqual("Smith", result.Rows[1]["LastName"]);
        }

        [TestMethod]
        public void SelectSpecificColumns_ReturnsOnlyRequestedColumns()
        {
            this.db.ExecuteStatement(
                "CREATE TABLE People (Id INT, FirstName NVARCHAR(50), LastName NVARCHAR(50));");
            this.db.ExecuteStatement(
                "INSERT INTO People (Id, FirstName, LastName) VALUES (1, 'Alice', 'Wonder');");

            var result = this.db.ExecuteQuery("SELECT FirstName, LastName FROM People;");

            Assert.IsTrue(result.Success);
            CollectionAssert.AreEqual(new[] { "FirstName", "LastName" }, result.Columns);
            CollectionAssert.DoesNotContain(result.Columns, "Id");
            Assert.AreEqual(1, result.RowCount);
            Assert.AreEqual("Alice", result.Rows[0]["FirstName"]);
            Assert.AreEqual("Wonder", result.Rows[0]["LastName"]);
        }

        [TestMethod]
        public void SelectStar_EmptyTable_ReturnsZeroRows()
        {
            this.db.ExecuteStatement(
                "CREATE TABLE Empty (Id INT, Name NVARCHAR(50));");

            var result = this.db.ExecuteQuery("SELECT * FROM Empty;");

            Assert.IsTrue(result.Success);
            CollectionAssert.AreEqual(new[] { "Id", "Name" }, result.Columns);
            Assert.AreEqual(0, result.RowCount);
        }

        [TestMethod]
        public void SelectStar_AfterMultipleInserts_ReturnsCorrectRowCount()
        {
            this.db.ExecuteStatement(
                "CREATE TABLE Items (ItemID INT, ItemName NVARCHAR(100));");

            for (int i = 1; i <= 5; i++)
            {
                this.db.ExecuteStatement(
                    $"INSERT INTO Items (ItemID, ItemName) VALUES ({i}, 'Item{i}');");
            }

            var result = this.db.ExecuteQuery("SELECT * FROM Items;");

            Assert.IsTrue(result.Success);
            Assert.AreEqual(5, result.RowCount);
            for (int i = 0; i < 5; i++)
            {
                Assert.AreEqual($"Item{i + 1}", result.Rows[i]["ItemName"]);
            }
        }
    }
}
