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
        public void ServerSettingsProbe_ReturnsSingleRowWithKeyColumns()
        {
            // SMO's Server-properties batch (multi-statement DECLARE/SET/SELECT). It gates Object
            // Explorer: if it fails, SMO aborts before enumerating databases.
            const string sql =
                "DECLARE @edition sysname;\n" +
                "SELECT\n" +
                "@SmoAuditLevel AS [AuditLevel],\n" +
                "(case when @SmoLoginMode < 3 then @SmoLoginMode else 9 end) AS [LoginMode],\n" +
                "CAST(@@MAX_PRECISION AS int) AS [MaxPrecision],\n" +
                "@MasterPath AS [MasterDBPath],\n" +
                "CAST(@@SERVICENAME AS sysname) AS [ServiceName]";

            Assert.IsTrue(SsmsCompatibility.TryHandle(sql, out var sets));
            Assert.AreEqual(1, sets.Count);
            var row = sets[0].Rows.Single();
            CollectionAssert.Contains(sets[0].Columns.ToArray(), "AuditLevel");
            CollectionAssert.Contains(sets[0].Columns.ToArray(), "LoginMode");
            CollectionAssert.Contains(sets[0].Columns.ToArray(), "MasterDBPath");
            // MaxPrecision/SqlCharSet/SqlSortOrder are tinyint-typed (byte) so SMO reads them via GetByte.
            Assert.IsInstanceOfType(row["MaxPrecision"], typeof(byte));
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
        public void UrnConcatWithQuotename_IsEvaluated()
        {
            var machine = System.Environment.MachineName;
            Assert.IsTrue(SsmsCompatibility.TryHandle(
                "SELECT\n" +
                "CAST(SERVERPROPERTY(N'Servername') AS sysname) AS [Name],\n" +
                "'IntegrationServices[@Name=' + quotename(CAST(SERVERPROPERTY(N'Servername') AS sysname),'''') + ']' AS [Urn]\n" +
                "ORDER BY\n[Name] ASC", out var sets));
            var row = sets[0].Rows[0];
            Assert.AreEqual(machine, row["Name"]);
            Assert.AreEqual($"IntegrationServices[@Name='{machine}']", row["Urn"]);
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

        [TestMethod]
        public void DatabasesEnumeration_UserFolder_EchoesSelectListInOrderWithTypedValues()
        {
            // SSMS reads the result BY ORDINAL, so the synthesized columns must be exactly the SELECT
            // aliases, in order, each with the CLR type matching its SQL wire type.
            const string sql =
                "SELECT\n" +
                "dtb.name AS [Database_Name],\n" +
                "CAST(dtb.ansi_null_default_on AS bit) AS [Database_AnsiNullDefault],\n" +
                "dtb.database_id AS [ID],\n" +
                "dtb.compatibility_level AS [Database_CompatibilityLevel],\n" +
                "dtb.recovery_model AS [RecoveryModel],\n" +
                "dtb.user_access AS [UserAccess],\n" +
                "dtb.service_broker_guid AS [Database_ServiceBrokerGuid],\n" +
                "(case dtb.state when 0 then 1 else 0 end) AS [Database_Status],\n" +
                "(CASE WHEN dtb.name IN ('master','model','msdb','tempdb') THEN 1 ELSE 0 END) AS [IsSystemObject]\n" +
                "FROM master.sys.databases dtb\n" +
                "WHERE (CAST(CASE WHEN dtb.name IN ('master','model','msdb','tempdb') THEN 1 ELSE 0 END AS bit)=N'0')\n" +
                "ORDER BY [Database_Name] ASC";

            var dbs = new[]
            {
                new TidalDbLib.TidalDb.ExplorerDatabase("alpha", 5, new System.DateTime(2021, 2, 3)),
                new TidalDbLib.TidalDb.ExplorerDatabase("beta", 6, new System.DateTime(2022, 4, 5)),
            };

            Assert.IsTrue(SsmsCompatibility.TryHandle(sql, () => dbs, out var sets));
            Assert.AreEqual(1, sets.Count);
            Assert.AreEqual(2, sets[0].Rows.Count);

            // Columns are exactly the SELECT aliases, in SELECT order.
            CollectionAssert.AreEqual(
                new[]
                {
                    "Database_Name", "Database_AnsiNullDefault", "ID", "Database_CompatibilityLevel",
                    "RecoveryModel", "UserAccess", "Database_ServiceBrokerGuid", "Database_Status",
                    "IsSystemObject",
                },
                sets[0].Columns.ToArray());

            var alpha = sets[0].Rows[0];
            Assert.AreEqual("alpha", alpha["Database_Name"]);
            Assert.AreEqual(5, alpha["ID"]);
            Assert.AreEqual(false, alpha["Database_AnsiNullDefault"]);
            Assert.AreEqual(false, alpha["IsSystemObject"]);
            Assert.AreEqual(1, alpha["Database_Status"]);
            // compatibility_level/recovery_model/user_access are tinyint; service_broker_guid is a GUID.
            Assert.IsInstanceOfType(alpha["Database_CompatibilityLevel"], typeof(byte));
            Assert.IsInstanceOfType(alpha["RecoveryModel"], typeof(byte));
            Assert.IsInstanceOfType(alpha["UserAccess"], typeof(byte));
            Assert.IsInstanceOfType(alpha["Database_ServiceBrokerGuid"], typeof(System.Guid));
        }

        [TestMethod]
        public void DatabasesEnumeration_SystemFilter_ReturnsSystemDatabases()
        {
            const string sql =
                "SELECT dtb.name AS [Name], dtb.database_id AS [ID],\n" +
                "(CASE WHEN dtb.name IN ('master') THEN 1 ELSE 0 END) AS [IsSystemObject]\n" +
                "FROM master.sys.databases dtb\n" +
                "WHERE (CAST(CASE WHEN dtb.name IN ('master','model','msdb','tempdb') THEN 1 ELSE dtb.is_distributor END AS bit)=N'1')";

            var dbs = new[] { new TidalDbLib.TidalDb.ExplorerDatabase("alpha", 5, System.DateTime.Now) };
            Assert.IsTrue(SsmsCompatibility.TryHandle(sql, () => dbs, out var sets));
            Assert.AreEqual(4, sets[0].Rows.Count);
            Assert.AreEqual("master", sets[0].Rows[0]["Name"]);
            Assert.AreEqual(true, sets[0].Rows[0]["IsSystemObject"]);
        }

        [TestMethod]
        public void DatabasesEnumeration_SnapshotFolder_ReturnsNoRows()
        {
            const string sql =
                "SELECT dtb.name AS [Database_Name], dtb.database_id AS [ID]\n" +
                "FROM master.sys.databases dtb\n" +
                "WHERE (CAST(isnull(dtb.source_database_id, 0) AS bit)=N'1')";

            var dbs = new[] { new TidalDbLib.TidalDb.ExplorerDatabase("alpha", 5, System.DateTime.Now) };
            Assert.IsTrue(SsmsCompatibility.TryHandle(sql, () => dbs, out var sets));
            Assert.AreEqual(0, sets[0].Rows.Count);
            CollectionAssert.AreEqual(new[] { "Database_Name", "ID" }, sets[0].Columns.ToArray());
        }

        [TestMethod]
        public void DatabasesEnumeration_WithoutSource_IsNotHandled()
        {
            const string sql =
                "SELECT dtb.name AS [Name], CAST(dtb.ansi_null_default_on AS bit) AS [AnsiNullDefault]\n" +
                "FROM master.sys.databases dtb";

            // The parameterless overload has no database source, so the batch falls through to the engine.
            Assert.IsFalse(SsmsCompatibility.TryHandle(sql, out _));
        }

        [TestMethod]
        public void DatabasesEnumeration_RealObjectExplorerQuery_PopulatesUserDatabases()
        {
            // The exact shape SSMS Object Explorer sends (via RPC sp_executesql): Database_-prefixed
            // aliases, quotename()/suser_sname(), bitwise status, hex literals, "=N'0'" bit comparison.
            const string sql =
                "SELECT\n" +
                "dtb.name AS [Database_Name],\n" +
                "'Server[@Name=' + quotename('MACHINE','''') + ']/Database[@Name=' + quotename(dtb.name,'''') + ']' AS [Database_Urn],\n" +
                "(case when dtb.state = 0 then 1 else 0 end | 0x0 | 0x200) AS [Database_Status],\n" +
                "dtb.compatibility_level AS [Database_CompatibilityLevel],\n" +
                "CAST(0 AS bit) AS [Database_IsSqlDw],\n" +
                "dtb.is_fulltext_enabled AS [Database_IsFullTextEnabled],\n" +
                "dtb.recovery_model AS [RecoveryModel],\n" +
                "dtb.user_access AS [UserAccess],\n" +
                "dtb.is_read_only AS [ReadOnly],\n" +
                "suser_sname(dtb.owner_sid) AS [Database_Owner],\n" +
                "dtb.name AS [Database_DatabaseName2]\n" +
                "FROM\n" +
                "master.sys.databases AS dtb\n" +
                "WHERE\n" +
                "(CAST(case when dtb.name in ('master','model','msdb','tempdb') then 1 else dtb.is_distributor end AS bit)=N'0')\n" +
                "ORDER BY\n" +
                "[Database_Name] ASC";

            var dbs = new[]
            {
                new TidalDbLib.TidalDb.ExplorerDatabase("Sales", 5, new System.DateTime(2021, 2, 3)),
                new TidalDbLib.TidalDb.ExplorerDatabase("tpch", 6, new System.DateTime(2022, 4, 5)),
            };

            Assert.IsTrue(SsmsCompatibility.TryHandle(sql, () => dbs, out var sets));
            Assert.AreEqual(1, sets.Count);
            Assert.AreEqual(2, sets[0].Rows.Count);
            Assert.AreEqual("Database_Name", sets[0].Columns[0]);

            var sales = sets[0].Rows[0];
            Assert.AreEqual("Sales", sales["Database_Name"]);
            StringAssert.Contains(sales["Database_Urn"]!.ToString(), "Database[@Name='Sales']");
            Assert.AreEqual(1, sales["Database_Status"]);
            Assert.IsInstanceOfType(sales["Database_CompatibilityLevel"], typeof(byte));
            Assert.AreEqual(false, sales["Database_IsSqlDw"]);
            Assert.AreEqual("sa", sales["Database_Owner"]);
        }

        [TestMethod]
        public void DatabasesEnumeration_UserFolderExcludingSnapshots_ReturnsUserDatabases()
        {
            // The real Object Explorer user-Databases WHERE excludes snapshots via
            // "isnull(dtb.source_database_id,0) AS bit)=N'0'". This must NOT be mistaken
            // for the snapshots subfolder (which uses "=N'1'") and return the user DBs.
            const string sql =
                "SELECT dtb.name AS [Database_Name], dtb.database_id AS [ID]\n" +
                "FROM master.sys.databases AS dtb\n" +
                "WHERE\n" +
                "(CAST(case when dtb.name in ('master','model','msdb','tempdb') then 1 else dtb.is_distributor end AS bit)=N'0'" +
                " and CAST(isnull(dtb.source_database_id, 0) AS bit)=N'0')\n" +
                "ORDER BY [Database_Name] ASC";

            var dbs = new[]
            {
                new TidalDbLib.TidalDb.ExplorerDatabase("Sales", 5, new System.DateTime(2021, 2, 3)),
                new TidalDbLib.TidalDb.ExplorerDatabase("tpch", 6, new System.DateTime(2022, 4, 5)),
            };

            Assert.IsTrue(SsmsCompatibility.TryHandle(sql, () => dbs, out var sets));
            Assert.AreEqual(1, sets.Count);
            Assert.AreEqual(2, sets[0].Rows.Count);
            Assert.AreEqual("Sales", sets[0].Rows[0]["Database_Name"]);
            Assert.AreEqual("tpch", sets[0].Rows[1]["Database_Name"]);
        }

        [TestMethod]
        public void DatabasesEnumeration_WithHadrPreamble_UsesProjectionColumnsNotPreamble()
        {
            // The real Object Explorer query mentions "sys.databases AS dtb" three times: twice in the
            // HADR temp-table preamble and once in the real projection. Column/row extraction must key
            // off the LAST (projection) occurrence, else SSMS gets the preamble's 2 columns and cancels.
            const string sql =
                "declare @HasViewPermission int\n" +
                "insert into #tmp_sync_states\n" +
                "select dtb.database_id, hadrd.synchronization_state\n" +
                "from sys.databases AS dtb\n" +
                "join #tmp_db_hadr_dbrs AS hadrd ON hadrd.database_id = dtb.database_id\n" +
                "SELECT\n" +
                "dtb.name AS [Database_Name],\n" +
                "dtb.compatibility_level AS [Database_CompatibilityLevel],\n" +
                "suser_sname(dtb.owner_sid) AS [Database_Owner]\n" +
                "FROM\n" +
                "master.sys.databases AS dtb\n" +
                "LEFT OUTER JOIN sys.database_mirroring AS dmi ON dmi.database_id = dtb.database_id\n" +
                "WHERE\n" +
                "(CAST(case when dtb.name in ('master','model','msdb','tempdb') then 1 else dtb.is_distributor end AS bit)=N'0'" +
                " and CAST(isnull(dtb.source_database_id, 0) AS bit)=N'0')\n" +
                "ORDER BY [Database_Name] ASC";

            var dbs = new[]
            {
                new TidalDbLib.TidalDb.ExplorerDatabase("Sales", 5, new System.DateTime(2021, 2, 3)),
                new TidalDbLib.TidalDb.ExplorerDatabase("tpch", 6, new System.DateTime(2022, 4, 5)),
            };

            Assert.IsTrue(SsmsCompatibility.TryHandle(sql, () => dbs, out var sets));
            Assert.AreEqual(1, sets.Count);
            CollectionAssert.AreEqual(
                new[] { "Database_Name", "Database_CompatibilityLevel", "Database_Owner" },
                sets[0].Columns.ToArray());
            Assert.AreEqual(2, sets[0].Rows.Count);
            Assert.AreEqual("Sales", sets[0].Rows[0]["Database_Name"]);
            Assert.AreEqual("tpch", sets[0].Rows[1]["Database_Name"]);
        }

        [TestMethod]
        public void ConfigurationsProbe_ReturnsLanguageAndCutoffDefaults()
        {
            const string sql =
                "select case when cfg.configuration_id = 124 then 1 else cfg.value_in_use end as value,\n" +
                "cfg.name as name, cfg.configuration_id as configuration_id\n" +
                "from sys.configurations as cfg\n" +
                "where cfg.configuration_id in (115, 124, 1126, 1127, 1555) order by cfg.configuration_id asc";

            Assert.IsTrue(SsmsCompatibility.TryHandle(sql, out var sets));
            Assert.AreEqual(1, sets.Count);
            Assert.AreEqual(5, sets[0].Rows.Count);
            CollectionAssert.AreEqual(new[] { "value", "name", "configuration_id" }, sets[0].Columns.ToArray());
            var defaultLang = sets[0].Rows.Single(r => (int)r["configuration_id"]! == 124);
            Assert.AreEqual(1033, defaultLang["value"]);
            Assert.AreEqual("us_english", defaultLang["name"]);
        }

        [TestMethod]
        public void PolicyStoreProbe_ReturnsDisabledPolicyRow()
        {
            const string sql =
                "SELECT\n" +
                "'PolicyStore[@Name=' + quotename(CAST(serverproperty(N'Servername') AS sysname),'''') + ']' AS [Urn],\n" +
                "CAST(serverproperty(N'Servername') AS sysname) AS [Name],\n" +
                "CAST((SELECT current_value FROM msdb.dbo.syspolicy_configuration WHERE name = 'Enabled') AS bit) AS [Enabled],\n" +
                "CAST((SELECT current_value FROM msdb.dbo.syspolicy_configuration WHERE name = 'HistoryRetentionInDays') AS int) AS [HistoryRetentionInDays],\n" +
                "CAST((SELECT current_value FROM msdb.dbo.syspolicy_configuration WHERE name = 'LogOnSuccess') AS bit) AS [LogOnSuccess]\n" +
                "WHERE (CAST(serverproperty(N'Servername') AS sysname)=N'HOST')";

            Assert.IsTrue(SsmsCompatibility.TryHandle(sql, out var sets));
            Assert.AreEqual(1, sets.Count);
            Assert.AreEqual(1, sets[0].Rows.Count);
            var row = sets[0].Rows[0];
            Assert.AreEqual(false, row["Enabled"]);
            Assert.AreEqual(0, row["HistoryRetentionInDays"]);
            Assert.AreEqual(false, row["LogOnSuccess"]);
            StringAssert.StartsWith((string)row["Urn"]!, "PolicyStore[@Name=");
        }
    }
}
