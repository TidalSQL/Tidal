namespace TidalSqlTests
{
    using System;
    using System.Data;
    using System.IO;
    using System.Net;
    using Microsoft.Data.SqlClient;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using TidalSqlLib;
    using TidalSqlServer.Tds;

    /// <summary>
    /// End-to-end tests that start the TDS server on an ephemeral loopback port and connect with the
    /// real <see cref="SqlConnection"/> client, exercising the PRELOGIN/LOGIN7 handshake and SQL batch
    /// result streaming. Milestone 1 is unencrypted, so connections use <c>Encrypt=False</c>.
    /// </summary>
    [TestClass]
    public class TdsProtocolTests
    {
        private const string AdminPassword = "admin";

        private TidalSql db = null!;
        private string dbName = null!;
        private string dbPath = null!;
        private TdsServer server = null!;
        private int port;

        [TestInitialize]
        public void TestInitialize()
        {
            new TidalSql().InitializeServer(AdminPassword);

            this.dbName = "TdsTest_" + Guid.NewGuid().ToString("N")[..8];
            this.db = new TidalSql();
            Assert.IsTrue(this.db.ExecuteQuery($"create database {this.dbName}").Success);
            Assert.IsTrue(this.db.ExecuteQuery($"use {this.dbName}").Success);
            this.dbPath = this.db.CurrentDatabasePath!;

            Assert.IsTrue(this.db.ExecuteQuery("CREATE TABLE Customer (Id INT, Name NVARCHAR(50));").Success);
            Assert.IsTrue(this.db.ExecuteQuery(
                "INSERT INTO Customer (Id, Name) VALUES (1, 'Alice'), (2, 'Bob'), (3, 'Carol');").Success);

            this.server = new TdsServer("TidalSqlTest");
            this.server.Start(new IPEndPoint(IPAddress.Loopback, 0));
            this.port = this.server.Endpoint.Port;
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try { this.server?.Dispose(); } catch { }
            try { Directory.Delete(this.dbPath, true); } catch { }
        }

        private string ConnectionString(string user, string password) =>
            new SqlConnectionStringBuilder
            {
                DataSource = $"127.0.0.1,{this.port}",
                UserID = user,
                Password = password,
                InitialCatalog = this.dbName,
                Encrypt = SqlConnectionEncryptOption.Optional,
                TrustServerCertificate = true,
                ConnectTimeout = 15,
                Pooling = false,
            }.ConnectionString;

        [TestMethod]
        public void SqlClient_CanConnectAndSelectRows()
        {
            using var conn = new SqlConnection(this.ConnectionString("admin", AdminPassword));
            conn.Open();

            using var cmd = new SqlCommand("SELECT Id, Name FROM Customer ORDER BY Id;", conn);
            using var reader = cmd.ExecuteReader();

            Assert.AreEqual(2, reader.FieldCount);
            Assert.AreEqual("Id", reader.GetName(0));
            Assert.AreEqual("Name", reader.GetName(1));

            var ids = new System.Collections.Generic.List<int>();
            var names = new System.Collections.Generic.List<string>();
            while (reader.Read())
            {
                ids.Add(Convert.ToInt32(reader.GetValue(0)));
                names.Add(Convert.ToString(reader.GetValue(1))!);
            }

            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, ids);
            CollectionAssert.AreEqual(new[] { "Alice", "Bob", "Carol" }, names);
        }

        [TestMethod]
        public void SqlClient_WrongPassword_FailsToLogin()
        {
            using var conn = new SqlConnection(this.ConnectionString("admin", "not-the-password"));
            var ex = Assert.ThrowsException<SqlException>(() => conn.Open());
            Assert.AreEqual(18456, ex.Number, "Expected a 'login failed' (18456) error.");
        }

        [TestMethod]
        public void SqlClient_ReportsFailedQueryAsError()
        {
            using var conn = new SqlConnection(this.ConnectionString("admin", AdminPassword));
            conn.Open();

            using var cmd = new SqlCommand("SELECT * FROM NoSuchTable;", conn);
            Assert.ThrowsException<SqlException>(() => cmd.ExecuteReader());
        }
    }
}
