namespace ParqBaseTests
{
    using System.Linq;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using ParqBaseLib;

    [TestClass]
    public class PersistenceTests
    {
        private string dbName = null!;
        private string dbPath = null!;
        private string originalDir = null!;

        [TestInitialize]
        public void TestInitialize()
        {
            this.originalDir = Directory.GetCurrentDirectory();
            this.dbName = "PersistTest_" + Guid.NewGuid().ToString("N")[..8];
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try { Directory.SetCurrentDirectory(this.originalDir); } catch { }
            try { Directory.Delete(this.dbPath, true); } catch { }
        }

        [TestMethod]
        public void CreateAndInsert_PersistToFileSystem_AndSurviveRestart()
        {
            // Session 1: create database + table, insert rows.
            var session1 = new ParqBase();
            Assert.IsTrue(session1.ExecuteQuery($"create database {this.dbName}").Success);
            Assert.IsTrue(session1.ExecuteQuery($"use {this.dbName}").Success);
            Assert.IsTrue(session1.ExecuteQuery(
                "CREATE TABLE Employees (EmployeeID INT, FirstName NVARCHAR(50), HireDate DATE, Salary MONEY);").Success);
            Assert.IsTrue(session1.ExecuteQuery(
                "INSERT INTO Employees (EmployeeID, FirstName, HireDate, Salary) VALUES (1, 'John', '2024-02-17', 75000);").Success);
            Assert.IsTrue(session1.ExecuteQuery(
                "INSERT INTO Employees (EmployeeID, FirstName, HireDate, Salary) VALUES (2, 'Jane', '2025-06-15', 82000);").Success);

            this.dbPath = session1.CurrentDatabasePath!;

            // The database directory and the table file must exist on disk.
            Assert.IsTrue(Directory.Exists(this.dbPath), "Database directory should be persisted.");
            var tableFile = Path.Combine(this.dbPath, "tables", "Employees.parquet");
            Assert.IsTrue(File.Exists(tableFile), "Table parquet file should be persisted.");
            Assert.IsTrue(new FileInfo(tableFile).Length > 0, "Table parquet file should contain data.");

            // Session 2: a brand-new instance (simulating a process restart) reads the persisted data.
            var session2 = new ParqBase();
            Assert.IsTrue(session2.ExecuteQuery($"use {this.dbName}").Success);

            var tables = session2.ExecuteQuery("SELECT name FROM sys.tables;");
            Assert.IsTrue(tables.Rows.Any(r => (string)r["name"]! == "Employees"));

            var result = session2.ExecuteQuery("SELECT * FROM Employees ORDER BY EmployeeID;");
            Assert.IsTrue(result.Success);
            Assert.AreEqual(2, result.RowCount);
            CollectionAssert.AreEqual(
                new[] { "EmployeeID", "FirstName", "HireDate", "Salary" }, result.Columns);

            Assert.AreEqual(1, (int)result.Rows[0]["EmployeeID"]!);
            Assert.AreEqual("John", result.Rows[0]["FirstName"]);
            Assert.AreEqual(new DateTime(2024, 2, 17), (DateTime)result.Rows[0]["HireDate"]!);
            Assert.AreEqual(75000m, (decimal)result.Rows[0]["Salary"]!);

            Assert.AreEqual(2, (int)result.Rows[1]["EmployeeID"]!);
            Assert.AreEqual("Jane", result.Rows[1]["FirstName"]);
        }

        [TestMethod]
        public void Insert_AfterRestart_AppendsToPersistedTable()
        {
            var session1 = new ParqBase();
            session1.ExecuteQuery($"create database {this.dbName}");
            session1.ExecuteQuery($"use {this.dbName}");
            session1.ExecuteQuery("CREATE TABLE Nums (Id INT);");
            session1.ExecuteQuery("INSERT INTO Nums (Id) VALUES (1);");
            this.dbPath = session1.CurrentDatabasePath!;

            // Fresh instance inserts without re-creating the table (schema loaded from file, not cache).
            var session2 = new ParqBase();
            session2.ExecuteQuery($"use {this.dbName}");
            var insert = session2.ExecuteQuery("INSERT INTO Nums (Id) VALUES (2);");
            Assert.IsTrue(insert.Success, insert.Message);

            var result = session2.ExecuteQuery("SELECT Id FROM Nums ORDER BY Id;");
            Assert.AreEqual(2, result.RowCount);
            Assert.AreEqual(1, (int)result.Rows[0]["Id"]!);
            Assert.AreEqual(2, (int)result.Rows[1]["Id"]!);
        }
    }
}
