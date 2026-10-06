namespace TidalDbTests
{
    using System.Linq;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using TidalDbServer.Tds;

    /// <summary>
    /// Unit tests for the SSMS/SMO compatibility layer that answers the connection-time metadata batches
    /// the engine cannot execute. These are the batches SSMS fires between login and a usable connection.
    /// </summary>
    [TestClass]
    public class SsmsCompatibilityTests
    {
        [TestMethod]
        public void ServerInfoProbe_ReturnsThreeResultSets()
        {
            const string sql =
                "DECLARE @edition sysname;\n" +
                "SET @edition = cast(SERVERPROPERTY(N'EDITION') as sysname);\n" +
                "SELECT case when @edition = N'SQL Azure' then 2 else 1 end as 'DatabaseEngineType'," +
                "SERVERPROPERTY('EngineEdition') AS DatabaseEngineEdition," +
                "SERVERPROPERTY('ProductVersion') AS ProductVersion," +
                "@@MICROSOFTVERSION AS MicrosoftVersion;\n" +
                "select host_platform from sys.dm_os_host_info";

            Assert.IsTrue(SsmsCompatibility.TryHandle(sql, out var sets));
            Assert.AreEqual(3, sets.Count);
            CollectionAssert.AreEqual(
                new[] { "DatabaseEngineType", "DatabaseEngineEdition", "ProductVersion", "MicrosoftVersion" },
                sets[0].Columns.ToArray());
            Assert.AreEqual(1, sets[0].Rows[0]["DatabaseEngineType"]);
            Assert.AreEqual("host_platform", sets[1].Columns.Single());
            Assert.AreEqual("ConnectionProtocol", sets[2].Columns.Single());
        }

        [TestMethod]
        public void AdminConnectionProbe_ReportsSmoEnabled()
        {
            const string sql =
                "IF (@@microsoftversion / 0x01000000) >= 9\n" +
                "SELECT CAST(0 AS BIT) AS N'AdminConnection', CAST(1 AS BIT) AS N'SmoEnabled'";

            Assert.IsTrue(SsmsCompatibility.TryHandle(sql, out var sets));
            Assert.AreEqual(1, sets.Count);
            Assert.AreEqual(false, sets[0].Rows[0]["AdminConnection"]);
            Assert.AreEqual(true, sets[0].Rows[0]["SmoEnabled"]);
        }

        [TestMethod]
        public void SetOnlyBatch_IsAcceptedWithNoResultSets()
        {
            Assert.IsTrue(SsmsCompatibility.TryHandle("SET LOCK_TIMEOUT 10000", out var sets));
            Assert.AreEqual(0, sets.Count);
        }

        [TestMethod]
        public void ScalarServerPropertySelect_IsEvaluated()
        {
            Assert.IsTrue(SsmsCompatibility.TryHandle(
                "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int) AS [EngineEdition]", out var sets));
            Assert.AreEqual(1, sets.Count);
            Assert.AreEqual("EngineEdition", sets[0].Columns.Single());
            Assert.AreEqual(3, sets[0].Rows[0]["EngineEdition"]);
        }

        [TestMethod]
        public void MultiColumnScalarSelect_EvaluatesEachExpression()
        {
            Assert.IsTrue(SsmsCompatibility.TryHandle(
                "SELECT SERVERPROPERTY('Edition') AS [Edition], " +
                "CONVERT(nvarchar(40), CONNECTIONPROPERTY('net_transport')) AS [transport], " +
                "@@MAX_PRECISION AS [prec]", out var sets));
            var row = sets[0].Rows[0];
            Assert.AreEqual("Developer Edition (64-bit)", row["Edition"]);
            Assert.AreEqual("TCP", row["transport"]);
            Assert.AreEqual(38, row["prec"]);
        }

        [TestMethod]
        public void OrdinaryUserQuery_IsNotHandled()
        {
            Assert.IsFalse(SsmsCompatibility.TryHandle("SELECT Id, Name FROM Customer", out _));
            Assert.IsFalse(SsmsCompatibility.TryHandle("SELECT 1", out _));
        }

        [TestMethod]
        public void HasDbAccessProbe_ReturnsGranted()
        {
            Assert.IsTrue(SsmsCompatibility.TryHandle("select has_dbaccess('msdb')", out var sets));
            Assert.AreEqual(1, sets.Count);
            Assert.AreEqual(1, sets[0].Rows[0][sets[0].Columns.Single()]);
        }

        [TestMethod]
        public void MetadataFunctionProbes_AreEvaluated()
        {
            Assert.IsTrue(SsmsCompatibility.TryHandle(
                "SELECT IS_SRVROLEMEMBER('sysadmin') AS [a], " +
                "SUSER_SNAME() AS [b], USER_NAME() AS [c], DB_NAME() AS [d], " +
                "SYSTEM_USER AS [e], ISNULL(DATABASEPROPERTYEX('x','y'), 'fallback') AS [f]",
                out var sets));
            var row = sets[0].Rows[0];
            Assert.AreEqual(1, row["a"]);
            Assert.AreEqual("admin", row["b"]);
            Assert.AreEqual("dbo", row["c"]);
            Assert.AreEqual("master", row["d"]);
            Assert.AreEqual("admin", row["e"]);
            Assert.AreEqual("fallback", row["f"]);
        }

        [TestMethod]
        public void CaseWithObjectIdProbe_EvaluatesElseBranch()
        {
            const string sql =
                "select \n" +
                "    case \n" +
                "        when object_id('dbo.sysdac_instances') is not null then 1 \n" +
                "    else \n" +
                "        0 \n" +
                "    end";

            Assert.IsTrue(SsmsCompatibility.TryHandle(sql, out var sets));
            Assert.AreEqual(1, sets.Count);
            Assert.AreEqual(0, sets[0].Rows[0][sets[0].Columns.Single()]);
        }

        [TestMethod]
        public void ImplicitAliasScalarSelect_IsEvaluated()
        {
            Assert.IsTrue(SsmsCompatibility.TryHandle(
                "SELECT SERVERPROPERTY('EngineEdition') EngineEdition, " +
                "SERVERPROPERTY('ProductVersion') ProductVersion", out var sets));
            var row = sets[0].Rows[0];
            Assert.AreEqual(3, row["EngineEdition"]);
            Assert.AreEqual("16.0.1000.6", row["ProductVersion"]);
        }
    }
}
