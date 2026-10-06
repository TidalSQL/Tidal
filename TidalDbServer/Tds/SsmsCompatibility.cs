namespace TidalDbServer.Tds
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;
    using TidalDbLib;

    /// <summary>
    /// Recognises the fixed, version-stable batches that SQL Server Management Studio (and other tools
    /// built on SMO) issue while establishing a connection and populating Object Explorer, and answers
    /// them with synthesized result sets. These batches use constructs the TidalDb engine does not
    /// implement — <c>SERVERPROPERTY</c>, <c>CONNECTIONPROPERTY</c>, <c>@@MICROSOFTVERSION</c>,
    /// <c>sys.dm_os_host_info</c>, multi-statement <c>DECLARE/SET/IF</c> blocks — so without this shim the
    /// connection fails before the client reaches a query window. Ordinary user queries are not matched
    /// here and fall through to the engine.
    /// </summary>
    internal static class SsmsCompatibility
    {
        private const RegexOptions ReOpts = RegexOptions.IgnoreCase | RegexOptions.Singleline;

        // Values reported to make the server look like a modern, on-premises SQL Server instance.
        private const int DatabaseEngineType = 1;        // 1 = on-premises (NT), 2 = SQL Azure
        private const int EngineEdition = 3;             // 3 = Enterprise/Developer (full feature set)
        private const string ProductVersion = "16.0.1000.6";
        private const int MicrosoftVersion = (16 << 24) | 1000; // encodes 16.0.x.1000
        private const string HostPlatform = "Windows";
        private const string ConnectionProtocol = "TCP";

        /// <summary>
        /// Attempts to handle a SQL batch as an SSMS connection/metadata query. When it returns
        /// <c>true</c>, <paramref name="resultSets"/> holds the result sets to stream back (possibly
        /// empty, meaning a successful statement that returns no rows). When it returns <c>false</c>, the
        /// batch is a normal query that must be executed by the engine.
        /// </summary>
        public static bool TryHandle(string sql, out List<QueryResult> resultSets)
        {
            resultSets = new List<QueryResult>();
            var text = sql.Trim();
            if (text.Length == 0)
            {
                return false;
            }

            // The server-information probe SSMS runs immediately after login (DatabaseEngineType,
            // EngineEdition, ProductVersion, MicrosoftVersion, host_platform, ConnectionProtocol).
            if (Contains(text, "DatabaseEngineType") && Contains(text, "EngineEdition"))
            {
                resultSets.Add(Row(
                    ("DatabaseEngineType", DatabaseEngineType),
                    ("DatabaseEngineEdition", EngineEdition),
                    ("ProductVersion", ProductVersion),
                    ("MicrosoftVersion", MicrosoftVersion)));
                resultSets.Add(Row(("host_platform", HostPlatform)));
                resultSets.Add(Row(("ConnectionProtocol", ConnectionProtocol)));
                return true;
            }

            // SSMS probes whether this is an admin (DAC) connection and whether SMO is enabled, to decide
            // which Object Explorer features to offer. Report: not a DAC connection, SMO enabled.
            if (Contains(text, "AdminConnection") && Contains(text, "SmoEnabled"))
            {
                resultSets.Add(Row(
                    ("AdminConnection", false),
                    ("SmoEnabled", true)));
                return true;
            }

            // SMO's Server object initialization batch (builds a #SVer temp table from xp_msver and
            // sys.dm_os_host_info, then SELECTs a single row of server-level properties). This gates
            // Object Explorer: without a valid row the server node will not expand. The temp-table
            // scaffolding only computes values we already know, so answer with the final row directly.
            if (Contains(text, "Server_Urn") && Contains(text, "VersionMajor"))
            {
                var machine = Environment.MachineName;
                resultSets.Add(Row(
                    ("Server_Name", machine),
                    ("Server_Urn", $"Server[@Name='{machine}']"),
                    ("Server_ServerType", TypedNull.Int),
                    ("Server_Status", 1),
                    ("Server_IsContainedAuthentication", 0),
                    ("VersionMajor", (MicrosoftVersion / 0x1000000) & 0xff),
                    ("VersionMinor", (MicrosoftVersion / 0x10000) & 0xff),
                    ("BuildNumber", MicrosoftVersion & 0xffff),
                    ("IsSingleUser", false),
                    ("Edition", "Developer Edition (64-bit)"),
                    ("EngineEdition", EngineEdition),
                    ("IsXTPSupported", true),
                    ("VersionString", ProductVersion),
                    ("HostPlatform", HostPlatform),
                    ("IsFullTextInstalled", false)));
                return true;
            }

            // A batch made up only of SET session options (QUOTED_IDENTIFIER, ANSI_NULLS, LOCK_TIMEOUT,
            // TRANSACTION ISOLATION LEVEL, DATEFORMAT, LANGUAGE, ...) — accept and return no rows.
            if (IsSetOnlyBatch(text))
            {
                return true;
            }

            // SMO enumerates server state through many single-row scalar SELECTs built from
            // SERVERPROPERTY(...), CONNECTIONPROPERTY(...) and @@globals with no FROM clause. Evaluate
            // those generically so new property probes do not each need a bespoke handler.
            if (TryHandleScalarSelect(text, out var scalar))
            {
                resultSets.Add(scalar);
                return true;
            }

            return false;
        }

        private static bool IsSetOnlyBatch(string text)
        {
            var statements = text
                .Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0 && !s.StartsWith("--", StringComparison.Ordinal))
                .ToList();

            if (statements.Count == 0)
            {
                return false;
            }

            return statements.All(s =>
                s.StartsWith("set ", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("go", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Handles a single-statement <c>SELECT</c> whose column list is composed only of scalar
        /// expressions (SERVERPROPERTY/CONNECTIONPROPERTY calls, @@globals, literals, CAST/CONVERT around
        /// those) and which has no <c>FROM</c> clause. Returns a one-row result with each aliased value.
        /// </summary>
        private static bool TryHandleScalarSelect(string text, out QueryResult result)
        {
            result = new QueryResult { Success = true, RowCount = 1 };

            var body = text.TrimEnd(';', ' ', '\r', '\n', '\t');
            if (!Regex.IsMatch(body, @"^\s*select\b", ReOpts))
            {
                return false;
            }

            // Only plain scalar projections — reject anything with a FROM/JOIN/WHERE or set operation.
            if (Regex.IsMatch(body, @"\b(from|join|where|into|union)\b", ReOpts))
            {
                return false;
            }

            // Must reference a construct the engine cannot evaluate, otherwise defer to the engine.
            if (!ReferencesCompat(body))
            {
                return false;
            }

            var selectList = body.Substring(body.IndexOf("select", StringComparison.OrdinalIgnoreCase) + "select".Length).Trim();
            var row = new Dictionary<string, object?>();
            var index = 0;
            foreach (var item in SplitTopLevel(selectList))
            {
                index++;
                var (expr, alias) = SplitAlias(item.Trim());
                if (string.IsNullOrEmpty(alias))
                {
                    alias = DeriveAlias(expr) ?? $"Column{index}";
                }

                if (result.Columns.Contains(alias))
                {
                    alias = $"{alias}_{index}";
                }

                result.Columns.Add(alias);
                row[alias] = EvaluateScalar(expr);
            }

            if (result.Columns.Count == 0)
            {
                return false;
            }

            result.Rows.Add(row);
            return true;
        }

        /// <summary>Splits a comma-separated list on commas that sit at paren depth zero and outside quotes.</summary>
        private static IEnumerable<string> SplitTopLevel(string s)
        {
            var depth = 0;
            var inQuote = false;
            var start = 0;
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '\'')
                {
                    inQuote = !inQuote;
                }
                else if (!inQuote && c == '(')
                {
                    depth++;
                }
                else if (!inQuote && c == ')')
                {
                    depth--;
                }
                else if (!inQuote && depth == 0 && c == ',')
                {
                    yield return s.Substring(start, i - start);
                    start = i + 1;
                }
            }

            if (start < s.Length)
            {
                yield return s.Substring(start);
            }
        }

        /// <summary>Separates a column expression from a trailing <c>AS alias</c> at paren depth zero.</summary>
        private static (string Expr, string? Alias) SplitAlias(string item)
        {
            var depth = 0;
            var inQuote = false;
            for (var i = 0; i + 2 < item.Length; i++)
            {
                var c = item[i];
                if (c == '\'')
                {
                    inQuote = !inQuote;
                }
                else if (!inQuote && c == '(')
                {
                    depth++;
                }
                else if (!inQuote && c == ')')
                {
                    depth--;
                }
                else if (!inQuote && depth == 0 &&
                    char.IsWhiteSpace(c) &&
                    (item[i + 1] == 'a' || item[i + 1] == 'A') &&
                    (item[i + 2] == 's' || item[i + 2] == 'S') &&
                    (i + 3 >= item.Length || char.IsWhiteSpace(item[i + 3])))
                {
                    var expr = item.Substring(0, i).Trim();
                    var alias = item.Substring(i + 3).Trim();
                    return (expr, CleanIdentifier(alias));
                }
            }

            // No explicit AS: recognise an implicit trailing alias (e.g. "SERVERPROPERTY('X') X" or
            // "'lit' alias"). Only when the expression part ends a function call or string literal and
            // the trailing token is a single identifier, so arithmetic like "& 0xff" is not mis-split.
            var ws = LastTopLevelWhitespace(item);
            if (ws > 0)
            {
                var left = item.Substring(0, ws).TrimEnd();
                var right = item.Substring(ws + 1).Trim();
                if ((left.EndsWith(")", StringComparison.Ordinal) || left.EndsWith("'", StringComparison.Ordinal)) &&
                    Regex.IsMatch(right, @"^(\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)$"))
                {
                    return (left, CleanIdentifier(right));
                }
            }

            return (item.Trim(), null);
        }

        /// <summary>Index of the last whitespace character at paren depth zero and outside quotes.</summary>
        private static int LastTopLevelWhitespace(string item)
        {
            var depth = 0;
            var inQuote = false;
            var last = -1;
            for (var i = 0; i < item.Length; i++)
            {
                var c = item[i];
                if (c == '\'')
                {
                    inQuote = !inQuote;
                }
                else if (!inQuote && c == '(')
                {
                    depth++;
                }
                else if (!inQuote && c == ')')
                {
                    depth--;
                }
                else if (!inQuote && depth == 0 && char.IsWhiteSpace(c))
                {
                    last = i;
                }
            }

            return last;
        }

        private static string CleanIdentifier(string name)
        {
            name = name.Trim();
            if (name.StartsWith("[", StringComparison.Ordinal) && name.EndsWith("]", StringComparison.Ordinal))
            {
                return name.Substring(1, name.Length - 2);
            }

            if (name.EndsWith("'", StringComparison.Ordinal) &&
                (name.StartsWith("N'", StringComparison.OrdinalIgnoreCase) || name.StartsWith("'", StringComparison.Ordinal)))
            {
                var start = name.IndexOf('\'') + 1;
                return name.Substring(start, name.Length - start - 1);
            }

            return name;
        }

        /// <summary>Derives a column name from a bare SERVERPROPERTY/global expression when no alias is given.</summary>
        private static string? DeriveAlias(string expr)
        {
            var prop = Regex.Match(expr, @"SERVERPROPERTY\s*\(\s*N?'([^']+)'", ReOpts);
            if (prop.Success)
            {
                return prop.Groups[1].Value;
            }

            var global = Regex.Match(expr, @"@@(\w+)", ReOpts);
            return global.Success ? global.Groups[1].Value : null;
        }

        /// <summary>Evaluates a scalar expression used in SMO/SSMS probes, unwrapping CAST/CONVERT.</summary>
        private static object? EvaluateScalar(string expr)
        {
            expr = expr.Trim();

            // Strip a single fully-enclosing pair of parentheses.
            if (expr.Length >= 2 && expr[0] == '(' && expr[expr.Length - 1] == ')' &&
                SplitTopLevel(expr.Substring(1, expr.Length - 2)).Count() == 1)
            {
                var inner = expr.Substring(1, expr.Length - 2).Trim();
                if (!inner.Contains('('))
                {
                    expr = inner;
                }
            }

            // CAST(inner AS type) -> evaluate inner.
            var cast = Regex.Match(expr, @"^cast\s*\((.*)\s+as\s+[^\)]+\)$", ReOpts);
            if (cast.Success)
            {
                return EvaluateScalar(cast.Groups[1].Value);
            }

            // CASE WHEN <cond> THEN <a> ELSE <b> END -> evaluate the taken branch.
            var caseExpr = Regex.Match(
                expr, @"^case\s+when\s+(.+?)\s+then\s+(.+?)\s+else\s+(.+?)\s+end$", ReOpts);
            if (caseExpr.Success)
            {
                return EvaluateCondition(caseExpr.Groups[1].Value)
                    ? EvaluateScalar(caseExpr.Groups[2].Value)
                    : EvaluateScalar(caseExpr.Groups[3].Value);
            }

            // Niladic functions (no parentheses): SYSTEM_USER, SESSION_USER, CURRENT_USER.
            if (NiladicValues.TryGetValue(expr, out var niladic))
            {
                return niladic;
            }

            // @@global.
            var global = Regex.Match(expr, @"^@@(\w+)$", ReOpts);
            if (global.Success)
            {
                return Globals.TryGetValue(global.Groups[1].Value, out var gv) ? gv : null;
            }

            // Generic function call name(args).
            var fn = Regex.Match(expr, @"^([A-Za-z_][A-Za-z0-9_]*)\s*\((.*)\)$", ReOpts);
            if (fn.Success)
            {
                var name = fn.Groups[1].Value.ToUpperInvariant();
                var args = SplitTopLevel(fn.Groups[2].Value)
                    .Select(a => a.Trim())
                    .Where(a => a.Length > 0)
                    .ToList();
                return EvaluateFunction(name, args);
            }

            // Integer literal (decimal or hex).
            if (Regex.IsMatch(expr, @"^-?\d+$"))
            {
                return int.Parse(expr, System.Globalization.CultureInfo.InvariantCulture);
            }

            var hex = Regex.Match(expr, @"^0x([0-9a-fA-F]+)$", ReOpts);
            if (hex.Success)
            {
                return (int)Convert.ToInt64(hex.Groups[1].Value, 16);
            }

            var str = Regex.Match(expr, @"^N?'(.*)'$", ReOpts);
            if (str.Success)
            {
                return str.Groups[1].Value.Replace("''", "'");
            }

            return null;
        }

        /// <summary>Evaluates a recognized scalar metadata function call to a plausible value for SMO.</summary>
        private static object? EvaluateFunction(string name, List<string> args)
        {
            switch (name)
            {
                case "SERVERPROPERTY":
                    return args.Count > 0 && ServerProperties.TryGetValue(Unquote(args[0]), out var sp) ? sp : null;
                case "CONNECTIONPROPERTY":
                    return args.Count > 0 && ConnectionProperties.TryGetValue(Unquote(args[0]), out var cp) ? cp : null;
                case "CONVERT":
                    // CONVERT(type, inner [, style]) -> evaluate the second argument.
                    return args.Count >= 2 ? EvaluateScalar(args[1]) : null;
                case "ISNULL":
                case "COALESCE":
                    foreach (var a in args)
                    {
                        var v = EvaluateScalar(a);
                        if (v != null && v is not TypedNull)
                        {
                            return v;
                        }
                    }

                    return args.Count > 0 ? EvaluateScalar(args[args.Count - 1]) : null;

                // Access / membership probes -> grant.
                case "HAS_DBACCESS":
                case "IS_SRVROLEMEMBER":
                case "IS_MEMBER":
                case "IS_ROLEMEMBER":
                case "HAS_PERMS_BY_NAME":
                    return 1;

                case "FULLTEXTSERVICEPROPERTY":
                    return 0;

                // Principal-name probes.
                case "SUSER_SNAME":
                case "SUSER_NAME":
                case "ORIGINAL_LOGIN":
                    return "admin";
                case "USER_NAME":
                case "SCHEMA_NAME":
                    return "dbo";
                case "DB_NAME":
                    return "master";

                // Id probes.
                case "SUSER_ID":
                case "USER_ID":
                case "SCHEMA_ID":
                case "DB_ID":
                    return 1;
                case "OBJECT_ID":
                    return TypedNull.Int;

                case "OBJECT_NAME":
                case "DATABASEPROPERTYEX":
                case "COLLATIONPROPERTY":
                    return null;

                default:
                    return null;
            }
        }

        /// <summary>Evaluates a simple boolean condition (IS [NOT] NULL, = , &lt;&gt;) used in CASE probes.</summary>
        private static bool EvaluateCondition(string cond)
        {
            cond = cond.Trim();

            var isNull = Regex.Match(cond, @"^(.+?)\s+is\s+(not\s+)?null$", ReOpts);
            if (isNull.Success)
            {
                var value = EvaluateScalar(isNull.Groups[1].Value);
                var isNotNull = isNull.Groups[2].Success;
                return isNotNull ? !IsNullish(value) : IsNullish(value);
            }

            var neq = Regex.Match(cond, @"^(.+?)\s*(<>|!=)\s*(.+)$", ReOpts);
            if (neq.Success)
            {
                return !object.Equals(EvaluateScalar(neq.Groups[1].Value), EvaluateScalar(neq.Groups[3].Value));
            }

            var eq = Regex.Match(cond, @"^(.+?)\s*=\s*(.+)$", ReOpts);
            if (eq.Success)
            {
                return object.Equals(EvaluateScalar(eq.Groups[1].Value), EvaluateScalar(eq.Groups[2].Value));
            }

            var value2 = EvaluateScalar(cond);
            return value2 is int i ? i != 0 : !IsNullish(value2);
        }

        /// <summary>True when a value represents SQL NULL (CLR null or a typed-null marker).</summary>
        private static bool IsNullish(object? value) => value is null || value is TypedNull;

        /// <summary>Removes a surrounding string-literal quote (optionally N-prefixed), if present.</summary>
        private static string Unquote(string arg)
        {
            arg = arg.Trim();
            var m = Regex.Match(arg, @"^N?'(.*)'$", ReOpts);
            return m.Success ? m.Groups[1].Value.Replace("''", "'") : arg;
        }

        /// <summary>Scalar functions/constructs the engine cannot evaluate but SMO probes expect.</summary>
        private static bool ReferencesCompat(string body)
        {
            if (body.Contains("@@", StringComparison.Ordinal))
            {
                return true;
            }

            foreach (var f in CompatFunctions)
            {
                if (Contains(body, f))
                {
                    return true;
                }
            }

            foreach (var n in NiladicValues.Keys)
            {
                if (Regex.IsMatch(body, $@"\b{n}\b", ReOpts))
                {
                    return true;
                }
            }

            return false;
        }

        private static readonly string[] CompatFunctions =
        {
            "SERVERPROPERTY", "CONNECTIONPROPERTY", "HAS_DBACCESS", "IS_SRVROLEMEMBER",
            "IS_MEMBER", "IS_ROLEMEMBER", "HAS_PERMS_BY_NAME", "SUSER_SNAME", "SUSER_NAME",
            "SUSER_ID", "ORIGINAL_LOGIN", "USER_NAME", "USER_ID", "DB_NAME", "DB_ID",
            "SCHEMA_NAME", "SCHEMA_ID", "OBJECT_ID", "OBJECT_NAME", "FULLTEXTSERVICEPROPERTY",
            "DATABASEPROPERTYEX", "COLLATIONPROPERTY",
        };

        private static readonly Dictionary<string, object?> NiladicValues =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["SYSTEM_USER"] = "admin",
                ["SESSION_USER"] = "dbo",
                ["CURRENT_USER"] = "dbo",
            };

        private static bool Contains(string haystack, string needle) =>
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Builds a single-row result set from ordered (columnName, value) pairs.</summary>
        private static QueryResult Row(params (string Name, object? Value)[] columns)
        {
            var result = new QueryResult { Success = true, RowCount = 1 };
            var row = new Dictionary<string, object?>();
            foreach (var (name, value) in columns)
            {
                result.Columns.Add(name);
                row[name] = value;
            }

            result.Rows.Add(row);
            return result;
        }

        private static readonly Dictionary<string, object?> ServerProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Edition"] = "Developer Edition (64-bit)",
            ["EditionID"] = -2117995310L,
            ["EngineEdition"] = EngineEdition,
            ["ProductVersion"] = ProductVersion,
            ["ProductLevel"] = "RTM",
            ["ProductUpdateLevel"] = null,
            ["ProductMajorVersion"] = 16,
            ["ProductMinorVersion"] = 0,
            ["ProductBuild"] = 1000,
            ["ProductBuildType"] = null,
            ["Collation"] = "SQL_Latin1_General_CP1_CI_AS",
            ["CollationID"] = 872468488,
            ["ComparisonStyle"] = 196609,
            ["SqlCharSet"] = 1,
            ["SqlCharSetName"] = "iso_1",
            ["SqlSortOrder"] = 52,
            ["SqlSortOrderName"] = "nocase_iso",
            ["MachineName"] = Environment.MachineName,
            ["ComputerNamePhysicalNetBIOS"] = Environment.MachineName,
            ["ServerName"] = Environment.MachineName,
            ["InstanceName"] = null,
            ["InstanceDefaultDataPath"] = null,
            ["InstanceDefaultLogPath"] = null,
            ["IsClustered"] = 0,
            ["IsHadrEnabled"] = 0,
            ["HadrManagerStatus"] = null,
            ["IsSingleUser"] = 0,
            ["IsIntegratedSecurityOnly"] = 1,
            ["IsFullTextInstalled"] = 0,
            ["IsXTPSupported"] = 1,
            ["IsPolyBaseInstalled"] = 0,
            ["IsAdvancedAnalyticsInstalled"] = 0,
            ["FilestreamConfiguredLevel"] = 0,
            ["FilestreamEffectiveLevel"] = 0,
            ["BuildClrVersion"] = "v4.0.30319",
            ["LCID"] = 1033,
            ["IsLocalDB"] = 0,
            ["ResourceVersion"] = ProductVersion,
            ["ResourceLastUpdateDateTime"] = null,
        };

        private static readonly Dictionary<string, object?> ConnectionProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            ["net_transport"] = "TCP",
            ["protocol_type"] = "TSQL",
            ["auth_scheme"] = "SQL",
            ["physical_net_transport"] = "TCP",
            ["local_net_address"] = "127.0.0.1",
            ["local_tcp_port"] = 1433,
            ["client_net_address"] = "127.0.0.1",
        };

        private static readonly Dictionary<string, object?> Globals = new(StringComparer.OrdinalIgnoreCase)
        {
            ["MICROSOFTVERSION"] = MicrosoftVersion,
            ["VERSION"] = "Microsoft SQL Server 2022 (RTM) - 16.0.1000.6 (X64) - TidalDb compatibility layer",
            ["SERVERNAME"] = Environment.MachineName,
            ["SERVICENAME"] = "MSSQLSERVER",
            ["SPID"] = 51,
            ["LANGUAGE"] = "us_english",
            ["LANGID"] = 0,
            ["MAX_PRECISION"] = 38,
            ["LOCK_TIMEOUT"] = -1,
            ["TRANCOUNT"] = 0,
            ["NESTLEVEL"] = 0,
            ["OPTIONS"] = 0,
            ["ERROR"] = 0,
            ["ROWCOUNT"] = 0,
            ["FETCH_STATUS"] = 0,
            ["TEXTSIZE"] = -1,
        };
    }
}
