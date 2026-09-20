namespace TidalSqlTests
{
    using System.Linq;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using TidalSqlLib;

    [TestClass]
    public class QueryFeatureTests
    {
        private TidalSql db = null!;
        private string dbName = null!;
        private string dbPath = null!;
        private string originalDir = null!;

        [TestInitialize]
        public void TestInitialize()
        {
            this.originalDir = Directory.GetCurrentDirectory();
            this.dbName = "FeatureTest_" + Guid.NewGuid().ToString("N")[..8];

            this.db = new TidalSql();
            this.db.ExecuteStatement($"create database {this.dbName}");
            this.db.ExecuteStatement($"use {this.dbName}");
            this.dbPath = this.db.CurrentDatabasePath!;

            this.db.ExecuteStatement(
                "CREATE TABLE Emp (Id INT, Name NVARCHAR(50), Salary MONEY);");
            this.db.ExecuteStatement(
                "INSERT INTO Emp (Id, Name, Salary) VALUES (1, 'Alice', 100), (2, 'Bob', 200), (3, 'Carol', 300);");

            this.db.ExecuteStatement(
                "CREATE TABLE Dept (EmpId INT, DeptName NVARCHAR(50));");
            this.db.ExecuteStatement(
                "INSERT INTO Dept (EmpId, DeptName) VALUES (1, 'Sales'), (2, 'Eng');");
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try { Directory.SetCurrentDirectory(this.originalDir); } catch { }
            try { Directory.Delete(this.dbPath, true); } catch { }
        }

        [TestMethod]
        public void Insert_MultiRow_InsertsAllRows()
        {
            var result = this.db.ExecuteQuery("SELECT * FROM Emp;");
            Assert.AreEqual(3, result.RowCount);
        }

        [TestMethod]
        public void Where_FiltersRows()
        {
            var result = this.db.ExecuteQuery("SELECT Name FROM Emp WHERE Id > 1;");
            Assert.IsTrue(result.Success);
            Assert.AreEqual(2, result.RowCount);
        }

        [TestMethod]
        public void Where_And_Or_Combines()
        {
            var result = this.db.ExecuteQuery(
                "SELECT Name FROM Emp WHERE Id = 1 OR Name = 'Carol';");
            Assert.AreEqual(2, result.RowCount);
        }

        [TestMethod]
        public void OrderBy_Descending_Sorts()
        {
            var result = this.db.ExecuteQuery("SELECT Id FROM Emp ORDER BY Id DESC;");
            Assert.AreEqual(3, (int)result.Rows[0]["Id"]!);
            Assert.AreEqual(1, (int)result.Rows[2]["Id"]!);
        }

        [TestMethod]
        public void Top_LimitsRows()
        {
            var result = this.db.ExecuteQuery("SELECT TOP 2 Id FROM Emp ORDER BY Id;");
            Assert.AreEqual(2, result.RowCount);
            Assert.AreEqual(1, (int)result.Rows[0]["Id"]!);
        }

        [TestMethod]
        public void Insert_PartialColumns_StoresNullForOmitted()
        {
            this.db.ExecuteStatement("INSERT INTO Emp (Id) VALUES (4);");
            var result = this.db.ExecuteQuery("SELECT Name FROM Emp WHERE Id = 4;");
            Assert.AreEqual(1, result.RowCount);
            Assert.IsNull(result.Rows[0]["Name"]);
        }

        [TestMethod]
        public void Insert_NegativeNumber_Supported()
        {
            this.db.ExecuteStatement("INSERT INTO Emp (Id, Name, Salary) VALUES (-5, 'Neg', -50);");
            var result = this.db.ExecuteQuery("SELECT Salary FROM Emp WHERE Id = -5;");
            Assert.AreEqual(1, result.RowCount);
            Assert.AreEqual(-50m, (decimal)result.Rows[0]["Salary"]!);
        }

        [TestMethod]
        public void ParseError_ReportedAsFailure()
        {
            var result = this.db.ExecuteQuery("SELCT * FRM Emp;");
            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.Message, "Parse error");
        }

        [TestMethod]
        public void CreateTable_Duplicate_ReportsFailure()
        {
            var result = this.db.ExecuteQuery("CREATE TABLE Emp (Id INT);");
            Assert.IsFalse(result.Success);
        }

        [TestMethod]
        public void Select_InnerJoin_MatchesRows()
        {
            var result = this.db.ExecuteQuery(
                "SELECT e.Name, d.DeptName FROM Emp e JOIN Dept d ON e.Id = d.EmpId ORDER BY e.Id;");
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(2, result.RowCount);
            Assert.AreEqual("Alice", (string)result.Rows[0]["Name"]!);
            Assert.AreEqual("Sales", (string)result.Rows[0]["DeptName"]!);
        }

        [TestMethod]
        public void Select_LeftJoin_KeepsUnmatchedLeftRows()
        {
            var result = this.db.ExecuteQuery(
                "SELECT e.Name, d.DeptName FROM Emp e LEFT JOIN Dept d ON e.Id = d.EmpId ORDER BY e.Id;");
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(3, result.RowCount);
            Assert.IsNull(result.Rows[2]["DeptName"]);
        }

        [TestMethod]
        public void Select_RightJoin_KeepsUnmatchedRightRows()
        {
            this.db.ExecuteStatement("INSERT INTO Dept (EmpId, DeptName) VALUES (99, 'Ghost');");
            var result = this.db.ExecuteQuery(
                "SELECT e.Name, d.DeptName FROM Emp e RIGHT JOIN Dept d ON e.Id = d.EmpId;");
            Assert.IsTrue(result.Success, result.Message);
            Assert.IsTrue(result.Rows.Any(r => r["Name"] == null && (string)r["DeptName"]! == "Ghost"));
        }

        [TestMethod]
        public void Select_CrossJoin_ProducesCartesianProduct()
        {
            var result = this.db.ExecuteQuery("SELECT e.Id, d.DeptName FROM Emp e CROSS JOIN Dept d;");
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(6, result.RowCount);
        }

        [TestMethod]
        public void Select_DerivedTable_Subquery()
        {
            var result = this.db.ExecuteQuery(
                "SELECT t.Name FROM (SELECT Name FROM Emp WHERE Id > 1) t ORDER BY t.Name;");
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(2, result.RowCount);
            Assert.AreEqual("Bob", (string)result.Rows[0]["Name"]!);
        }

        [TestMethod]
        public void Select_InSubquery_FiltersRows()
        {
            var result = this.db.ExecuteQuery(
                "SELECT Name FROM Emp WHERE Id IN (SELECT EmpId FROM Dept) ORDER BY Id;");
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(2, result.RowCount);
        }

        [TestMethod]
        public void Select_ExistsCorrelatedSubquery_FiltersRows()
        {
            var result = this.db.ExecuteQuery(
                "SELECT Name FROM Emp e WHERE EXISTS (SELECT 1 FROM Dept d WHERE d.EmpId = e.Id) ORDER BY e.Id;");
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(2, result.RowCount);
        }

        [TestMethod]
        public void Select_ScalarSubquery_InWhere()
        {
            var result = this.db.ExecuteQuery(
                "SELECT Name FROM Emp WHERE Id = (SELECT EmpId FROM Dept WHERE DeptName = 'Eng');");
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(1, result.RowCount);
            Assert.AreEqual("Bob", (string)result.Rows[0]["Name"]!);
        }

        [TestMethod]
        public void Insert_WithoutDatabase_ReportsFailure()
        {
            var fresh = new TidalSql();
            var result = fresh.ExecuteQuery("INSERT INTO Nope (Id) VALUES (1);");
            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.Message, "No database selected");
        }

        [TestMethod]
        public void ListDatabases_IncludesCurrentDatabase()
        {
            var result = this.db.ExecuteQuery("SELECT name FROM sys.databases;");

            Assert.IsTrue(result.Success);
            CollectionAssert.AreEqual(new[] { "name" }, result.Columns);
            Assert.IsTrue(result.Rows.Any(r => (string)r["name"]! == this.dbName));
        }

        [TestMethod]
        public void ListDatabases_SelectStar_ReturnsNameColumn()
        {
            var result = this.db.ExecuteQuery("SELECT * FROM sys.databases;");

            Assert.IsTrue(result.Success);
            CollectionAssert.AreEqual(new[] { "name" }, result.Columns);
        }

        [TestMethod]
        public void ListDatabases_WorksWithoutSelectedDatabase()
        {
            var fresh = new TidalSql();

            var result = fresh.ExecuteQuery("SELECT name FROM sys.databases;");

            Assert.IsTrue(result.Success);
            CollectionAssert.AreEqual(new[] { "name" }, result.Columns);
        }

        [TestMethod]
        public void ListObjects_ReturnsTablesInCurrentDatabase()
        {
            this.db.ExecuteStatement("CREATE TABLE Widgets (Id INT);");

            var result = this.db.ExecuteQuery("SELECT name, type FROM sys.objects;");

            Assert.IsTrue(result.Success);
            CollectionAssert.AreEqual(new[] { "name", "type" }, result.Columns);

            var emp = result.Rows.Single(r => (string)r["name"]! == "Emp");
            Assert.AreEqual("table", emp["type"]);
            Assert.IsTrue(result.Rows.Any(r => (string)r["name"]! == "Widgets"));
        }

        [TestMethod]
        public void ListObjects_SelectStar_ReturnsNameAndType()
        {
            var result = this.db.ExecuteQuery("SELECT * FROM sys.objects;");

            Assert.IsTrue(result.Success);
            CollectionAssert.AreEqual(new[] { "name", "type" }, result.Columns);
        }

        [TestMethod]
        public void ListTables_ReturnsTableNames()
        {
            var result = this.db.ExecuteQuery("SELECT name FROM sys.tables;");

            Assert.IsTrue(result.Success);
            CollectionAssert.AreEqual(new[] { "name" }, result.Columns);
            Assert.IsTrue(result.Rows.Any(r => (string)r["name"]! == "Emp"));
        }

        [TestMethod]
        public void ListObjects_WithoutSelectedDatabase_ThrowsDescriptiveError()
        {
            var fresh = new TidalSql();

            var result = fresh.ExecuteQuery("SELECT name, type FROM sys.objects;");

            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.Message, "No database selected");
            StringAssert.Contains(result.Message, "USE <database>");
        }
    }
}
