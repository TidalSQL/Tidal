namespace TidalSqlLib.Security
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>Raised when the current principal lacks permission for an action.</summary>
    internal sealed class SecurityException : Exception
    {
        public SecurityException(string message)
            : base(message)
        {
        }
    }

    /// <summary>Actions that are subject to authorization checks.</summary>
    internal enum SecurityAction
    {
        Select,
        Insert,
        Update,
        Delete,
        Execute,
        CreateObject,
        AlterObject,
        ViewDefinition,
    }

    /// <summary>
    /// Computes effective permissions in a SQL Server-like model: role membership is expanded
    /// transitively, DENY overrides GRANT, permissions are inherited from database -> schema ->
    /// object, ownership implies control, and fixed roles carry implicit permissions.
    /// sysadmin (server) and db_owner (database) bypass all checks.
    /// </summary>
    internal sealed class Authorizer
    {
        private readonly SecurityCatalog catalog;

        public Authorizer(SecurityCatalog catalog)
        {
            this.catalog = catalog;
        }

        /// <summary>Set of principals a user acts as: itself, its roles (transitively), and public.</summary>
        public HashSet<string> EffectivePrincipals(string user)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { user, SecurityCatalog.PublicRole };
            var members = this.catalog.GetRoleMembers();
            var queue = new Queue<string>();
            queue.Enqueue(user);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var edge in members.Where(m => string.Equals(m.Member, current, StringComparison.OrdinalIgnoreCase)))
                {
                    if (result.Add(edge.Role))
                    {
                        queue.Enqueue(edge.Role);
                    }
                }
            }

            return result;
        }

        public bool IsSysadmin(string user) => this.IsServerRoleMember(user, "sysadmin");

        public bool IsSecurityAdmin(string user) => this.IsServerRoleMember(user, "securityadmin");

        /// <summary>True when the user may create logins and other server-scoped security objects.</summary>
        public bool CanManageServerSecurity(string user) =>
            this.IsSysadmin(user) || this.IsSecurityAdmin(user);

        /// <summary>True when the user may create/alter users, roles, schemas and grant permissions.</summary>
        public bool CanManageDatabaseSecurity(string user)
        {
            if (this.IsSysadmin(user))
            {
                return true;
            }

            var principals = this.EffectivePrincipals(user);
            return principals.Contains("db_owner") || principals.Contains("db_securityadmin");
        }

        /// <summary>Authorization decision for a data or DDL action against an object in a schema.</summary>
        public bool CanAccessObject(string user, SecurityAction action, string schema, string obj)
        {
            var principals = this.EffectivePrincipals(user);

            if (this.IsSysadmin(user) || principals.Contains("db_owner"))
            {
                return true;
            }

            // Ownership: the owner of a schema controls its objects.
            var schemaRecord = this.catalog.FindSchema(schema);
            if (schemaRecord != null && principals.Contains(schemaRecord.Owner))
            {
                return true;
            }

            var covering = CoveringPermissions(action);
            var denied = false;
            var granted = false;
            foreach (var permission in this.catalog.GetPermissions())
            {
                if (!principals.Contains(permission.Grantee) ||
                    !covering.Contains(permission.Permission) ||
                    !ScopeApplies(permission, schema, obj))
                {
                    continue;
                }

                if (permission.State == PermissionState.Deny)
                {
                    denied = true;
                }
                else
                {
                    granted = true;
                }
            }

            if (denied)
            {
                return false;
            }

            if (granted)
            {
                return true;
            }

            return this.HasFixedRoleGrant(principals, action);
        }

        /// <summary>
        /// Metadata visibility for an object: true when the user may see that the object exists. A
        /// non-owner has no visibility until explicitly granted — either VIEW DEFINITION, or any
        /// object permission (SELECT/INSERT/UPDATE/DELETE/EXECUTE/ALTER) that implies awareness of it.
        /// sysadmin, db_owner, and schema owners see everything they control.
        /// </summary>
        public bool CanViewObject(string user, string schema, string obj)
        {
            var actions = new[]
            {
                SecurityAction.ViewDefinition,
                SecurityAction.Select,
                SecurityAction.Insert,
                SecurityAction.Update,
                SecurityAction.Delete,
                SecurityAction.Execute,
                SecurityAction.AlterObject,
            };

            foreach (var action in actions)
            {
                if (this.CanAccessObject(user, action, schema, obj))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when the user has any access at all within the database: they are a sysadmin, the
        /// database owner, a member of a fixed database role, own a schema, or hold at least one
        /// granted (non-denied) permission. Used to hide databases from mapped users who have not yet
        /// been granted anything, so access is default-deny until permissions are assigned.
        /// </summary>
        public bool HasAnyAccess(string user)
        {
            if (this.IsSysadmin(user))
            {
                return true;
            }

            var principals = this.EffectivePrincipals(user);
            if (principals.Contains("db_owner") ||
                principals.Contains("db_datareader") ||
                principals.Contains("db_datawriter") ||
                principals.Contains("db_executor") ||
                principals.Contains("db_ddladmin") ||
                principals.Contains("db_securityadmin"))
            {
                return true;
            }

            if (this.catalog.GetSchemas().Any(s => principals.Contains(s.Owner)))
            {
                return true;
            }

            return this.catalog.GetPermissions().Any(p =>
                p.State == PermissionState.Grant && principals.Contains(p.Grantee));
        }

        private bool HasFixedRoleGrant(HashSet<string> principals, SecurityAction action)
        {
            switch (action)
            {
                case SecurityAction.Select:
                    return principals.Contains("db_datareader");
                case SecurityAction.Insert:
                case SecurityAction.Update:
                case SecurityAction.Delete:
                    return principals.Contains("db_datawriter");
                case SecurityAction.Execute:
                    return principals.Contains("db_executor");
                case SecurityAction.CreateObject:
                case SecurityAction.AlterObject:
                    return principals.Contains("db_ddladmin");
                case SecurityAction.ViewDefinition:
                    // Any fixed role that carries an object privilege also implies visibility.
                    return principals.Contains("db_datareader") ||
                        principals.Contains("db_datawriter") ||
                        principals.Contains("db_executor") ||
                        principals.Contains("db_ddladmin");
                default:
                    return false;
            }
        }

        private static bool ScopeApplies(PermissionRecord permission, string schema, string obj)
        {
            return permission.Class switch
            {
                SecurableClass.Database => true,
                SecurableClass.Schema => string.Equals(permission.Securable, schema, StringComparison.OrdinalIgnoreCase),
                SecurableClass.Object => string.Equals(permission.Securable, $"{schema}.{obj}", StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }

        private static HashSet<string> CoveringPermissions(SecurityAction action)
        {
            // CONTROL covers everything; ALTER covers DDL. Any of these satisfies the action.
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CONTROL" };
            switch (action)
            {
                case SecurityAction.Select:
                    set.Add("SELECT");
                    break;
                case SecurityAction.Insert:
                    set.Add("INSERT");
                    break;
                case SecurityAction.Update:
                    set.Add("UPDATE");
                    break;
                case SecurityAction.Delete:
                    set.Add("DELETE");
                    break;
                case SecurityAction.Execute:
                    set.Add("EXECUTE");
                    break;
                case SecurityAction.CreateObject:
                case SecurityAction.AlterObject:
                    set.Add("ALTER");
                    break;
                case SecurityAction.ViewDefinition:
                    set.Add("VIEW DEFINITION");
                    set.Add("VIEW");
                    break;
            }

            return set;
        }

        private bool IsServerRoleMember(string user, string serverRole)
        {
            // Resolve the user's login, then test server-role membership of that login.
            var principal = this.catalog.FindPrincipal(user);
            var login = principal?.LoginName;
            if (!string.IsNullOrEmpty(login) && this.catalog.IsServerRoleMember(serverRole, login!))
            {
                return true;
            }

            // Also allow membership keyed directly by the user/principal name.
            return this.catalog.IsServerRoleMember(serverRole, user);
        }
    }
}
