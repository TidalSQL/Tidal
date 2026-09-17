namespace ParqBaseLib
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using ParqBaseLib.Security;

    public partial class ParqBase
    {
        private readonly SessionContext session = new();
        private readonly object gate = new();

        public ParqBase()
        {
        }

        // Retained for dependency-injection resolution; ParqBase keeps its own session state.
        public ParqBase(IServiceProvider serviceProvider)
        {
        }

        /// <summary>
        /// Root folder for all persisted data (databases and the server security catalog).
        /// Resolves to the ParqBaseLib project directory so data survives a clean build of the
        /// output (bin/obj) folders, and can be overridden with the PARQBASE_DATA_ROOT env var.
        /// </summary>
        internal static string DataRoot { get; } = ResolveDataRoot();

        /// <summary>Absolute path of the folder that holds all databases.</summary>
        internal static string DatabasesRoot => Path.Combine(DataRoot, "databases");

        /// <summary>Absolute path of the server-scoped security/system folder.</summary>
        internal static string SystemRoot => Path.Combine(DataRoot, "system");

        /// <summary>
        /// Absolute path of the database currently selected in this session, or null if none.
        /// </summary>
        public string? CurrentDatabasePath => this.session.CurrentDatabasePath;

        /// <summary>
        /// The database user this session is currently executing as (via EXECUTE AS USER),
        /// or null when unauthenticated (treated as a full-control administrator).
        /// </summary>
        public string? CurrentUser => this.session.CurrentUser;

        /// <summary>The server login this session authenticated as, or null if not logged in.</summary>
        public string? CurrentLogin => this.session.CurrentLogin;

        /// <summary>True once a login has successfully authenticated on this session.</summary>
        public bool IsAuthenticated => this.session.CurrentLogin != null;

        /// <summary>
        /// Ensures the server is initialized with a default administrator login, creating it on
        /// first run. Returns details so a host can surface the initial credentials to the user.
        /// </summary>
        /// <param name="defaultPassword">Password to assign the admin login when first created.</param>
        public ServerInitResult InitializeServer(string defaultPassword)
        {
            if (string.IsNullOrEmpty(defaultPassword))
            {
                throw new ArgumentException("A default admin password is required.", nameof(defaultPassword));
            }

            lock (this.gate)
            {
                var catalog = new SecurityCatalog(SystemRoot, null);
                var created = catalog.EnsureDefaultAdmin(SecurityCatalog.DefaultAdminLogin, defaultPassword);
                return new ServerInitResult(
                    SecurityCatalog.DefaultAdminLogin,
                    created,
                    created ? defaultPassword : null);
            }
        }

        /// <summary>
        /// Authenticates a login against the server security catalog. On success the session records
        /// the login (and whether it is a sysadmin). Returns false for bad, unknown or disabled logins.
        /// </summary>
        public bool Login(string loginName, string password)
        {
            if (string.IsNullOrWhiteSpace(loginName) || password == null)
            {
                return false;
            }

            lock (this.gate)
            {
                var catalog = new SecurityCatalog(SystemRoot, null);
                if (!catalog.VerifyLogin(loginName, password))
                {
                    return false;
                }

                this.session.CurrentLogin = loginName;
                this.session.IsSysadminLogin = catalog.IsServerRoleMember("sysadmin", loginName);
                return true;
            }
        }

        /// <summary>True when the authenticated login is a member of the sysadmin server role.</summary>
        public bool CurrentLoginIsSysadmin => this.session.IsSysadminLogin;

        /// <summary>
        /// Establishes the session for a login that has already been authenticated elsewhere (for
        /// example a web host that verified credentials once and now resumes the session from a token).
        /// Records the login and whether it is a sysadmin, without re-checking the password.
        /// </summary>
        public void ResumeLogin(string loginName)
        {
            if (string.IsNullOrWhiteSpace(loginName))
            {
                throw new ArgumentException("A login name is required.", nameof(loginName));
            }

            lock (this.gate)
            {
                var catalog = new SecurityCatalog(SystemRoot, null);
                this.session.CurrentLogin = loginName;
                this.session.IsSysadminLogin = catalog.IsServerRoleMember("sysadmin", loginName);
            }
        }

        /// <summary>Clears all authentication and impersonation state for this session (log out).</summary>
        public void Logout()
        {
            lock (this.gate)
            {
                this.session.CurrentLogin = null;
                this.session.IsSysadminLogin = false;
                this.session.AutoUser = null;
                this.session.CurrentDatabasePath = null;
            }
        }

        /// <summary>
        /// Lists the databases the currently authenticated login may access: every database for a
        /// sysadmin, and for a constrained login those in which it maps to a database user. Returns an
        /// empty list when no login is authenticated, so a logged-out session sees no databases.
        /// </summary>
        public IReadOnlyList<string> ListAccessibleDatabases()
        {
            lock (this.gate)
            {
                var login = this.session.CurrentLogin;
                if (login == null)
                {
                    return Array.Empty<string>();
                }

                var all = Directory.Exists(DatabasesRoot)
                    ? Directory.GetDirectories(DatabasesRoot)
                        .Select(Path.GetFileName)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Select(n => n!)
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : new List<string>();

                if (this.session.IsSysadminLogin)
                {
                    return all;
                }

                var accessible = new List<string>();
                foreach (var db in all)
                {
                    var dbPath = Path.Combine(DatabasesRoot, db);
                    var catalog = new SecurityCatalog(SystemRoot, dbPath);
                    var user = catalog.GetPrincipals().FirstOrDefault(p =>
                        p.Kind == PrincipalKind.User &&
                        string.Equals(p.LoginName, login, StringComparison.OrdinalIgnoreCase));
                    if (user != null && new Authorizer(catalog).HasAnyAccess(user.Name))
                    {
                        accessible.Add(db);
                    }
                }

                return accessible;
            }
        }

        /// <summary>
        /// Returns whether the current login may read (SELECT) a table, honouring GRANT/DENY, roles,
        /// ownership and fixed roles. A sysadmin (or an unauthenticated programmatic session) may read
        /// anything. Used by the web host to authorize direct table reads that bypass the SQL engine.
        /// </summary>
        public bool CanReadTable(string database, string table, string schema = "dbo")
        {
            lock (this.gate)
            {
                if (this.session.CurrentLogin == null)
                {
                    return false;
                }

                if (this.session.IsSysadminLogin)
                {
                    return true;
                }

                schema = string.IsNullOrWhiteSpace(schema) ? SecurityCatalog.DboSchema : schema;
                var dbPath = Path.Combine(DatabasesRoot, database);
                if (!Directory.Exists(dbPath))
                {
                    return false;
                }

                var catalog = new SecurityCatalog(SystemRoot, dbPath);
                var user = catalog.GetPrincipals().FirstOrDefault(p =>
                    p.Kind == PrincipalKind.User &&
                    string.Equals(p.LoginName, this.session.CurrentLogin, StringComparison.OrdinalIgnoreCase));
                if (user == null)
                {
                    return false;
                }

                return new Authorizer(catalog).CanAccessObject(user.Name, SecurityAction.Select, schema, table);
            }
        }

        /// <summary>
        /// Returns whether the current login may see the metadata/definition of an object (a table,
        /// view, or stored procedure) — i.e. holds VIEW DEFINITION or any object permission on it.
        /// This is the "view" tier, distinct from reading data (<see cref="CanReadTable"/>). A sysadmin
        /// (or an unauthenticated programmatic session) may view anything. Used by the web host to gate
        /// table-overview and procedure-definition endpoints.
        /// </summary>
        public bool CanViewObject(string database, string name, string schema = "dbo")
        {
            lock (this.gate)
            {
                if (this.session.CurrentLogin == null)
                {
                    return false;
                }

                if (this.session.IsSysadminLogin)
                {
                    return true;
                }

                schema = string.IsNullOrWhiteSpace(schema) ? SecurityCatalog.DboSchema : schema;
                var dbPath = Path.Combine(DatabasesRoot, database);
                if (!Directory.Exists(dbPath))
                {
                    return false;
                }

                var catalog = new SecurityCatalog(SystemRoot, dbPath);
                var user = catalog.GetPrincipals().FirstOrDefault(p =>
                    p.Kind == PrincipalKind.User &&
                    string.Equals(p.LoginName, this.session.CurrentLogin, StringComparison.OrdinalIgnoreCase));
                if (user == null)
                {
                    return false;
                }

                return new Authorizer(catalog).CanViewObject(user.Name, schema, name);
            }
        }

        /// <summary>
        /// Lists the effective permissions of the current login within a database — the GRANT/DENY
        /// rows that apply to the login's user (directly or through role membership), plus the implicit
        /// grants carried by the fixed database roles it belongs to. A sysadmin has full control.
        /// Returns an empty list when the login has no access to the database.
        /// </summary>
        public IReadOnlyList<UserPermission> GetMyPermissions(string database)
        {
            lock (this.gate)
            {
                var login = this.session.CurrentLogin;
                if (login == null)
                {
                    return Array.Empty<UserPermission>();
                }

                if (this.session.IsSysadminLogin)
                {
                    return new[]
                    {
                        new UserPermission(login, "CONTROL", "SERVER", "(all databases)", "GRANT (sysadmin)"),
                    };
                }

                var dbPath = Path.Combine(DatabasesRoot, database);
                if (!Directory.Exists(dbPath))
                {
                    throw new Exception($"Database [{database}] does not exist.");
                }

                var catalog = new SecurityCatalog(SystemRoot, dbPath);
                var user = catalog.GetPrincipals().FirstOrDefault(p =>
                    p.Kind == PrincipalKind.User &&
                    string.Equals(p.LoginName, login, StringComparison.OrdinalIgnoreCase));
                if (user == null)
                {
                    return Array.Empty<UserPermission>();
                }

                var authorizer = new Authorizer(catalog);
                var principals = authorizer.EffectivePrincipals(user.Name);

                var results = new List<UserPermission>();

                // Explicit GRANT/DENY rows that apply to this user (via itself, its roles, or public).
                foreach (var p in catalog.GetPermissions().Where(p => principals.Contains(p.Grantee)))
                {
                    results.Add(new UserPermission(
                        p.Grantee,
                        p.Permission,
                        p.Class.ToString().ToUpperInvariant(),
                        p.Securable,
                        p.State == PermissionState.Deny ? "DENY" : "GRANT"));
                }

                // Implicit grants from fixed database roles the user belongs to.
                void FixedRole(string role, string permission)
                {
                    if (principals.Contains(role))
                    {
                        results.Add(new UserPermission(role, permission, "DATABASE", database, "GRANT (fixed role)"));
                    }
                }

                FixedRole("db_owner", "CONTROL");
                FixedRole("db_datareader", "SELECT");
                FixedRole("db_datawriter", "INSERT, UPDATE, DELETE");
                FixedRole("db_ddladmin", "ALTER");
                FixedRole("db_executor", "EXECUTE");

                return results;
            }
        }

        public void ExecuteStatement(string statement)
        {
            this.ExecuteQuery(statement);
        }

        public QueryResult ExecuteQuery(string statement, CancellationToken cancellationToken = default)
        {
            // A ParqBase instance represents a single logical session; serialize calls so a
            // shared instance stays consistent. Concurrency comes from using separate instances.
            lock (this.gate)
            {
                var visitor = new ParqBaseStatementVisitor(this.session, cancellationToken);
                return visitor.Run(statement);
            }
        }

        /// <summary>
        /// Executes a multi-statement T-SQL script (for example one that creates a database, creates
        /// tables and inserts data). Statements run in order against this session; GO batch separators
        /// are supported. Returns one <see cref="QueryResult"/> per executed statement. Execution stops
        /// at the first failure unless <paramref name="continueOnError"/> is true.
        /// </summary>
        public IReadOnlyList<QueryResult> ExecuteScript(string script, bool continueOnError = false, CancellationToken cancellationToken = default)
        {
            lock (this.gate)
            {
                var visitor = new ParqBaseStatementVisitor(this.session, cancellationToken);
                return visitor.RunScript(script, continueOnError);
            }
        }

        /// <summary>
        /// Reads a T-SQL script from a file and executes it via <see cref="ExecuteScript(string, bool)"/>.
        /// </summary>
        public IReadOnlyList<QueryResult> ExecuteScriptFile(string path, bool continueOnError = false)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A script file path is required.", nameof(path));
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Script file not found: {path}", path);
            }

            var script = File.ReadAllText(path);
            return this.ExecuteScript(script, continueOnError);
        }

        /// <summary>
        /// Returns catalog metadata for a single table (row count, size on disk, last modified and
        /// per-column type information) for use by tooling such as the web UI's table overview.
        /// Throws when the database or table does not exist.
        /// </summary>
        public TableInfo GetTableInfo(string database, string table, string schema = "dbo")
        {
            if (string.IsNullOrWhiteSpace(database))
            {
                throw new ArgumentException("A database name is required.", nameof(database));
            }

            if (string.IsNullOrWhiteSpace(table))
            {
                throw new ArgumentException("A table name is required.", nameof(table));
            }

            schema = string.IsNullOrWhiteSpace(schema) ? SecurityCatalog.DboSchema : schema;

            var databasePath = Path.Combine(DatabasesRoot, database);
            if (!Directory.Exists(databasePath))
            {
                throw new Exception($"Database [{database}] does not exist.");
            }

            var source = ParqBaseStatementVisitor.ResolveTableSource(databasePath, schema, table);
            if (!source.Exists)
            {
                throw new Exception($"Table [{schema}].[{table}] does not exist in database [{database}].");
            }

            // Read under a shared lock so a concurrent writer's atomic file replace cannot cause a
            // torn read or an IOException while the file is being swapped.
            TableMeta? meta;
            long fileLength;
            DateTime lastWriteTime;
            long rowCount;
            IReadOnlyList<ColumnMeta> columnSource;
            using (TableLock.Read(source.LockKey))
            {
                rowCount = ParqBaseStatementVisitor.CountRows(source.Files);

                // Aggregate on-disk size and the newest write time across every part.
                fileLength = 0;
                lastWriteTime = DateTime.MinValue;
                foreach (var part in source.Files)
                {
                    var info = new FileInfo(part);
                    fileLength += info.Length;
                    if (info.LastWriteTime > lastWriteTime)
                    {
                        lastWriteTime = info.LastWriteTime;
                    }
                }

                if (source.IsMultiPart)
                {
                    // Multi-part (directory) tables have no metadata sidecar; infer columns from the
                    // first part's Parquet schema.
                    meta = null;
                    columnSource = ParqBaseStatementVisitor.ReadSchemaColumns(source.PrimaryFile);
                }
                else
                {
                    // Prefer the rich metadata sidecar (created by CREATE TABLE). Tables imported as raw
                    // Parquet have no sidecar, so create one now from the column schema stored in the file.
                    meta = ParqBaseStatementVisitor.LoadTableMeta(source.PrimaryFile);
                    columnSource = meta?.Columns
                        ?? ParqBaseStatementVisitor.EnsureTableMeta(source.PrimaryFile).Columns;
                }
            }

            var columns = new List<TableColumnInfo>();
            for (var i = 0; i < columnSource.Count; i++)
            {
                var column = columnSource[i];
                var (dataType, maxLength, precision, scale) =
                    ParqBaseStatementVisitor.DescribeColumnType(column);
                columns.Add(new TableColumnInfo(
                    i,
                    column.Name,
                    dataType,
                    column.Nullable,
                    maxLength,
                    precision,
                    scale,
                    column.IsIdentity,
                    column.DefaultSql,
                    column.ComputedSql));
            }

            return new TableInfo(
                database,
                schema,
                table,
                rowCount,
                columns.Count,
                fileLength,
                lastWriteTime,
                columns);
        }

        /// <summary>
        /// Returns the T-SQL definition (a CREATE OR ALTER PROCEDURE script) for a stored procedure,
        /// reconstructed from its persisted metadata. Throws when the database or procedure is missing.
        /// </summary>
        public string GetProcedureDefinition(string database, string procedure, string schema = "dbo")
        {
            if (string.IsNullOrWhiteSpace(database))
            {
                throw new ArgumentException("A database name is required.", nameof(database));
            }

            if (string.IsNullOrWhiteSpace(procedure))
            {
                throw new ArgumentException("A procedure name is required.", nameof(procedure));
            }

            schema = string.IsNullOrWhiteSpace(schema) ? SecurityCatalog.DboSchema : schema;

            var databasePath = Path.Combine(DatabasesRoot, database);
            if (!Directory.Exists(databasePath))
            {
                throw new Exception($"Database [{database}] does not exist.");
            }

            var fileName = ParqBaseStatementVisitor.ProcedureFileName(schema, procedure);
            var filePath = Path.Combine(databasePath, "procedures", fileName);
            if (!File.Exists(filePath))
            {
                throw new Exception($"Stored procedure [{schema}].[{procedure}] does not exist in database [{database}].");
            }

            return ParqBaseStatementVisitor.BuildProcedureDefinition(filePath);
        }

        /// <summary>
        /// Determines the persistent data root. Prefers the PARQBASE_DATA_ROOT environment variable,
        /// then walks up from the running assembly's location to find the ParqBaseLib project folder
        /// (the directory containing ParqBaseLib.csproj), and finally falls back to the base directory.
        /// Keeping data out of bin/obj means it is not deleted by a clean/rebuild.
        /// </summary>
        private static string ResolveDataRoot()
        {
            var configured = Environment.GetEnvironmentVariable("PARQBASE_DATA_ROOT");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                Directory.CreateDirectory(configured);
                return Path.GetFullPath(configured);
            }

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var projectDir = Path.Combine(dir.FullName, "ParqBaseLib");
                if (File.Exists(Path.Combine(projectDir, "ParqBaseLib.csproj")))
                {
                    return projectDir;
                }

                dir = dir.Parent;
            }

            return AppContext.BaseDirectory;
        }
    }

    /// <summary>
    /// Outcome of <see cref="ParqBase.InitializeServer(string)"/>. When <see cref="Created"/> is
    /// true the default admin login was just created and <see cref="Password"/> carries the initial
    /// password so a host can display it once; otherwise it is null.
    /// </summary>
    public sealed record ServerInitResult(string AdminLogin, bool Created, string? Password);

    /// <summary>
    /// A single effective permission of the current login within a database, surfaced to the web UI's
    /// "My Access" view. <see cref="State"/> is GRANT, DENY, or a fixed-role/sysadmin annotation.
    /// </summary>
    public sealed record UserPermission(string Grantee, string Permission, string Class, string Securable, string State);

    /// <summary>
    /// Catalog metadata describing a single table, returned by <see cref="ParqBase.GetTableInfo"/>.
    /// </summary>
    public sealed record TableInfo(
        string Database,
        string Schema,
        string Table,
        long RowCount,
        int ColumnCount,
        long SizeBytes,
        DateTime LastModified,
        IReadOnlyList<TableColumnInfo> Columns);

    /// <summary>Per-column catalog metadata for a table (approximates SQL Server's sys.columns).</summary>
    public sealed record TableColumnInfo(
        int ColumnId,
        string Name,
        string DataType,
        bool Nullable,
        int MaxLength,
        int Precision,
        int Scale,
        bool IsIdentity,
        string? DefaultDefinition,
        string? ComputedDefinition);
}
