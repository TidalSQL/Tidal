namespace TidalSqlTests
{
    using System;
    using System.IO;
    using System.Linq;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using TidalSqlLib;

    [TestClass]
    public class ScriptExecutionTests
    {
        private TidalSql db = null!;
        private string dbName = null!;
        private string dbPath = null!;

        [TestInitialize]
        public void TestInitialize()
        {
            this.dbName = "ScriptTest_" + Guid.NewGuid().ToString("N")[..8];
            this.db = new TidalSql();
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try
            {
                if (this.dbPath != null)
                {
                    Directory.Delete(this.dbPath, true);
                }
            }
            catch
            {
            }
        }

        [TestMethod]
        public void ExecuteScript_CreatesDatabaseTablesAndInsertsData()
        {
            var script = $@"
CREATE DATABASE {this.dbName};
USE {this.dbName};
CREATE TABLE Product (Id INT, Name NVARCHAR(50), Price MONEY);
INSERT INTO Product (Id, Name, Price) VALUES (1, 'Pen', 2), (2, 'Notebook', 5);
CREATE TABLE Customer (Id INT, Name NVARCHAR(50));
INSERT INTO Customer (Id, Name) VALUES (10, 'Alice');
";

            var results = this.db.ExecuteScript(script);
            this.dbPath = this.db.CurrentDatabasePath!;

            Assert.IsTrue(results.All(r => r.Success), string.Join("; ", results.Select(r => r.Message)));
            Assert.AreEqual(6, results.Count);

            var products = this.db.ExecuteQuery("SELECT Id, Name, Price FROM Product;");
            Assert.IsTrue(products.Success, products.Message);
            Assert.AreEqual(2, products.RowCount);

            var customers = this.db.ExecuteQuery("SELECT Id, Name FROM Customer;");
            Assert.IsTrue(customers.Success, customers.Message);
            Assert.AreEqual(1, customers.RowCount);
        }

        [TestMethod]
        public void ExecuteScript_HonorsGoBatchSeparators()
        {
            var script = $@"
CREATE DATABASE {this.dbName}
GO
USE {this.dbName}
GO
CREATE TABLE T (Id INT)
INSERT INTO T (Id) VALUES (1), (2), (3)
GO
";

            var results = this.db.ExecuteScript(script);
            this.dbPath = this.db.CurrentDatabasePath!;

            Assert.IsTrue(results.All(r => r.Success), string.Join("; ", results.Select(r => r.Message)));

            var rows = this.db.ExecuteQuery("SELECT Id FROM T;");
            Assert.AreEqual(3, rows.RowCount);
        }

        [TestMethod]
        public void ExecuteScript_StopsAtFirstFailureByDefault()
        {
            var script = $@"
CREATE DATABASE {this.dbName};
USE {this.dbName};
CREATE TABLE T (Id INT);
INSERT INTO NoSuchTable (Id) VALUES (1);
INSERT INTO T (Id) VALUES (99);
";

            var results = this.db.ExecuteScript(script);
            this.dbPath = this.db.CurrentDatabasePath!;

            Assert.IsFalse(results.Last().Success);

            // The statement after the failure must not have run.
            var rows = this.db.ExecuteQuery("SELECT Id FROM T;");
            Assert.AreEqual(0, rows.RowCount);
        }

        [TestMethod]
        public void ExecuteScript_ContinueOnError_RunsRemainingStatements()
        {
            var script = $@"
CREATE DATABASE {this.dbName};
USE {this.dbName};
CREATE TABLE T (Id INT);
INSERT INTO NoSuchTable (Id) VALUES (1);
INSERT INTO T (Id) VALUES (99);
";

            var results = this.db.ExecuteScript(script, continueOnError: true);
            this.dbPath = this.db.CurrentDatabasePath!;

            Assert.IsTrue(results.Any(r => !r.Success));

            var rows = this.db.ExecuteQuery("SELECT Id FROM T;");
            Assert.AreEqual(1, rows.RowCount);
        }

        [TestMethod]
        public void ExecuteScriptFile_ReadsAndRunsScript()
        {
            var script = $@"
CREATE DATABASE {this.dbName};
USE {this.dbName};
CREATE TABLE T (Id INT);
INSERT INTO T (Id) VALUES (1), (2);
";
            var path = Path.Combine(Path.GetTempPath(), $"tidalsql_{Guid.NewGuid():N}.sql");
            File.WriteAllText(path, script);

            try
            {
                var results = this.db.ExecuteScriptFile(path);
                this.dbPath = this.db.CurrentDatabasePath!;

                Assert.IsTrue(results.All(r => r.Success), string.Join("; ", results.Select(r => r.Message)));

                var rows = this.db.ExecuteQuery("SELECT Id FROM T;");
                Assert.AreEqual(2, rows.RowCount);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void ExecuteScriptFile_MissingFile_Throws()
        {
            var missing = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.sql");
            Assert.ThrowsException<FileNotFoundException>(() => this.db.ExecuteScriptFile(missing));
        }
    }
}
