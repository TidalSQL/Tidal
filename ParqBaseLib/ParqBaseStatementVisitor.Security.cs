namespace ParqBaseLib
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Microsoft.SqlServer.TransactSql.ScriptDom;
    using ParqBaseLib.Security;

    /// <summary>
    /// Security statements (authentication + authorization): logins, users, roles, schemas,
    /// GRANT/DENY/REVOKE, and EXECUTE AS / REVERT impersonation.
    /// </summary>
    internal partial class ParqBaseStatementVisitor
    {
        // ---- Authentication: logins --------------------------------------------

        public override void Visit(CreateLoginStatement node)
        {
            try
            {
                this.AuthorizeManageServerSecurity();

                var name = node.Name.Value;
                if (node.Source is not PasswordCreateLoginSource passwordSource ||
                    passwordSource.Password is not StringLiteral password)
                {
                    throw new NotSupportedException("Only 'CREATE LOGIN <name> WITH PASSWORD = '...'' is supported.");
                }

                var (hash, salt) = PasswordHasher.Create(password.Value);

                Directory.CreateDirectory(SystemRoot());
                this.Catalog().AddLogin(new LoginRecord
                {
                    Name = name,
                    Salt = salt,
                    PasswordHash = hash,
                });

                this.LastResult.Message = $"Login '{name}' created.";
            }
            catch (Exception ex)
            {
                this.Fail(ex);
            }
        }

        // ---- Authentication: database users ------------------------------------

        public override void Visit(CreateUserStatement node)
        {
            try
            {
                this.AuthorizeManageDatabaseSecurity();
                this.RequireDatabasePath();

                var name = node.Name.Value;
                string? loginName = null;
                var option = node.UserLoginOption;
                if (option == null || option.UserLoginOptionType == UserLoginOptionType.Login)
                {
                    loginName = option?.Identifier?.Value ?? name;
                    if (!this.Catalog().LoginExists(loginName))
                    {
                        throw new Exception($"Login '{loginName}' does not exist. Create it with CREATE LOGIN first.");
                    }
                }
                else if (option.UserLoginOptionType != UserLoginOptionType.WithoutLogin)
                {
                    throw new NotSupportedException($"Unsupported CREATE USER option: {option.UserLoginOptionType}.");
                }

                this.Catalog().AddPrincipal(new PrincipalRecord
                {
                    Name = name,
                    Kind = PrincipalKind.User,
                    LoginName = loginName,
                    Owner = SecurityCatalog.DboSchema,
                });

                this.LastResult.Message = $"User '{name}' created.";
            }
            catch (Exception ex)
            {
                this.Fail(ex);
            }
        }

        // ---- Authorization: roles ----------------------------------------------

        public override void Visit(CreateRoleStatement node)
        {
            try
            {
                this.AuthorizeManageDatabaseSecurity();
                this.RequireDatabasePath();

                var name = node.Name.Value;
                this.Catalog().AddPrincipal(new PrincipalRecord
                {
                    Name = name,
                    Kind = PrincipalKind.Role,
                    Owner = node.Owner?.Value ?? SecurityCatalog.DboSchema,
                });

                this.LastResult.Message = $"Role '{name}' created.";
            }
            catch (Exception ex)
            {
                this.Fail(ex);
            }
        }

        public override void Visit(AlterRoleStatement node)
        {
            try
            {
                this.AuthorizeManageDatabaseSecurity();
                this.RequireDatabasePath();

                var role = node.Name.Value;
                var catalog = this.Catalog();
                var rolePrincipal = catalog.FindPrincipal(role);
                if (rolePrincipal == null || rolePrincipal.Kind != PrincipalKind.Role)
                {
                    throw new Exception($"Role '{role}' does not exist.");
                }

                switch (node.Action)
                {
                    case AddMemberAlterRoleAction add:
                    {
                        var member = add.Member.Value;
                        if (!catalog.PrincipalExists(member))
                        {
                            throw new Exception($"Principal '{member}' does not exist in this database.");
                        }

                        catalog.AddRoleMember(role, member);
                        this.LastResult.Message = $"'{member}' added to role '{role}'.";
                        break;
                    }

                    case DropMemberAlterRoleAction drop:
                    {
                        var member = drop.Member.Value;
                        catalog.RemoveRoleMember(role, member);
                        this.LastResult.Message = $"'{member}' removed from role '{role}'.";
                        break;
                    }

                    default:
                        throw new NotSupportedException("Only ALTER ROLE ... ADD/DROP MEMBER is supported.");
                }
            }
            catch (Exception ex)
            {
                this.Fail(ex);
            }
        }

        // ---- Securables: schemas -----------------------------------------------

        public override void Visit(CreateSchemaStatement node)
        {
            try
            {
                this.AuthorizeManageDatabaseSecurity();
                this.RequireDatabasePath();

                if (node.Name == null)
                {
                    throw new NotSupportedException("CREATE SCHEMA requires a schema name.");
                }

                var name = node.Name.Value;
                var owner = node.Owner?.Value ?? SecurityCatalog.DboSchema;
                this.Catalog().AddSchema(name, owner);
                this.LastResult.Message = $"Schema '{name}' created.";
            }
            catch (Exception ex)
            {
                this.Fail(ex);
            }
        }

        // ---- Permissions: GRANT / DENY / REVOKE --------------------------------

        public override void Visit(GrantStatement node)
        {
            this.ApplyPermission(node.SecurityTargetObject, node.Permissions, node.Principals, PermissionState.Grant, revoke: false);
        }

        public override void Visit(DenyStatement node)
        {
            this.ApplyPermission(node.SecurityTargetObject, node.Permissions, node.Principals, PermissionState.Deny, revoke: false);
        }

        public override void Visit(RevokeStatement node)
        {
            this.ApplyPermission(node.SecurityTargetObject, node.Permissions, node.Principals, PermissionState.Grant, revoke: true);
        }

        private void ApplyPermission(
            SecurityTargetObject target,
            IList<Permission> permissions,
            IList<SecurityPrincipal> principals,
            PermissionState state,
            bool revoke)
        {
            try
            {
                this.AuthorizeManageDatabaseSecurity();
                this.RequireDatabasePath();

                var (securableClass, securableName) = this.ResolveSecurable(target);
                var tokens = permissions.Select(PermissionToken).ToList();
                var grantees = principals.Select(PrincipalName).ToList();

                var catalog = this.Catalog();
                foreach (var grantee in grantees)
                {
                    if (!catalog.PrincipalExists(grantee))
                    {
                        throw new Exception($"Principal '{grantee}' does not exist in this database.");
                    }
                }

                foreach (var grantee in grantees)
                {
                    foreach (var token in tokens)
                    {
                        if (revoke)
                        {
                            catalog.RevokePermission(grantee, token, securableClass, securableName);
                        }
                        else
                        {
                            catalog.SetPermission(new PermissionRecord
                            {
                                Grantee = grantee,
                                Permission = token,
                                Class = securableClass,
                                Securable = securableName,
                                State = state,
                            });
                        }
                    }
                }

                var verb = revoke ? "revoked from" : state == PermissionState.Deny ? "denied to" : "granted to";
                this.LastResult.Message =
                    $"{string.Join(", ", tokens)} {verb} {string.Join(", ", grantees)} on {securableClass}::{securableName}.";
            }
            catch (Exception ex)
            {
                this.Fail(ex);
            }
        }

        // ---- Authentication context: EXECUTE AS / REVERT -----------------------

        public override void Visit(ExecuteAsStatement node)
        {
            try
            {
                if (node.ExecuteContext?.Kind != ExecuteAsOption.User ||
                    node.ExecuteContext.Principal is not StringLiteral principal)
                {
                    throw new NotSupportedException("Only 'EXECUTE AS USER = '...'' is supported.");
                }

                this.RequireDatabasePath();
                var user = principal.Value;
                if (!this.Catalog().PrincipalExists(user))
                {
                    throw new Exception($"Cannot execute as the database principal '{user}' because it does not exist.");
                }

                this.session.PushUser(user);
                this.LastResult.Message = $"Now executing as user '{user}'.";
            }
            catch (Exception ex)
            {
                this.Fail(ex);
            }
        }

        public override void Visit(RevertStatement node)
        {
            if (this.session.PopUser())
            {
                this.LastResult.Message = "Reverted execution context.";
            }
            else
            {
                this.LastResult.Message = "No impersonation context to revert.";
            }
        }

        // ---- Authorization helpers ---------------------------------------------

        private static string SystemRoot() => ParqBase.SystemRoot;

        private Security.SecurityCatalog Catalog() =>
            new(SystemRoot(), this.session.CurrentDatabasePath);

        private void Authorize(SecurityAction action, string schema, string obj)
        {
            var user = this.session.CurrentUser;
            if (user == null)
            {
                return; // Unauthenticated sessions act as a full-control administrator.
            }

            var authorizer = new Authorizer(this.Catalog());
            if (!authorizer.CanAccessObject(user, action, schema, obj))
            {
                throw new Security.SecurityException(
                    $"Permission denied: {action.ToString().ToUpperInvariant()} on [{schema}].[{obj}] for user '{user}'.");
            }
        }

        private void AuthorizeManageDatabaseSecurity()
        {
            var user = this.session.CurrentUser;
            if (user == null)
            {
                return;
            }

            var authorizer = new Authorizer(this.Catalog());
            if (!authorizer.CanManageDatabaseSecurity(user))
            {
                throw new Security.SecurityException(
                    $"Permission denied: managing database security requires db_owner or db_securityadmin (user '{user}').");
            }
        }

        private void AuthorizeManageServerSecurity()
        {
            var user = this.session.CurrentUser;
            if (user == null)
            {
                return;
            }

            var authorizer = new Authorizer(this.Catalog());
            if (!authorizer.CanManageServerSecurity(user))
            {
                throw new Security.SecurityException(
                    $"Permission denied: managing server security requires sysadmin or securityadmin (user '{user}').");
            }
        }

        private (SecurableClass Class, string Name) ResolveSecurable(SecurityTargetObject target)
        {
            if (target == null)
            {
                throw new NotSupportedException("A securable target is required.");
            }

            var identifiers = target.ObjectName?.MultiPartIdentifier?.Identifiers;
            switch (target.ObjectKind)
            {
                case SecurityObjectKind.Schema:
                    return (SecurableClass.Schema, identifiers!.Last().Value);

                case SecurityObjectKind.Database:
                    return (SecurableClass.Database, this.CurrentDatabaseName());

                default:
                {
                    if (identifiers == null || identifiers.Count == 0)
                    {
                        throw new NotSupportedException("An object securable requires a name.");
                    }

                    var (schema, name) = SplitSchemaObject(identifiers);
                    return (SecurableClass.Object, $"{schema}.{name}");
                }
            }
        }

        private string CurrentDatabaseName() => Path.GetFileName(this.RequireDatabasePath());

        private static (string Schema, string Name) SplitSchemaObject(IList<Identifier> identifiers)
        {
            return identifiers.Count switch
            {
                1 => (SecurityCatalog.DboSchema, identifiers[0].Value),
                2 => (identifiers[0].Value, identifiers[1].Value),
                _ => (identifiers[^2].Value, identifiers[^1].Value),
            };
        }

        private static string PermissionToken(Permission permission) =>
            string.Join(" ", permission.Identifiers.Select(i => i.Value)).ToUpperInvariant();

        private static string PrincipalName(SecurityPrincipal principal) =>
            principal.Identifier?.Value ?? SecurityCatalog.PublicRole;

        // ---- Security catalog views --------------------------------------------

        private RowSet? TryBuildSecurityCatalog(NamedTableReference named, string qualifier)
        {
            if (IsSystemCatalog(named, "server_principals"))
            {
                return this.BuildServerPrincipalsRowSet(qualifier);
            }

            if (IsSystemCatalog(named, "database_principals"))
            {
                return this.BuildDatabasePrincipalsRowSet(qualifier);
            }

            if (IsSystemCatalog(named, "database_role_members"))
            {
                return this.BuildRoleMembersRowSet(qualifier);
            }

            if (IsSystemCatalog(named, "schemas"))
            {
                return this.BuildSchemasRowSet(qualifier);
            }

            if (IsSystemCatalog(named, "database_permissions"))
            {
                return this.BuildPermissionsRowSet(qualifier);
            }

            return null;
        }

        private RowSet BuildServerPrincipalsRowSet(string qualifier)
        {
            var schema = new List<ColumnRef> { new(qualifier, "name"), new(qualifier, "type") };
            var rows = new List<object?[]>();
            foreach (var login in this.Catalog().GetLogins().OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
            {
                rows.Add(new object?[] { login.Name, "SQL_LOGIN" });
            }

            foreach (var role in SecurityCatalog.FixedServerRoles)
            {
                rows.Add(new object?[] { role, "SERVER_ROLE" });
            }

            return new RowSet(schema, rows);
        }

        private RowSet BuildDatabasePrincipalsRowSet(string qualifier)
        {
            var schema = new List<ColumnRef> { new(qualifier, "name"), new(qualifier, "type") };
            var rows = this.Catalog().GetPrincipals()
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p => new object?[]
                {
                    p.Name,
                    p.Kind == PrincipalKind.User ? "SQL_USER" : "DATABASE_ROLE",
                })
                .ToList();
            return new RowSet(schema, rows);
        }

        private RowSet BuildRoleMembersRowSet(string qualifier)
        {
            var schema = new List<ColumnRef> { new(qualifier, "role_name"), new(qualifier, "member_name") };
            var rows = this.Catalog().GetRoleMembers()
                .OrderBy(m => m.Role, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.Member, StringComparer.OrdinalIgnoreCase)
                .Select(m => new object?[] { m.Role, m.Member })
                .ToList();
            return new RowSet(schema, rows);
        }

        private RowSet BuildSchemasRowSet(string qualifier)
        {
            var schema = new List<ColumnRef> { new(qualifier, "name"), new(qualifier, "owner") };
            var rows = this.Catalog().GetSchemas()
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(s => new object?[] { s.Name, s.Owner })
                .ToList();
            return new RowSet(schema, rows);
        }

        private RowSet BuildPermissionsRowSet(string qualifier)
        {
            var schema = new List<ColumnRef>
            {
                new(qualifier, "grantee"),
                new(qualifier, "permission"),
                new(qualifier, "class"),
                new(qualifier, "securable"),
                new(qualifier, "state"),
            };
            var rows = this.Catalog().GetPermissions()
                .OrderBy(p => p.Grantee, StringComparer.OrdinalIgnoreCase)
                .Select(p => new object?[]
                {
                    p.Grantee,
                    p.Permission,
                    p.Class.ToString().ToUpperInvariant(),
                    p.Securable,
                    p.State == PermissionState.Deny ? "DENY" : "GRANT",
                })
                .ToList();
            return new RowSet(schema, rows);
        }
    }
}
