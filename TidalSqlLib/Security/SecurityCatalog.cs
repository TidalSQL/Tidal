namespace TidalSqlLib.Security
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Reads and writes the security catalog. Server-scoped data (logins, server-role
    /// membership) lives under a process-wide "system" folder; database-scoped data
    /// (users, roles, schemas, permissions) lives under each database's "security" folder.
    /// Metadata is stored as JSON, keeping the small, relational security catalog separate
    /// from the Parquet table storage.
    /// </summary>
    internal sealed class SecurityCatalog
    {
        public const string DboSchema = "dbo";
        public const string PublicRole = "public";

        /// <summary>Name of the default server administrator login seeded on first run.</summary>
        public const string DefaultAdminLogin = "admin";

        public static readonly string[] FixedDatabaseRoles =
        {
            "db_owner",
            "db_securityadmin",
            "db_ddladmin",
            "db_datareader",
            "db_datawriter",
            "db_executor",
            PublicRole,
        };

        public static readonly string[] FixedServerRoles =
        {
            "sysadmin",
            "securityadmin",
            "dbcreator",
        };

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private readonly string systemRoot;
        private readonly string? databasePath;

        public SecurityCatalog(string systemRoot, string? databasePath)
        {
            this.systemRoot = systemRoot;
            this.databasePath = databasePath;
        }

        private string LoginsFile => Path.Combine(this.systemRoot, "logins.json");

        private string ServerRoleMembersFile => Path.Combine(this.systemRoot, "serverRoleMembers.json");

        private string SecurityDir => Path.Combine(this.RequireDb(), "security");

        private string PrincipalsFile => Path.Combine(this.SecurityDir, "principals.json");

        private string RoleMembersFile => Path.Combine(this.SecurityDir, "roleMembers.json");

        private string SchemasFile => Path.Combine(this.SecurityDir, "schemas.json");

        private string PermissionsFile => Path.Combine(this.SecurityDir, "permissions.json");

        // ---- Server scope ------------------------------------------------------

        public List<LoginRecord> GetLogins() => Load<LoginRecord>(this.LoginsFile);

        public bool LoginExists(string name) =>
            this.GetLogins().Any(l => Eq(l.Name, name));

        public LoginRecord? FindLogin(string name) =>
            this.GetLogins().FirstOrDefault(l => Eq(l.Name, name));

        public void AddLogin(LoginRecord login)
        {
            var logins = this.GetLogins();
            if (logins.Any(l => Eq(l.Name, login.Name)))
            {
                throw new InvalidOperationException($"Login '{login.Name}' already exists.");
            }

            logins.Add(login);
            Save(this.LoginsFile, logins);
        }

        /// <summary>
        /// Removes a login and any server-role memberships it held. Returns true when a login was
        /// removed, false when no login of that name existed.
        /// </summary>
        public bool RemoveLogin(string name)
        {
            var logins = this.GetLogins();
            if (logins.RemoveAll(l => Eq(l.Name, name)) == 0)
            {
                return false;
            }

            Save(this.LoginsFile, logins);

            var members = this.GetServerRoleMembers();
            if (members.RemoveAll(m => Eq(m.Member, name)) > 0)
            {
                Save(this.ServerRoleMembersFile, members);
            }

            return true;
        }

        /// <summary>
        /// Creates the default sysadmin login (and its sysadmin membership) if it does not already
        /// exist. Returns true when it was created, so callers can surface the initial credentials.
        /// </summary>
        public bool EnsureDefaultAdmin(string name, string password)
        {
            Directory.CreateDirectory(this.systemRoot);
            if (this.LoginExists(name))
            {
                return false;
            }

            var (hash, salt) = PasswordHasher.Create(password);
            this.AddLogin(new LoginRecord { Name = name, PasswordHash = hash, Salt = salt });
            this.AddServerRoleMember("sysadmin", name);
            return true;
        }

        /// <summary>Verifies a login's password. Returns false for unknown or disabled logins.</summary>
        public bool VerifyLogin(string name, string password)
        {
            var login = this.FindLogin(name);
            if (login == null || login.Disabled)
            {
                return false;
            }

            return PasswordHasher.Verify(password, login.PasswordHash, login.Salt);
        }

        public List<RoleMemberRecord> GetServerRoleMembers() => Load<RoleMemberRecord>(this.ServerRoleMembersFile);

        public void AddServerRoleMember(string role, string login)
        {
            var members = this.GetServerRoleMembers();
            if (!members.Any(m => Eq(m.Role, role) && Eq(m.Member, login)))
            {
                members.Add(new RoleMemberRecord { Role = role, Member = login });
                Save(this.ServerRoleMembersFile, members);
            }
        }

        public bool IsServerRoleMember(string role, string login) =>
            this.GetServerRoleMembers().Any(m => Eq(m.Role, role) && Eq(m.Member, login));

        public void RemoveServerRoleMember(string role, string login)
        {
            var members = this.GetServerRoleMembers();
            if (members.RemoveAll(m => Eq(m.Role, role) && Eq(m.Member, login)) > 0)
            {
                Save(this.ServerRoleMembersFile, members);
            }
        }

        // ---- Database scope ----------------------------------------------------

        public List<PrincipalRecord> GetPrincipals() => Load<PrincipalRecord>(this.PrincipalsFile);

        public PrincipalRecord? FindPrincipal(string name) =>
            this.GetPrincipals().FirstOrDefault(p => Eq(p.Name, name));

        public bool PrincipalExists(string name) => this.FindPrincipal(name) != null;

        public void AddPrincipal(PrincipalRecord principal)
        {
            var principals = this.GetPrincipals();
            if (principals.Any(p => Eq(p.Name, principal.Name)))
            {
                throw new InvalidOperationException($"Principal '{principal.Name}' already exists in this database.");
            }

            principals.Add(principal);
            Save(this.PrincipalsFile, principals);
        }

        public List<RoleMemberRecord> GetRoleMembers() => Load<RoleMemberRecord>(this.RoleMembersFile);

        public void AddRoleMember(string role, string member)
        {
            var members = this.GetRoleMembers();
            if (!members.Any(m => Eq(m.Role, role) && Eq(m.Member, member)))
            {
                members.Add(new RoleMemberRecord { Role = role, Member = member });
                Save(this.RoleMembersFile, members);
            }
        }

        public void RemoveRoleMember(string role, string member)
        {
            var members = this.GetRoleMembers();
            var removed = members.RemoveAll(m => Eq(m.Role, role) && Eq(m.Member, member));
            if (removed > 0)
            {
                Save(this.RoleMembersFile, members);
            }
        }

        public List<SchemaRecord> GetSchemas() => Load<SchemaRecord>(this.SchemasFile);

        public SchemaRecord? FindSchema(string name) =>
            this.GetSchemas().FirstOrDefault(s => Eq(s.Name, name));

        public bool SchemaExists(string name) =>
            Eq(name, DboSchema) || this.FindSchema(name) != null;

        public void AddSchema(string name, string owner)
        {
            var schemas = this.GetSchemas();
            if (schemas.Any(s => Eq(s.Name, name)) || Eq(name, DboSchema))
            {
                throw new InvalidOperationException($"Schema '{name}' already exists.");
            }

            schemas.Add(new SchemaRecord { Name = name, Owner = owner });
            Save(this.SchemasFile, schemas);
        }

        public List<PermissionRecord> GetPermissions() => Load<PermissionRecord>(this.PermissionsFile);

        /// <summary>
        /// Adds or replaces a GRANT/DENY. A GRANT and DENY for the same grantee/permission/
        /// securable are mutually exclusive (the newer state wins), matching T-SQL.
        /// </summary>
        public void SetPermission(PermissionRecord record)
        {
            var permissions = this.GetPermissions();
            permissions.RemoveAll(p => SameTarget(p, record));
            permissions.Add(record);
            Save(this.PermissionsFile, permissions);
        }

        /// <summary>Removes any GRANT or DENY matching the grantee/permission/securable.</summary>
        public void RevokePermission(string grantee, string permission, SecurableClass @class, string securable)
        {
            var permissions = this.GetPermissions();
            var removed = permissions.RemoveAll(p =>
                Eq(p.Grantee, grantee) &&
                Eq(p.Permission, permission) &&
                p.Class == @class &&
                Eq(p.Securable, securable));
            if (removed > 0)
            {
                Save(this.PermissionsFile, permissions);
            }
        }

        /// <summary>
        /// Seeds the fixed database roles, the dbo user/schema, and the public role for a newly
        /// created database, so role membership and permission grants have targets to reference.
        /// </summary>
        public void SeedDatabase()
        {
            Directory.CreateDirectory(this.SecurityDir);

            var principals = this.GetPrincipals();
            void EnsureRole(string name)
            {
                if (!principals.Any(p => Eq(p.Name, name)))
                {
                    principals.Add(new PrincipalRecord { Name = name, Kind = PrincipalKind.Role, Owner = DboSchema, IsFixed = true });
                }
            }

            void EnsureUser(string name, string? login)
            {
                if (!principals.Any(p => Eq(p.Name, name)))
                {
                    principals.Add(new PrincipalRecord { Name = name, Kind = PrincipalKind.User, LoginName = login, Owner = DboSchema, IsFixed = true });
                }
            }

            EnsureUser("dbo", null);
            EnsureUser("guest", null);
            foreach (var role in FixedDatabaseRoles)
            {
                EnsureRole(role);
            }

            Save(this.PrincipalsFile, principals);

            var schemas = this.GetSchemas();
            if (!schemas.Any(s => Eq(s.Name, DboSchema)))
            {
                schemas.Add(new SchemaRecord { Name = DboSchema, Owner = "dbo" });
                Save(this.SchemasFile, schemas);
            }
        }

        public static bool SameTarget(PermissionRecord a, PermissionRecord b) =>
            Eq(a.Grantee, b.Grantee) &&
            Eq(a.Permission, b.Permission) &&
            a.Class == b.Class &&
            Eq(a.Securable, b.Securable);

        private static bool Eq(string? a, string? b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private string RequireDb() =>
            this.databasePath ?? throw new InvalidOperationException(
                "No database selected. Use 'USE <database>;' to select a database first.");

        private static List<T> Load<T>(string path)
        {
            if (!File.Exists(path))
            {
                return new List<T>();
            }

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<T>();
            }

            return JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? new List<T>();
        }

        private static void Save<T>(string path, List<T> items)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(items, JsonOptions));
        }
    }
}
