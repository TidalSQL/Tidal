namespace TidalSqlTests
{
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using Parquet;
    using Parquet.Data;
    using TidalSqlLib;

    [TestClass]
    public class InsertStatementTests
    {
        private TidalSql db = null!;
        private string dbName = null!;
        private string dbPath = null!;
        private string originalDir = null!;

        [TestInitialize]
        public void TestInitialize()
        {
            this.originalDir = Directory.GetCurrentDirectory();
            this.dbName = "InsertTest_" + Guid.NewGuid().ToString("N")[..8];

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
        public void Insert_SingleRow_CreatesParquetFileWithData()
        {
            this.db.ExecuteStatement(
                "CREATE TABLE Employees (EmployeeID INT, FirstName NVARCHAR(50), LastName NVARCHAR(50), HireDate DATE);");

            this.db.ExecuteStatement(
                "INSERT INTO Employees (EmployeeID, FirstName, LastName, HireDate) " +
                "VALUES (1, 'John', 'Doe', '2024-02-17');");

            var filePath = Path.Combine(this.dbPath, "tables", "Employees.parquet");
            Assert.IsTrue(File.Exists(filePath), "Parquet file should exist after insert.");

            var data = ReadParquetData(filePath);
            Assert.AreEqual(1, data["EmployeeID"].Count);
            Assert.AreEqual(1, (int)data["EmployeeID"][0]);
            Assert.AreEqual("John", (string)data["FirstName"][0]);
            Assert.AreEqual("Doe", (string)data["LastName"][0]);
        }

        [TestMethod]
        public void Insert_MultipleRows_AllRowsPersisted()
        {
            this.db.ExecuteStatement(
                "CREATE TABLE Products (ProductID INT, ProductName NVARCHAR(100));");

            this.db.ExecuteStatement(
                "INSERT INTO Products (ProductID, ProductName) VALUES (1, 'Widget');");
            this.db.ExecuteStatement(
                "INSERT INTO Products (ProductID, ProductName) VALUES (2, 'Gadget');");
            this.db.ExecuteStatement(
                "INSERT INTO Products (ProductID, ProductName) VALUES (3, 'Doohickey');");

            var filePath = Path.Combine(this.dbPath, "tables", "Products.parquet");
            var data = ReadParquetData(filePath);

            Assert.AreEqual(3, data["ProductID"].Count);
            Assert.AreEqual(1, (int)data["ProductID"][0]);
            Assert.AreEqual(2, (int)data["ProductID"][1]);
            Assert.AreEqual(3, (int)data["ProductID"][2]);
            Assert.AreEqual("Widget", (string)data["ProductName"][0]);
            Assert.AreEqual("Gadget", (string)data["ProductName"][1]);
            Assert.AreEqual("Doohickey", (string)data["ProductName"][2]);
        }

        [TestMethod]
        public void Insert_AllDataTypes_ValuesStoredCorrectly()
        {
            this.db.ExecuteStatement(
                "CREATE TABLE Mixed (Id INT, Name NVARCHAR(50), StartDate DATE, Salary MONEY);");

            this.db.ExecuteStatement(
                "INSERT INTO Mixed (Id, Name, StartDate, Salary) " +
                "VALUES (42, 'Alice', '2025-06-15', 75000);");

            var filePath = Path.Combine(this.dbPath, "tables", "Mixed.parquet");
            var data = ReadParquetData(filePath);

            Assert.AreEqual(42, (int)data["Id"][0]);
            Assert.AreEqual("Alice", (string)data["Name"][0]);
            Assert.AreEqual(new DateTime(2025, 6, 15), (DateTime)data["StartDate"][0]);
            Assert.AreEqual(75000m, (decimal)data["Salary"][0]);
        }

        private static Dictionary<string, List<object>> ReadParquetData(string filePath)
        {
            var result = new Dictionary<string, List<object>>();
            Task.Run(async () =>
            {
                using var stream = File.OpenRead(filePath);
                using var reader = await ParquetReader.CreateAsync(stream);
                foreach (var field in reader.Schema.DataFields)
                {
                    result[field.Name] = new List<object>();
                }

                for (int g = 0; g < reader.RowGroupCount; g++)
                {
                    using var groupReader = reader.OpenRowGroupReader(g);
                    foreach (var field in reader.Schema.DataFields)
                    {
                        var dc = await groupReader.ReadColumnAsync(field);
                        foreach (var val in dc.Data)
                        {
                            result[field.Name].Add(val);
                        }
                    }
                }
            }).Wait();
            return result;
        }
    }
}
