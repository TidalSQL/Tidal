namespace ParqBaseTests
{
    using System;
    using System.IO;
    using System.Linq;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using ParqBaseLib;

    /// <summary>
    /// Tests for web-style authentication/authorization: a login sees only the databases and tables
    /// it has access to, a sysadmin sees everything, and a logged-out session sees nothing. Access is
    /// enforced by auto-impersonating a login's mapped database user on USE.
    /// </summary>
    [TestClass]
    public class WebAccessTests
    {
        private ParqBase admin = null!;
        private string suffix = null!;
        private string dbName = null!;
        private string dbPath = null!;
        private string otherName = null!;
        private string otherPath = null!;
        private string loginName = null!;

        [TestInitialize]
        public void TestInitialize()
        {
            this.suffix = Guid.NewGuid().ToString("N")[..8];
            this.dbName = "WebTest_" + this.suffix;
            this.otherName = "WebOther_" + this.suffix;
            this.loginName = "weblogin_" + this.suffix;

            this.admin = new ParqBase();

            // Ensure the shared sysadmin 'admin' login exists so the admin-visibility test can sign in.
            this.admin.InitializeServer("admin");

            // Build a database the login can access: a granted table and an ungranted one.
            Ok(this.admin.ExecuteQuery($"CREATE DATABASE {this.dbName};"));
            Ok(this.admin.ExecuteQuery($"USE {this.dbName};"));
            this.dbPath = this.admin.CurrentDatabasePath!;
            Ok(this.admin.ExecuteQuery("CREATE TABLE Public1 (Id INT, Name NVARCHAR(50));"));
            Ok(this.admin.ExecuteQuery("INSERT INTO Public1 (Id, Name) VALUES (1, 'A'), (2, 'B');"));
            Ok(this.admin.ExecuteQuery("CREATE TABLE Secret1 (Id INT);"));
            Ok(this.admin.ExecuteQuery("INSERT INTO Secret1 (Id) VALUES (9);"));

            Ok(this.admin.ExecuteQuery($"CREATE LOGIN {this.loginName} WITH PASSWORD = 'P@ssw0rd!';"));
            Ok(this.admin.ExecuteQuery($"CREATE USER WebUser FOR LOGIN {this.loginName};"));
            Ok(this.admin.ExecuteQuery("GRANT SELECT ON Public1 TO WebUser;"));

            // A second database the login is NOT mapped into.
            Ok(this.admin.ExecuteQuery($"CREATE DATABASE {this.otherName};"));
            Ok(this.admin.ExecuteQuery($"USE {this.otherName};"));
            this.otherPath = this.admin.CurrentDatabasePath!;
            Ok(this.admin.ExecuteQuery("CREATE TABLE OtherT (Id INT);"));
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try { Directory.Delete(this.dbPath, true); } catch { }
            try { Directory.Delete(this.otherPath, true); } catch { }
        }

        private static void Ok(QueryResult r) => Assert.IsTrue(r.Success, r.Message);

        private ParqBase UserSession()
        {
            var s = new ParqBase();
            s.ResumeLogin(this.loginName);
            return s;
        }

        [TestMethod]
        public void LoggedOut_SeesNoDatabases()
        {
            var session = new ParqBase();
            Assert.AreEqual(0, session.ListAccessibleDatabases().Count);
        }

        [TestMethod]
        public void NonAdminLogin_SeesOnlyMappedDatabases()
        {
            var accessible = this.UserSession().ListAccessibleDatabases();
            CollectionAssert.Contains(accessible.ToList(), this.dbName);
            CollectionAssert.DoesNotContain(accessible.ToList(), this.otherName);
        }

        [TestMethod]
        public void AdminLogin_SeesEverything()
        {
            var session = new ParqBase();
            session.ResumeLogin("admin");
            Assert.IsTrue(session.CurrentLoginIsSysadmin);
            var accessible = session.ListAccessibleDatabases().ToList();
            CollectionAssert.Contains(accessible, this.dbName);
            CollectionAssert.Contains(accessible, this.otherName);
        }

        [TestMethod]
        public void NonAdminLogin_AutoImpersonation_EnforcesGrants()
        {
            var session = this.UserSession();

            var allowed = session.ExecuteScript($"USE {this.dbName};\nGO\nSELECT * FROM Public1;", continueOnError: false).Last();
            Assert.IsTrue(allowed.Success, allowed.Message);
            Assert.AreEqual(2, allowed.RowCount);

            var denied = session.ExecuteScript($"USE {this.dbName};\nGO\nSELECT * FROM Secret1;", continueOnError: false).Last();
            Assert.IsFalse(denied.Success);
            StringAssert.Contains(denied.Message, "Permission denied");
        }

        [TestMethod]
        public void NonAdminLogin_CannotUseDatabaseWithoutAccess()
        {
            var result = this.UserSession().ExecuteQuery($"USE {this.otherName};");
            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.Message, "does not have access");
        }

        [TestMethod]
        public void CanReadTable_RespectsGrantAndDeny()
        {
            var session = this.UserSession();
            Assert.IsTrue(session.CanReadTable(this.dbName, "Public1"));
            Assert.IsFalse(session.CanReadTable(this.dbName, "Secret1"));
        }

        [TestMethod]
        public void SysTables_FilteredToVisibleTables()
        {
            var result = this.UserSession()
                .ExecuteScript($"USE {this.dbName};\nGO\nSELECT name FROM sys.tables;", continueOnError: false)
                .Last();
            Ok(result);
            var names = result.Rows.Select(r => (string)r["name"]!).ToList();
            CollectionAssert.Contains(names, "Public1");
            CollectionAssert.DoesNotContain(names, "Secret1");
        }

        [TestMethod]
        public void GetMyPermissions_ListsEffectiveGrants()
        {
            var perms = this.UserSession().GetMyPermissions(this.dbName);
            Assert.IsTrue(perms.Any(p =>
                p.Permission == "SELECT" &&
                p.Securable == "dbo.Public1" &&
                p.State == "GRANT"));
        }

        [TestMethod]
        public void Logout_ClearsAccess()
        {
            var session = this.UserSession();
            Assert.IsTrue(session.ListAccessibleDatabases().Count > 0);
            session.Logout();
            Assert.AreEqual(0, session.ListAccessibleDatabases().Count);
        }

        // ---- CREATE DATABASE authorization + creator ownership -----------------

        [TestMethod]
        public void CreateDatabase_WithoutPermission_IsDenied()
        {
            // The seeded login is a plain user with no server-role membership.
            var result = this.UserSession().ExecuteQuery($"CREATE DATABASE Denied_{this.suffix};");
            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.Message, "does not have permission to create databases");
        }

        [TestMethod]
        public void DbCreatorLogin_CanCreateDatabase_AndOwnsIt()
        {
            // Admin grants the login the dbcreator server role.
            Ok(this.admin.ExecuteQuery($"ALTER SERVER ROLE dbcreator ADD MEMBER {this.loginName};"));

            var newDb = "Owned_" + this.suffix;
            var session = this.UserSession();
            try
            {
                // The login can now create a database...
                var created = session.ExecuteQuery($"CREATE DATABASE {newDb};");
                Ok(created);

                // ...and immediately has full control of it (creator becomes db_owner).
                var work = session.ExecuteScript(
                    $"USE {newDb};\nGO\nCREATE TABLE Mine (Id INT);\nINSERT INTO Mine (Id) VALUES (1);\nSELECT * FROM Mine;",
                    continueOnError: false);
                foreach (var r in work)
                {
                    Assert.IsTrue(r.Success, r.Message);
                }

                Assert.AreEqual(1, work.Last().RowCount);

                // A fresh session for the same login still sees and can access the database.
                var fresh = this.UserSession();
                CollectionAssert.Contains(fresh.ListAccessibleDatabases().ToList(), newDb);
                Assert.IsTrue(fresh.ExecuteQuery($"USE {newDb};").Success);
            }
            finally
            {
                try { Directory.Delete(Path.Combine(Path.GetDirectoryName(this.dbPath)!, newDb), true); } catch { }
            }
        }

        [TestMethod]
        public void AlterServerRole_RequiresServerSecurityRights()
        {
            // A plain login cannot manage server-role membership.
            var result = this.UserSession().ExecuteQuery($"ALTER SERVER ROLE dbcreator ADD MEMBER {this.loginName};");
            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.Message, "Permission denied");
        }

        // ---- Default-deny visibility: read / write / view are all explicit ------

        [TestMethod]
        public void MappedUser_WithNoGrants_SeesNothing()
        {
            // A login mapped to a database user but granted nothing must have no access at all:
            // the database is hidden and no tables are visible (default deny).
            var noGrant = "nogrant_" + this.suffix;
            Ok(this.admin.ExecuteQuery($"CREATE LOGIN {noGrant} WITH PASSWORD = 'P@ssw0rd!';"));
            Ok(this.admin.ExecuteScript($"USE {this.dbName};\nGO\nCREATE USER NoGrantU FOR LOGIN {noGrant};", continueOnError: false).Last());

            var session = new ParqBase();
            session.ResumeLogin(noGrant);

            CollectionAssert.DoesNotContain(session.ListAccessibleDatabases().ToList(), this.dbName);

            var tables = session.ExecuteScript($"USE {this.dbName};\nGO\nSELECT name FROM sys.tables;", continueOnError: false).Last();
            Assert.IsTrue(tables.Success, tables.Message);
            Assert.AreEqual(0, tables.RowCount);

            Assert.IsFalse(session.CanViewObject(this.dbName, "Public1"));
            Assert.IsFalse(session.CanReadTable(this.dbName, "Public1"));
        }

        [TestMethod]
        public void ViewDefinition_RevealsObject_ButDoesNotAllowRead()
        {
            // Granting only VIEW DEFINITION makes the object visible (explorer + metadata) but must not
            // permit reading its data — view and read are separate permissions.
            var viewer = "viewer_" + this.suffix;
            Ok(this.admin.ExecuteQuery($"CREATE LOGIN {viewer} WITH PASSWORD = 'P@ssw0rd!';"));
            Ok(this.admin.ExecuteScript(
                $"USE {this.dbName};\nGO\nCREATE USER ViewerU FOR LOGIN {viewer};\nGRANT VIEW DEFINITION ON Secret1 TO ViewerU;",
                continueOnError: false).Last());

            var session = new ParqBase();
            session.ResumeLogin(viewer);

            // The database and the granted object are now visible...
            CollectionAssert.Contains(session.ListAccessibleDatabases().ToList(), this.dbName);
            var tables = session.ExecuteScript($"USE {this.dbName};\nGO\nSELECT name FROM sys.tables;", continueOnError: false).Last();
            var names = tables.Rows.Select(r => (string)r["name"]!).ToList();
            CollectionAssert.Contains(names, "Secret1");
            Assert.IsTrue(session.CanViewObject(this.dbName, "Secret1"));

            // ...but the data is still off-limits.
            Assert.IsFalse(session.CanReadTable(this.dbName, "Secret1"));
            var read = session.ExecuteScript($"USE {this.dbName};\nGO\nSELECT * FROM Secret1;", continueOnError: false).Last();
            Assert.IsFalse(read.Success);
            StringAssert.Contains(read.Message, "Permission denied");
        }
    }
}
