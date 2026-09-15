namespace ParqBaseTests
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using ParqBaseLib;

    [TestClass]
    public class CancellationTests
    {
        private string dbName = null!;
        private string dbPath = null!;

        [TestInitialize]
        public void TestInitialize()
        {
            this.dbName = "CancelTest_" + Guid.NewGuid().ToString("N")[..8];
            var session = new ParqBase();
            Assert.IsTrue(session.ExecuteQuery($"create database {this.dbName}").Success);
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);
            this.dbPath = session.CurrentDatabasePath!;
            Assert.IsTrue(session.ExecuteQuery(
                "CREATE TABLE dbo.Nums (Id INT IDENTITY(1,1) NOT NULL, N INT NOT NULL);").Success);
            var tuples = string.Join(",", System.Linq.Enumerable.Range(0, 200).Select(n => $"({n})"));
            Assert.IsTrue(session.ExecuteQuery($"INSERT INTO Nums (N) VALUES {tuples};").Success);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try { Directory.Delete(this.dbPath, true); } catch { }
        }

        [TestMethod]
        public void ExecuteQuery_WithAlreadyCancelledToken_ThrowsOperationCanceled()
        {
            var session = new ParqBase();
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsException<OperationCanceledException>(
                () => session.ExecuteQuery("SELECT * FROM Nums;", cts.Token));
        }

        [TestMethod]
        public void ExecuteScript_WithAlreadyCancelledToken_ThrowsOperationCanceled()
        {
            var session = new ParqBase();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsException<OperationCanceledException>(
                () => session.ExecuteScript($"USE {this.dbName};\nGO\nSELECT * FROM Nums;", false, cts.Token));
        }

        [TestMethod]
        public void ExecuteQuery_WithoutCancellation_StillSucceeds()
        {
            var session = new ParqBase();
            Assert.IsTrue(session.ExecuteQuery($"use {this.dbName}").Success);

            var result = session.ExecuteQuery("SELECT * FROM Nums;");
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(200, result.RowCount);
        }
    }
}
