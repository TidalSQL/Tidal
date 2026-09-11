namespace ParqBaseTests
{
    using System;
    using System.IO;
    using System.Linq;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using ParqBaseLib;

    [TestClass]
    public class SecurityTests
    {
        private ParqBase db = null!;
        private string dbName = null!;
        private string dbPath = null!;
        private string suffix = null!;

        [TestInitialize]
        public void TestInitialize()
        {
            this.suffix = Guid.NewGuid().ToString("N")[..8];
            this.dbName = "SecTest_" + this.suffix;

            this.db = new ParqBase();
            Assert.IsTrue(this.db.ExecuteQuery($"create database {this.dbName}").Success);
            Assert.IsTrue(this.db.ExecuteQuery($"use {this.dbName}").Success);
            this.dbPath = this.db.CurrentDatabasePath!;

            // Seed a demo table (as the implicit administrator).
            Ok(this.db.ExecuteQuery("CREATE TABLE Customer (Id INT, Name NVARCHAR(50));"));
            Ok(this.db.ExecuteQuery("INSERT INTO Customer (Id, Name) VALUES (1, 'Alice'), (2, 'Bob');"));
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try { Directory.Delete(this.dbPath, true); } catch { }
        }

        private static void Ok(QueryResult r) => Assert.IsTrue(r.Success, r.Message);

        private string Login(string name) => $"{name}_{this.suffix}";

        // ---- Principals & catalog views ----------------------------------------

        [TestMethod]
        public void CreateLogin_AppearsInServerPrincipals()
        {
            var login = this.Login("Don");
            Ok(this.db.ExecuteQuery($"CREATE LOGIN {login} WITH PASSWORD = 'S3cret!';"));

            var result = this.db.ExecuteQuery("SELECT name, type FROM sys.server_principals;");
            Ok(result);
            Assert.IsTrue(result.Rows.Any(r => (string)r["name"]! == login && (string)r["type"]! == "SQL_LOGIN"));
        }

        [TestMethod]
        public void CreateUser_ForLogin_AppearsInDatabasePrincipals()
        {
            var login = this.Login("Don");
            Ok(this.db.ExecuteQuery($"CREATE LOGIN {login} WITH PASSWORD = 'S3cret!';"));
            Ok(this.db.ExecuteQuery($"CREATE USER Don FOR LOGIN {login};"));

            var result = this.db.ExecuteQuery("SELECT name, type FROM sys.database_principals;");
            Ok(result);
            Assert.IsTrue(result.Rows.Any(r => (string)r["name"]! == "Don" && (string)r["type"]! == "SQL_USER"));
        }

        [TestMethod]
        public void CreateUser_ForMissingLogin_Fails()
        {
            var result = this.db.ExecuteQuery("CREATE USER Ghost FOR LOGIN NoSuchLogin;");
            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.Message, "does not exist");
        }

        [TestMethod]
        public void FixedRoles_AreSeeded()
        {
            var result = this.db.ExecuteQuery("SELECT name FROM sys.database_principals;");
            Ok(result);
            foreach (var role in new[] { "db_owner", "db_datareader", "db_datawriter", "db_ddladmin", "public" })
            {
                Assert.IsTrue(result.Rows.Any(r => (string)r["name"]! == role), $"missing fixed role {role}");
            }
        }

        [TestMethod]
        public void CreateRole_AddMember_AppearsInRoleMembers()
        {
            Ok(this.db.ExecuteQuery("CREATE USER Don WITHOUT LOGIN;"));
            Ok(this.db.ExecuteQuery("CREATE ROLE SalesReaders;"));
            Ok(this.db.ExecuteQuery("ALTER ROLE SalesReaders ADD MEMBER Don;"));

            var result = this.db.ExecuteQuery("SELECT role_name, member_name FROM sys.database_role_members;");
            Ok(result);
            Assert.IsTrue(result.Rows.Any(r =>
                (string)r["role_name"]! == "SalesReaders" && (string)r["member_name"]! == "Don"));

            Ok(this.db.ExecuteQuery("ALTER ROLE SalesReaders DROP MEMBER Don;"));
            var after = this.db.ExecuteQuery("SELECT role_name, member_name FROM sys.database_role_members;");
            Assert.IsFalse(after.Rows.Any(r =>
                (string)r["role_name"]! == "SalesReaders" && (string)r["member_name"]! == "Don"));
        }

        // ---- Authorization: GRANT / DENY / REVOKE ------------------------------

        [TestMethod]
        public void Grant_Select_AllowsImpersonatedUserToRead()
        {
            Ok(this.db.ExecuteQuery("CREATE USER Don WITHOUT LOGIN;"));
            Ok(this.db.ExecuteQuery("GRANT SELECT ON Customer TO Don;"));

            Ok(this.db.ExecuteQuery("EXECUTE AS USER = 'Don';"));
            Assert.AreEqual("Don", this.db.CurrentUser);
            var result = this.db.ExecuteQuery("SELECT Name FROM Customer;");
            Ok(result);
            Assert.AreEqual(2, result.RowCount);

            Ok(this.db.ExecuteQuery("REVERT;"));
            Assert.IsNull(this.db.CurrentUser);
        }

        [TestMethod]
        public void NoGrant_Select_IsDenied()
        {
            Ok(this.db.ExecuteQuery("CREATE USER Don WITHOUT LOGIN;"));
            Ok(this.db.ExecuteQuery("EXECUTE AS USER = 'Don';"));

            var result = this.db.ExecuteQuery("SELECT Name FROM Customer;");
            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.Message, "Permission denied");
        }

        [TestMethod]
        public void Deny_Overrides_Grant()
        {
            Ok(this.db.ExecuteQuery("CREATE USER Don WITHOUT LOGIN;"));
            Ok(this.db.ExecuteQuery("GRANT SELECT ON Customer TO Don;"));
            Ok(this.db.ExecuteQuery("DENY SELECT ON Customer TO Don;"));

            Ok(this.db.ExecuteQuery("EXECUTE AS USER = 'Don';"));
            var result = this.db.ExecuteQuery("SELECT Name FROM Customer;");
            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.Message, "Permission denied");
        }

        [TestMethod]
        public void Revoke_RemovesGrant()
        {
            Ok(this.db.ExecuteQuery("CREATE USER Don WITHOUT LOGIN;"));
            Ok(this.db.ExecuteQuery("GRANT SELECT ON Customer TO Don;"));
            Ok(this.db.ExecuteQuery("REVOKE SELECT ON Customer FROM Don;"));

            Ok(this.db.ExecuteQuery("EXECUTE AS USER = 'Don';"));
            var result = this.db.ExecuteQuery("SELECT Name FROM Customer;");
            Assert.IsFalse(result.Success);
        }

        [TestMethod]
        public void Insert_Requires_InsertPermission()
        {
            Ok(this.db.ExecuteQuery("CREATE USER Don WITHOUT LOGIN;"));
            Ok(this.db.ExecuteQuery("GRANT SELECT ON Customer TO Don;"));
            Ok(this.db.ExecuteQuery("EXECUTE AS USER = 'Don';"));

            var denied = this.db.ExecuteQuery("INSERT INTO Customer (Id, Name) VALUES (3, 'Carol');");
            Assert.IsFalse(denied.Success, "INSERT should be denied with only SELECT granted.");

            Ok(this.db.ExecuteQuery("REVERT;"));
            Ok(this.db.ExecuteQuery("GRANT INSERT ON Customer TO Don;"));
            Ok(this.db.ExecuteQuery("EXECUTE AS USER = 'Don';"));
            Ok(this.db.ExecuteQuery("INSERT INTO Customer (Id, Name) VALUES (3, 'Carol');"));
        }

        // ---- Roles carry permissions -------------------------------------------

        [TestMethod]
        public void RoleGrant_IsInheritedByMembers()
        {
            Ok(this.db.ExecuteQuery("CREATE USER Don WITHOUT LOGIN;"));
            Ok(this.db.ExecuteQuery("CREATE ROLE SalesReaders;"));
            Ok(this.db.ExecuteQuery("ALTER ROLE SalesReaders ADD MEMBER Don;"));
            Ok(this.db.ExecuteQuery("GRANT SELECT ON Customer TO SalesReaders;"));

            Ok(this.db.ExecuteQuery("EXECUTE AS USER = 'Don';"));
            var result = this.db.ExecuteQuery("SELECT Name FROM Customer;");
            Ok(result);
            Assert.AreEqual(2, result.RowCount);
        }

        [TestMethod]
        public void FixedRole_DbDatareader_AllowsSelect()
        {
            Ok(this.db.ExecuteQuery("CREATE USER Don WITHOUT LOGIN;"));
            Ok(this.db.ExecuteQuery("ALTER ROLE db_datareader ADD MEMBER Don;"));

            Ok(this.db.ExecuteQuery("EXECUTE AS USER = 'Don';"));
            var result = this.db.ExecuteQuery("SELECT Name FROM Customer;");
            Ok(result);
            Assert.AreEqual(2, result.RowCount);
        }

        // ---- Schema securables & inheritance -----------------------------------

        [TestMethod]
        public void SchemaGrant_CoversTablesInSchema()
        {
            Ok(this.db.ExecuteQuery("CREATE USER Don WITHOUT LOGIN;"));
            Ok(this.db.ExecuteQuery("CREATE SCHEMA Sales;"));
            Ok(this.db.ExecuteQuery("CREATE TABLE Sales.Orders (Id INT);"));
            Ok(this.db.ExecuteQuery("INSERT INTO Sales.Orders (Id) VALUES (10), (20);"));
            Ok(this.db.ExecuteQuery("GRANT SELECT ON SCHEMA::Sales TO Don;"));

            // Table is stored namespaced by its schema.
            Assert.IsTrue(File.Exists(Path.Combine(this.dbPath, "tables", "Sales.Orders.parquet")));

            Ok(this.db.ExecuteQuery("EXECUTE AS USER = 'Don';"));
            var result = this.db.ExecuteQuery("SELECT Id FROM Sales.Orders;");
            Ok(result);
            Assert.AreEqual(2, result.RowCount);
        }

        [TestMethod]
        public void SchemaOwner_HasControlOverItsObjects()
        {
            Ok(this.db.ExecuteQuery("CREATE USER Don WITHOUT LOGIN;"));
            Ok(this.db.ExecuteQuery("CREATE SCHEMA Sales AUTHORIZATION Don;"));
            Ok(this.db.ExecuteQuery("CREATE TABLE Sales.Orders (Id INT);"));
            Ok(this.db.ExecuteQuery("INSERT INTO Sales.Orders (Id) VALUES (1);"));

            // No explicit grant to Don, but he owns the schema.
            Ok(this.db.ExecuteQuery("EXECUTE AS USER = 'Don';"));
            var result = this.db.ExecuteQuery("SELECT Id FROM Sales.Orders;");
            Ok(result);
            Assert.AreEqual(1, result.RowCount);
        }

        [TestMethod]
        public void CreateTable_InMissingSchema_Fails()
        {
            var result = this.db.ExecuteQuery("CREATE TABLE Nope.Thing (Id INT);");
            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.Message, "Schema 'Nope' does not exist");
        }

        // ---- DDL & security administration -------------------------------------

        [TestMethod]
        public void Ddl_Denied_ForPlainUser_AllowedForDdlAdmin()
        {
            Ok(this.db.ExecuteQuery("CREATE USER Don WITHOUT LOGIN;"));

            Ok(this.db.ExecuteQuery("EXECUTE AS USER = 'Don';"));
            var denied = this.db.ExecuteQuery("CREATE TABLE Extra (Id INT);");
            Assert.IsFalse(denied.Success, "plain user should not create tables");
            Ok(this.db.ExecuteQuery("REVERT;"));

            Ok(this.db.ExecuteQuery("ALTER ROLE db_ddladmin ADD MEMBER Don;"));
            Ok(this.db.ExecuteQuery("EXECUTE AS USER = 'Don';"));
            Ok(this.db.ExecuteQuery("CREATE TABLE Extra (Id INT);"));
        }

        [TestMethod]
        public void ManageSecurity_Denied_ForPlainUser()
        {
            Ok(this.db.ExecuteQuery("CREATE USER Don WITHOUT LOGIN;"));
            Ok(this.db.ExecuteQuery("CREATE USER Eve WITHOUT LOGIN;"));

            Ok(this.db.ExecuteQuery("EXECUTE AS USER = 'Don';"));
            var denied = this.db.ExecuteQuery("GRANT SELECT ON Customer TO Eve;");
            Assert.IsFalse(denied.Success);
            StringAssert.Contains(denied.Message, "Permission denied");
        }

        [TestMethod]
        public void DbOwner_BypassesChecks()
        {
            Ok(this.db.ExecuteQuery("CREATE USER Don WITHOUT LOGIN;"));
            Ok(this.db.ExecuteQuery("ALTER ROLE db_owner ADD MEMBER Don;"));

            Ok(this.db.ExecuteQuery("EXECUTE AS USER = 'Don';"));
            Ok(this.db.ExecuteQuery("SELECT Name FROM Customer;"));
            Ok(this.db.ExecuteQuery("INSERT INTO Customer (Id, Name) VALUES (9, 'Zed');"));
            Ok(this.db.ExecuteQuery("CREATE TABLE OwnerMade (Id INT);"));
        }

        [TestMethod]
        public void ExecuteAs_NonexistentUser_Fails()
        {
            var result = this.db.ExecuteQuery("EXECUTE AS USER = 'Nobody';");
            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.Message, "does not exist");
        }

        [TestMethod]
        public void DatabasePermissions_View_ReflectsGrants()
        {
            Ok(this.db.ExecuteQuery("CREATE USER Don WITHOUT LOGIN;"));
            Ok(this.db.ExecuteQuery("GRANT SELECT ON Customer TO Don;"));

            var result = this.db.ExecuteQuery(
                "SELECT grantee, permission, class, securable, state FROM sys.database_permissions;");
            Ok(result);
            Assert.IsTrue(result.Rows.Any(r =>
                (string)r["grantee"]! == "Don" &&
                (string)r["permission"]! == "SELECT" &&
                (string)r["class"]! == "OBJECT" &&
                (string)r["securable"]! == "dbo.Customer" &&
                (string)r["state"]! == "GRANT"));
        }

        // ---- Authentication: default admin + login gate ------------------------

        [TestMethod]
        public void InitializeServer_CreatesDefaultAdmin_OnlyOnce()
        {
            // The default admin lives in the shared system folder; it may already exist from a
            // prior run. Either way, after initializing it must be usable for authentication.
            var first = this.db.InitializeServer("admin");
            Assert.AreEqual("admin", first.AdminLogin);

            // A second initialize is idempotent: it never re-creates or re-reveals the password.
            var second = this.db.InitializeServer("admin");
            Assert.IsFalse(second.Created);
            Assert.IsNull(second.Password);
        }

        [TestMethod]
        public void Login_WithDefaultAdminCredentials_Succeeds()
        {
            this.db.InitializeServer("admin");

            var fresh = new ParqBase();
            Assert.IsFalse(fresh.IsAuthenticated);
            Assert.IsTrue(fresh.Login("admin", "admin"));
            Assert.IsTrue(fresh.IsAuthenticated);
            Assert.AreEqual("admin", fresh.CurrentLogin);
        }

        [TestMethod]
        public void Login_WithWrongPassword_Fails()
        {
            this.db.InitializeServer("admin");

            var fresh = new ParqBase();
            Assert.IsFalse(fresh.Login("admin", "not-the-password"));
            Assert.IsFalse(fresh.IsAuthenticated);
            Assert.IsNull(fresh.CurrentLogin);
        }

        [TestMethod]
        public void Login_WithUnknownLogin_Fails()
        {
            this.db.InitializeServer("admin");

            var fresh = new ParqBase();
            Assert.IsFalse(fresh.Login("no_such_login_" + this.suffix, "admin"));
            Assert.IsFalse(fresh.IsAuthenticated);
        }

        [TestMethod]
        public void Login_AsCreatedLogin_Succeeds()
        {
            var login = this.Login("Analyst");
            Ok(this.db.ExecuteQuery($"CREATE LOGIN {login} WITH PASSWORD = 'P@ssw0rd!';"));

            var fresh = new ParqBase();
            Assert.IsTrue(fresh.Login(login, "P@ssw0rd!"));
            Assert.IsFalse(fresh.Login(login, "wrong"));
        }
    }
}
