namespace ParqBaseLib.Security
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
