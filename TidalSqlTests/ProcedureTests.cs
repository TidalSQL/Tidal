namespace TidalSqlTests
{
    using System;
    using System.IO;
    using System.Linq;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using TidalSqlLib;

    [TestClass]
    public class ProcedureTests
    {
        private string dbName = null!;
        private string dbPath = null!;

        [TestInitialize]
        public void TestInitialize()
        {
            this.dbName = "ProcTest_" + Guid.NewGuid().ToString("N")[..8];
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try { Directory.Delete(this.dbPath, true); } catch { }
        }

        private TidalSql NewDatabase()
        {
            var session = new TidalSql();
            Assert.IsTrue(session.ExecuteQuery($"create database {this.dbName}").Success);
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);
            this.dbPath = session.CurrentDatabasePath!;
            Assert.IsTrue(session.ExecuteQuery(
                "CREATE TABLE dbo.Items (Id INT IDENTITY(1,1) NOT NULL, Name VARCHAR(50) NOT NULL, Qty INT NOT NULL);").Success);
            return session;
        }

        private static void AssertAllSucceed(System.Collections.Generic.IReadOnlyList<QueryResult> results)
        {
            var failure = results.FirstOrDefault(r => !r.Success);
            Assert.IsNull(failure, failure?.Message);
        }

        [TestMethod]
        public void CreateAndExecuteProcedure_WithParameters_InsertsRow()
        {
            var session = this.NewDatabase();

            var results = session.ExecuteScript($@"
CREATE PROCEDURE dbo.AddItem @Name VARCHAR(50), @Qty INT
AS
INSERT INTO Items (Name, Qty) VALUES (@Name, @Qty);
GO
EXEC dbo.AddItem @Name = 'Bolt', @Qty = 42;
");
            AssertAllSucceed(results);

            var query = session.ExecuteQuery("SELECT Name, Qty FROM Items;");
            Assert.IsTrue(query.Success, query.Message);
            Assert.AreEqual(1, query.RowCount);
            Assert.AreEqual("Bolt", query.Rows[0]["Name"]);
            Assert.AreEqual(42, Convert.ToInt32(query.Rows[0]["Qty"]));
        }

        [TestMethod]
        public void ExecuteProcedure_PositionalArgsAndDefault()
        {
            var session = this.NewDatabase();

            var results = session.ExecuteScript($@"
CREATE PROCEDURE dbo.AddItem @Name VARCHAR(50), @Qty INT = 7
AS
INSERT INTO Items (Name, Qty) VALUES (@Name, @Qty);
GO
EXEC dbo.AddItem 'Nut';
EXEC dbo.AddItem 'Washer', 3;
");
            AssertAllSucceed(results);

            var query = session.ExecuteQuery("SELECT Name, Qty FROM Items ORDER BY Id;");
            Assert.AreEqual(2, query.RowCount);
            Assert.AreEqual(7, Convert.ToInt32(query.Rows[0]["Qty"]));
            Assert.AreEqual(3, Convert.ToInt32(query.Rows[1]["Qty"]));
        }

        [TestMethod]
        public void DeclareAndSetVariable_UsedInInsert()
        {
            var session = this.NewDatabase();

            var results = session.ExecuteScript($@"
USE {this.dbName};
DECLARE @n VARCHAR(50) = 'Gear';
DECLARE @q INT = 5;
SET @q = @q + 10;
INSERT INTO Items (Name, Qty) VALUES (@n, @q);
");
            AssertAllSucceed(results);

            var query = session.ExecuteQuery("SELECT Name, Qty FROM Items;");
            Assert.AreEqual(1, query.RowCount);
            Assert.AreEqual("Gear", query.Rows[0]["Name"]);
            Assert.AreEqual(15, Convert.ToInt32(query.Rows[0]["Qty"]));
        }

        [TestMethod]
        public void ExecuteProcedure_OutputParameter_CopiesBackToCaller()
        {
            var session = this.NewDatabase();

            var results = session.ExecuteScript($@"
CREATE PROCEDURE dbo.AddNumbers @a INT, @b INT, @sum INT OUTPUT
AS
SET @sum = @a + @b;
GO
DECLARE @total INT;
EXEC dbo.AddNumbers 20, 22, @total OUTPUT;
INSERT INTO Items (Name, Qty) VALUES ('Sum', @total);
");
            AssertAllSucceed(results);

            var query = session.ExecuteQuery("SELECT Qty FROM Items WHERE Name = 'Sum';");
            Assert.AreEqual(1, query.RowCount);
            Assert.AreEqual(42, Convert.ToInt32(query.Rows[0]["Qty"]));
        }

        [TestMethod]
        public void MissingRequiredParameter_Fails()
        {
            var session = this.NewDatabase();

            var results = session.ExecuteScript($@"
CREATE PROCEDURE dbo.NeedTwo @a INT, @b INT
AS
INSERT INTO Items (Name, Qty) VALUES ('x', @a + @b);
GO
EXEC dbo.NeedTwo 1;
", continueOnError: true);

            Assert.IsTrue(results.Any(r => !r.Success), "Expected a failure for the missing parameter.");
        }

        [TestMethod]
        public void Procedure_Persists_And_ListedInSysProcedures()
        {
            var session = this.NewDatabase();
            Assert.IsTrue(session.ExecuteQuery(
                "CREATE PROCEDURE dbo.Noop AS SELECT 1;").Success);

            // A fresh TidalSql instance must see the persisted procedure.
            var reopened = new TidalSql();
            Assert.IsTrue(reopened.ExecuteQuery($"use {this.dbName}").Success);
            var list = reopened.ExecuteQuery("SELECT name FROM sys.procedures;");
            Assert.IsTrue(list.Success, list.Message);
            Assert.IsTrue(list.Rows.Any(r => Equals(r["name"], "Noop")));
        }

        [TestMethod]
        public void CreateOrAlterProcedure_WithBeginEndAndSetNocount_ReturnsResultSet()
        {
            var session = this.NewDatabase();
            Assert.IsTrue(session.ExecuteQuery(
                "CREATE TABLE dbo.Invoices (InvoiceId INT NOT NULL, CustomerId INT NOT NULL, InvoiceDate DATETIME2(0) NOT NULL, TotalAmount DECIMAL(12,2) NOT NULL);").Success);
            foreach (var v in new[]
            {
                "(1, 100, '2026-01-01', 50.00)",
                "(2, 100, '2026-03-01', 75.00)",
                "(3, 200, '2026-02-01', 10.00)",
            })
            {
                Assert.IsTrue(session.ExecuteQuery(
                    $"INSERT INTO Invoices (InvoiceId, CustomerId, InvoiceDate, TotalAmount) VALUES {v};").Success);
            }

            const string ddl = @"CREATE OR ALTER PROCEDURE dbo.GetCustomerInvoices
    @CustomerId INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT i.InvoiceId, i.CustomerId, i.InvoiceDate, i.TotalAmount
    FROM dbo.Invoices AS i
    WHERE i.CustomerId = @CustomerId
    ORDER BY i.InvoiceDate DESC;
END;";

            // First run creates it; a second run must succeed (OR ALTER replaces in place).
            Assert.IsTrue(session.ExecuteQuery(ddl).Success);
            Assert.IsTrue(session.ExecuteQuery(ddl).Success);

            var result = session.ExecuteQuery("EXEC dbo.GetCustomerInvoices @CustomerId = 100;");
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(2, result.RowCount);
            // ORDER BY InvoiceDate DESC => invoice 2 first, then 1.
            Assert.AreEqual(2, Convert.ToInt32(result.Rows[0]["InvoiceId"]));
            Assert.AreEqual(1, Convert.ToInt32(result.Rows[1]["InvoiceId"]));
            Assert.IsTrue(result.Rows.All(r => Convert.ToInt32(r["CustomerId"]) == 100));
        }

        [TestMethod]
        public void GetProcedureDefinition_ReturnsCreateOrAlterScript()
        {
            var session = this.NewDatabase();
            Assert.IsTrue(session.ExecuteQuery(
                "CREATE PROCEDURE dbo.AddItem @Name VARCHAR(50), @Qty INT = 7 AS INSERT INTO Items (Name, Qty) VALUES (@Name, @Qty);").Success);

            var def = session.GetProcedureDefinition(this.dbName, "AddItem").Replace("\r\n", "\n");

            StringAssert.StartsWith(def, "CREATE OR ALTER PROCEDURE dbo.AddItem");
            StringAssert.Contains(def, "@Name");
            StringAssert.Contains(def, "@Qty");
            StringAssert.Contains(def, "= 7");
            StringAssert.Contains(def, "\nAS\n");
            StringAssert.Contains(def, "INSERT");

            Assert.ThrowsException<Exception>(() => session.GetProcedureDefinition(this.dbName, "NoSuchProc"));
        }

        [TestMethod]
        public void DropProcedure_RemovesIt()
        {
            var session = this.NewDatabase();
            Assert.IsTrue(session.ExecuteQuery("CREATE PROCEDURE dbo.Temp AS SELECT 1;").Success);
            Assert.IsTrue(session.ExecuteQuery("DROP PROCEDURE dbo.Temp;").Success);

            var list = session.ExecuteQuery("SELECT name FROM sys.procedures;");
            Assert.IsFalse(list.Rows.Any(r => Equals(r["name"], "Temp")));

            // DROP of a missing procedure fails without IF EXISTS, succeeds with it.
            Assert.IsFalse(session.ExecuteQuery("DROP PROCEDURE dbo.Temp;").Success);
            Assert.IsTrue(session.ExecuteQuery("DROP PROCEDURE IF EXISTS dbo.Temp;").Success);
        }
    }
}
