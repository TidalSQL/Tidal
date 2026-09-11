namespace ParqBaseLib.Security
{
    /// <summary>
    /// Distinguishes the kind of database principal stored in the catalog.
    /// </summary>
    internal enum PrincipalKind
    {
        User,
        Role,
    }

    /// <summary>
    /// State of a permission entry. DENY is stronger than GRANT and overrides it.
    /// </summary>
    internal enum PermissionState
    {
        Grant,
        Deny,
    }

    /// <summary>
    /// Class of securable a permission applies to.
    /// </summary>
    internal enum SecurableClass
    {
        Database,
        Schema,
        Object,
    }

    /// <summary>Server-level identity (a login), authenticated against the system.</summary>
    internal sealed class LoginRecord
    {
        public string Name { get; set; } = string.Empty;

        public string PasswordHash { get; set; } = string.Empty;

        public string Salt { get; set; } = string.Empty;

        public bool Disabled { get; set; }
    }

    /// <summary>Database-level principal: a user (mapped to a login) or a role.</summary>
    internal sealed class PrincipalRecord
    {
        public string Name { get; set; } = string.Empty;

        public PrincipalKind Kind { get; set; }

        /// <summary>For users: the login they map to (null for a user WITHOUT LOGIN).</summary>
        public string? LoginName { get; set; }

        /// <summary>Owning principal (AUTHORIZATION); defaults to dbo.</summary>
        public string? Owner { get; set; }

        /// <summary>True for the built-in fixed roles seeded on database creation.</summary>
        public bool IsFixed { get; set; }
    }

    /// <summary>Role membership edge: <see cref="Member"/> is a member of <see cref="Role"/>.</summary>
    internal sealed class RoleMemberRecord
    {
        public string Role { get; set; } = string.Empty;

        public string Member { get; set; } = string.Empty;
    }

    /// <summary>A schema securable that groups objects and can own permissions.</summary>
    internal sealed class SchemaRecord
    {
        public string Name { get; set; } = string.Empty;

        public string Owner { get; set; } = "dbo";
    }

    /// <summary>A GRANT or DENY of a permission to a principal on a securable.</summary>
    internal sealed class PermissionRecord
    {
        public string Grantee { get; set; } = string.Empty;

        /// <summary>Permission token, upper-cased, e.g. SELECT, INSERT, CONTROL, ALTER, EXECUTE.</summary>
        public string Permission { get; set; } = string.Empty;

        public SecurableClass Class { get; set; }

        /// <summary>
        /// Normalized securable name: object => "schema.object", schema => "schema",
        /// database => the database name.
        /// </summary>
        public string Securable { get; set; } = string.Empty;

        public PermissionState State { get; set; }
    }
}
