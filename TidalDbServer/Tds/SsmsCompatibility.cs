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
            => TryHandle(sql, null, out resultSets);

        /// <summary>
        /// As <see cref="TryHandle(string, out List{QueryResult})"/>, but also able to answer SSMS Object
        /// Explorer's Databases-folder enumeration when a <paramref name="databaseSource"/> is supplied.
        /// </summary>
        public static bool TryHandle(string sql, Func<IReadOnlyList<TidalDb.ExplorerDatabase>>? databaseSource, out List<QueryResult> resultSets)
        {
            resultSets = new List<QueryResult>();
            var text = sql.Trim();
            if (text.Length == 0)
            {
                return false;
            }

            // SSMS Object Explorer's Databases folder issues a large multi-statement batch (temp table +
            // IF serverproperty(...) + an ~83-column SELECT from sys.databases with LEFT JOINs to catalog
            // views the engine lacks, run in master context). It fails in the engine, so the folder never
            // populates. Synthesize the rows directly from the real on-disk databases.
            if (databaseSource != null && IsDatabasesEnumeration(text))
            {
                resultSets.Add(BuildDatabasesEnumerationResult(text, databaseSource));
                return true;
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

            // SMO's full Server-properties probe (a large multi-statement DECLARE/SET/SELECT batch that
            // reports AuditLevel, LoginMode, Collation, MasterDBPath, etc.). It uses @@globals,
            // xp_instance_regread, msdb policy functions and suser_sname — none of which the engine
            // implements — and it gates Object Explorer: if it fails, SMO aborts before enumerating
            // databases. Answer with a synthesized single row of plausible server settings.
            if (Contains(text, "[AuditLevel]") && Contains(text, "[LoginMode]") && Contains(text, "[MasterDBPath]"))
            {
                resultSets.Add(BuildServerSettingsRow());
                return true;
            }

            // A batch made up only of SET session options (QUOTED_IDENTIFIER, ANSI_NULLS, LOCK_TIMEOUT,
            // TRANSACTION ISOLATION LEVEL, DATEFORMAT, LANGUAGE, ...) — accept and return no rows.
            if (IsSetOnlyBatch(text))
            {
                return true;
            }

            // After enumerating databases, SMO reads server-level sp_configure settings from
            // sys.configurations (default language/full-text language, nested triggers, two-digit year
            // cutoff). The engine has no sys.configurations and runs in master context, so synthesize it.
            if (Contains(text, "sys.configurations") && Contains(text, "configuration_id"))
            {
                resultSets.Add(BuildConfigurationsResult(text));
                return true;
            }

            // SSMS Object Explorer constructs a DMF PolicyStore while evaluating the server node's state
            // (NavigableItem.get_State -> PolicyStore..ctor). The probe SELECTs PolicyStore properties from
            // msdb.dbo.syspolicy_configuration using quotename(); the engine implements neither, so it fails
            // and the server node enters an error state (red X) BEFORE the Databases folder is enumerated —
            // which is why user databases never appear. Answer with a benign, disabled-policy row.
            if (Contains(text, "PolicyStore[@Name=") || Contains(text, "syspolicy_configuration"))
            {
                resultSets.Add(BuildPolicyStoreRow());
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

            // A trailing ORDER BY over a from-less projection is a no-op for a single synthesized row.
            body = Regex.Replace(body, @"\s+order\s+by\s+.*$", string.Empty, ReOpts).TrimEnd();

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

        /// <summary>Splits on top-level <c>+</c> concatenation operators outside quotes and parentheses.</summary>
        private static IEnumerable<string> SplitTopLevelConcat(string s)
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
                else if (!inQuote && depth == 0 && c == '+')
                {
                    yield return s.Substring(start, i - start);
                    start = i + 1;
                }
            }

            if (start <= s.Length)
            {
                yield return s.Substring(start);
            }
        }

        /// <summary>Emulates T-SQL QUOTENAME: wraps a value in the given delimiter, doubling any inside.</summary>
        private static string QuoteName(string value, string delimiter)
        {
            var open = delimiter.Length > 0 ? delimiter[0] : '[';
            return open switch
            {
                '\'' => "'" + value.Replace("'", "''") + "'",
                '"' => "\"" + value.Replace("\"", "\"\"") + "\"",
                _ => "[" + value.Replace("]", "]]") + "]",
            };
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

            // String concatenation (a + b + c) at paren depth zero.
            var concat = SplitTopLevelConcat(expr).Select(p => p.Trim()).ToList();
            if (concat.Count > 1)
            {
                var values = concat.Select(EvaluateScalar).ToList();
                if (values.Any(IsNullish))
                {
                    return null;
                }

                return string.Concat(values.Select(
                    v => System.Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)));
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
                case "QUOTENAME":
                {
                    var value = args.Count > 0
                        ? System.Convert.ToString(
                            EvaluateScalar(args[0]), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
                        : string.Empty;
                    var delimiter = args.Count > 1 ? Unquote(args[1]) : "[";
                    return QuoteName(value, delimiter);
                }
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
            "DATABASEPROPERTYEX", "COLLATIONPROPERTY", "QUOTENAME",
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

        private static readonly Regex DatabasesFromRe =
            new(@"from\s+(?:master\s*\.\s*)?sys\.databases\s+(?:as\s+)?dtb\b", ReOpts);

        /// <summary>
        /// Recognises any SSMS/SMO enumeration of <c>sys.databases AS dtb</c> — the Object Explorer
        /// Databases folder, its System Databases / Snapshots subfolders, and single-database refreshes.
        /// Each variant SMO sends is a <em>different</em> column subset (with <c>Database_</c>-prefixed
        /// aliases, read by ordinal), so detection is by the FROM clause alone, not the SELECT list.
        /// </summary>
        private static bool IsDatabasesEnumeration(string text) => DatabasesFromRe.IsMatch(text);

        private static readonly string[] SystemDatabaseNames = { "master", "tempdb", "model", "msdb" };

        /// <summary>
        /// Synthesizes the Databases-folder result SSMS expects. SMO's Database object reads ~80 typed
        /// properties per row; a missing column or wrong wire type makes the whole folder fail to expand.
        /// The WHERE filter SMO appends selects a variant: system databases (<c>... AS bit)=1</c>), user
        /// databases (<c>=0</c>), a single named database, or snapshots (always empty here).
        /// </summary>
        private static QueryResult BuildDatabasesEnumerationResult(string text, Func<IReadOnlyList<TidalDb.ExplorerDatabase>> databaseSource)
        {
            // SMO reads each result set BY ORDINAL, and every Object Explorer action requests a different
            // column subset. So the only robust answer is to echo exactly the SELECT list SMO sent — in
            // order — resolving each aliased column to a correctly-typed synthesized value.
            var columns = ExtractDatabasesSelectColumns(text);
            var rows = SelectDatabaseRows(text, databaseSource);

            var result = new QueryResult { Success = true };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var emitted = new List<(string Expr, string Alias, string Key)>();
            var index = 0;
            foreach (var (expr, alias) in columns)
            {
                var original = alias ?? DeriveDatabaseColumnName(expr, index);
                var key = original;
                var suffix = 1;
                while (!seen.Add(key))
                {
                    key = original + "_" + suffix++;
                }

                emitted.Add((expr, original, key));
                result.Columns.Add(key);
                index++;
            }

            foreach (var db in rows)
            {
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (expr, alias, key) in emitted)
                {
                    row[key] = ResolveDatabaseColumn(expr, alias, db);
                }

                result.Rows.Add(row);
            }

            result.RowCount = rows.Count;
            return result;
        }

        /// <summary>
        /// Extracts the depth-0 SELECT column list that feeds the <c>sys.databases AS dtb</c> FROM clause,
        /// as (expression, alias) pairs in SELECT order. Subquery SELECTs (inside parens) and SELECTs
        /// embedded in string literals (e.g. the batch's <c>exec sp_executesql N'...'</c> preamble) are
        /// skipped, so only the real projection is parsed.
        /// </summary>
        private static IReadOnlyList<(string Expr, string? Alias)> ExtractDatabasesSelectColumns(string text)
            => ExtractSelectColumns(text, DatabasesFromRe);

        /// <summary>Index just past the last depth-0, unquoted <c>SELECT</c> keyword before <paramref name="before"/>.</summary>
        private static int LastTopLevelSelectEnd(string text, int before)
        {
            var depth = 0;
            var inQuote = false;
            var result = -1;
            for (var i = 0; i < before; i++)
            {
                var c = text[i];
                if (c == '\'')
                {
                    inQuote = !inQuote;
                    continue;
                }

                if (inQuote)
                {
                    continue;
                }

                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth--;
                }
                else if (depth == 0 && (c is 's' or 'S') &&
                    i + 6 <= text.Length &&
                    string.Compare(text, i, "select", 0, 6, StringComparison.OrdinalIgnoreCase) == 0 &&
                    (i == 0 || !IsWordChar(text[i - 1])) &&
                    (i + 6 >= text.Length || !IsWordChar(text[i + 6])))
                {
                    result = i + 6;
                }
            }

            return result;
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>
        /// Returns the last match of <paramref name="re"/> in <paramref name="text"/>. The real Object
        /// Explorer databases query mentions <c>sys.databases AS dtb</c> several times — twice inside the
        /// HADR temp-table preamble and finally in the real projection — so callers must key off the
        /// LAST occurrence (the projection) rather than the first (a preamble helper SELECT).
        /// </summary>
        private static Match LastMatch(Regex re, string text)
        {
            var last = Match.Empty;
            for (var m = re.Match(text); m.Success; m = m.NextMatch())
            {
                last = m;
            }

            return last;
        }

        /// <summary>
        /// Selects which databases a Databases-enumeration query targets by inspecting its WHERE clause:
        /// a single named database, the System Databases subfolder (<c>... AS bit)=1</c>), the Snapshots
        /// subfolder (always empty), or — the one that matters — the user Databases folder.
        /// </summary>
        private static List<DatabaseRow> SelectDatabaseRows(string text, Func<IReadOnlyList<TidalDb.ExplorerDatabase>> databaseSource)
        {
            var rows = new List<DatabaseRow>();

            var from = LastMatch(DatabasesFromRe, text);
            var where = from.Success ? text.Substring(from.Index + from.Length) : text;

            var nameMatch = Regex.Match(where, @"\[?dtb\]?\.\[?name\]?\s*=\s*N'([^']*)'", ReOpts);

            // The snapshots subfolder *wants* snapshots: "...source_database_id...) AS bit)=N'1'" or
            // "source_database_id is not null". The user/system Databases folders *exclude* them with
            // "...isnull(dtb.source_database_id,0) AS bit)=N'0'", which must NOT be treated as snapshots.
            var wantsSnapshots = Regex.IsMatch(where, @"source_database_id[^)]*\)\s*AS\s+bit\s*\)\s*=\s*N?'?1'?", ReOpts)
                || Regex.IsMatch(where, @"source_database_id\s+is\s+not\s+null", ReOpts);

            // The System Databases folder filters the is_distributor/system bit to 1:
            // "...else dtb.is_distributor end AS bit)=N'1'".
            var systemFilter = Regex.Match(where, @"is_distributor\s+end\s+AS\s+bit\s*\)\s*=\s*N?'?([01])'?", ReOpts);

            if (nameMatch.Success)
            {
                var wanted = nameMatch.Groups[1].Value;
                var sysIdx = Array.FindIndex(SystemDatabaseNames, n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
                if (sysIdx >= 0)
                {
                    rows.Add(new DatabaseRow(SystemDatabaseNames[sysIdx], sysIdx + 1, CatalogEpoch, true, false));
                }
                else
                {
                    foreach (var db in databaseSource())
                    {
                        if (string.Equals(db.Name, wanted, StringComparison.OrdinalIgnoreCase))
                        {
                            rows.Add(new DatabaseRow(db.Name, db.Id, db.CreateDate, false, false));
                        }
                    }
                }
            }
            else if (wantsSnapshots)
            {
                // No snapshots — return the empty, correctly-shaped result.
            }
            else if (systemFilter.Success && systemFilter.Groups[1].Value == "1")
            {
                for (var i = 0; i < SystemDatabaseNames.Length; i++)
                {
                    rows.Add(new DatabaseRow(SystemDatabaseNames[i], i + 1, CatalogEpoch, true, false));
                }
            }
            else
            {
                foreach (var db in databaseSource())
                {
                    rows.Add(new DatabaseRow(db.Name, db.Id, db.CreateDate, false, false));
                }
            }

            return rows;
        }

        private readonly record struct DatabaseRow(string Name, int Id, DateTime CreateDate, bool System, bool IsSnapshot);

        private static readonly DateTime CatalogEpoch = new(2020, 1, 1);

        private static string DeriveDatabaseColumnName(string expr, int index)
        {
            var dotted = Regex.Match(expr, @"([A-Za-z_][A-Za-z0-9_]*)\s*$", ReOpts);
            return dotted.Success ? dotted.Groups[1].Value : "Column" + index;
        }

        /// <summary>
        /// Resolves one Databases-enumeration column to a correctly-typed synthesized value. Per-database
        /// columns (name/id/urn/...) come from <paramref name="db"/>; the rest fall back to a static
        /// default surface, then to type inference from the SELECT expression (so unseen columns still get
        /// a value of the right wire type rather than crashing SMO's by-ordinal reader).
        /// </summary>
        private static object? ResolveDatabaseColumn(string expr, string alias, DatabaseRow db)
        {
            var key = alias.StartsWith("Database_", StringComparison.OrdinalIgnoreCase)
                ? alias.Substring("Database_".Length)
                : alias;

            switch (key.ToLowerInvariant())
            {
                case "name":
                case "databasename":
                case "databasename2":
                case "databasename3":
                case "databasename6":
                    return db.Name;
                case "urn":
                    return $"Server[@Name='{Environment.MachineName}']/Database[@Name='{db.Name.Replace("'", "''")}']";
                case "id":
                    return db.Id;
                case "createdate":
                    return db.CreateDate;
                case "issystemobject":
                    return db.System;
                case "isdatabasesnapshot":
                    return db.IsSnapshot;
            }

            if (DatabaseColumnDefaults.TryGetValue(key, out var value))
            {
                return value;
            }

            return InferDatabaseColumnFromExpr(expr);
        }

        /// <summary>Infers a typed value for a database column SMO requested that has no explicit default.</summary>
        private static object? InferDatabaseColumnFromExpr(string expr)
        {
            var cast = Regex.Match(
                expr,
                @"\bas\s+(bit|tinyint|smallint|int|bigint|datetime2|datetime|date|uniqueidentifier|sysname|nvarchar|varchar|nchar|char)\b",
                ReOpts);
            if (cast.Success)
            {
                return cast.Groups[1].Value.ToLowerInvariant() switch
                {
                    "bit" => false,
                    "tinyint" => (byte)0,
                    "smallint" => (short)0,
                    "int" => 0,
                    "bigint" => 0L,
                    "datetime" or "datetime2" or "date" => new DateTime(1900, 1, 1),
                    "uniqueidentifier" => Guid.Empty,
                    _ => string.Empty,
                };
            }

            // String-returning catalog functions SMO wraps around owner/quoted names.
            if (Regex.IsMatch(expr, @"\b(quotename|suser_sname|db_name)\b", ReOpts))
            {
                return string.Empty;
            }

            // Status is composed via "case dtb.state ..." and/or bitwise OR of state bits.
            if (Regex.IsMatch(expr, @"\bstate\b", ReOpts) || expr.Contains('|'))
            {
                return 1;
            }

            // Bare on-disk boolean flags (dtb.is_...), e.g. dtb.is_auto_shrink_on.
            if (Regex.IsMatch(expr, @"\bdtb\.\[?is_", ReOpts))
            {
                return false;
            }

            var number = expr.Trim();
            if (Regex.IsMatch(number, @"^-?\d+$") && int.TryParse(number, out var n))
            {
                return n;
            }

            return TypedNull.NVarChar;
        }

        /// <summary>
        /// The static Database-property surface (everything that is not per-database), keyed by SMO alias
        /// (the <c>Database_</c> prefix stripped). Covers both the Object Explorer folder queries and the
        /// full SFC-enumerated property set. Values must be the CLR type matching each column's wire type.
        /// </summary>
        private static readonly Dictionary<string, object?> DatabaseColumnDefaults = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AnsiNullDefault"] = false,
            ["AnsiNullsEnabled"] = false,
            ["AnsiPaddingEnabled"] = false,
            ["AnsiWarningsEnabled"] = false,
            ["ArithmeticAbortEnabled"] = false,
            ["AutoShrink"] = false,
            ["CloseCursorsOnCommitEnabled"] = false,
            ["ConcatenateNullYieldsNull"] = false,
            ["NumericRoundAbortEnabled"] = false,
            ["QuotedIdentifiersEnabled"] = false,
            ["ReadOnly"] = false,
            ["RecursiveTriggersEnabled"] = false,
            ["UserAccess"] = (byte)0,
            ["Status"] = 1,
            ["CompatibilityLevel"] = (byte)160,
            ["Collation"] = "SQL_Latin1_General_CP1_CI_AS",
            ["CollationName"] = "SQL_Latin1_General_CP1_CI_AS",
            ["DatabaseOwnershipChaining"] = false,
            ["AutoUpdateStatisticsAsync"] = false,
            ["DateCorrelationOptimization"] = false,
            ["Trustworthy"] = false,
            ["AutoCreateStatisticsEnabled"] = true,
            ["AutoUpdateStatisticsEnabled"] = true,
            ["IsParameterizationForced"] = false,
            ["IsReadCommittedSnapshotOn"] = false,
            ["DatabaseSnapshotBaseName"] = string.Empty,
            ["IsFullTextEnabled"] = false,
            ["ServiceBrokerGuid"] = Guid.Empty,
            ["SnapshotIsolationState"] = (byte)0,
            ["ReplicationOptions"] = 0,
            ["LocalCursorsDefault"] = false,
            ["PageVerify"] = (byte)2,
            ["RecoveryModel"] = (byte)3,
            ["AutoClose"] = false,
            ["BrokerEnabled"] = false,
            ["Owner"] = "sa",
            ["LogReuseWaitStatus"] = 0,
            ["RecoveryForkGuid"] = Guid.Empty,
            ["DatabaseGuid"] = Guid.Empty,
            ["HasFullBackup"] = false,
            ["ChangeTrackingEnabled"] = false,
            ["ChangeTrackingAutoCleanUp"] = false,
            ["ChangeTrackingRetentionPeriod"] = 0,
            ["ChangeTrackingRetentionPeriodUnits"] = (byte)0,
            ["ContainmentType"] = (byte)0,
            ["Containment"] = (byte)0,
            ["DefaultLanguageLcid"] = 1033,
            ["DefaultLanguageName"] = "us_english",
            ["DefaultFullTextLanguageLcid"] = 1033,
            ["DefaultFullTextLanguageName"] = "English",
            ["NestedTriggersEnabled"] = true,
            ["TransformNoiseWords"] = false,
            ["TwoDigitYearCutoff"] = (short)2049,
            ["TargetRecoveryTime"] = 60,
            ["DelayedDurability"] = 0,
            ["AutoCreateIncrementalStatisticsEnabled"] = false,
            ["IsLedger"] = false,
            ["IsUpdateable"] = true,
            ["EncryptionEnabled"] = false,
            ["HonorBrokerPriority"] = false,
            ["IsSqlDw"] = false,
            ["IsAccessible"] = true,
            ["IsFabricDatabase"] = false,
            ["FilestreamDirectoryName"] = string.Empty,
            ["FilestreamNonTransactedAccess"] = 0,
            ["RemoteDataArchiveEnabled"] = false,
            ["MaxDop"] = 0,
            ["MaxDopForSecondary"] = TypedNull.Int,
            ["LegacyCardinalityEstimation"] = 0,
            ["LegacyCardinalityEstimationForSecondary"] = 2,
            ["ParameterSniffing"] = 0,
            ["ParameterSniffingForSecondary"] = 2,
            ["QueryOptimizerHotfixes"] = 0,
            ["QueryOptimizerHotfixesForSecondary"] = 2,
            ["CatalogCollation"] = 0,
            ["LastGoodCheckDbTime"] = new DateTime(1900, 1, 1),
            ["AcceleratedRecoveryEnabled"] = false,
            ["DataRetentionEnabled"] = false,
            ["ContainmentType2"] = (byte)0,
        };

        /// <summary>
        /// Synthesizes the server-level sp_configure rows SMO reads from <c>sys.configurations</c>. SMO
        /// sends several variants (a full enumeration, or a single <c>value_in_use</c> probe for one
        /// configuration_id), read by ordinal, so the result echoes exactly the SELECT list and honours
        /// the WHERE configuration_id filter.
        /// </summary>
        /// <summary>
        /// Synthesizes the single row SSMS's DMF PolicyStore probe expects (Policy-Based Management is
        /// disabled). The real query reads <c>msdb.dbo.syspolicy_configuration</c> and uses
        /// <c>quotename()</c>, neither of which the engine implements; left to the engine it fails and the
        /// Object Explorer server node enters an error state before databases are enumerated.
        /// </summary>
        private static QueryResult BuildPolicyStoreRow()
        {
            var machine = Environment.MachineName;
            return Row(
                ("Urn", $"PolicyStore[@Name='{machine}']"),
                ("Name", machine),
                ("Enabled", false),
                ("HistoryRetentionInDays", 0),
                ("LogOnSuccess", false));
        }

        private static QueryResult BuildConfigurationsResult(string text)
        {
            var columns = ExtractSelectColumns(text, ConfigurationsFromRe);
            if (columns.Count == 0)
            {
                // Fallback to the canonical shape when the SELECT list can't be parsed.
                columns = new[]
                {
                    ((string)"cfg.value", (string?)"value"),
                    ("cfg.name", "name"),
                    ("cfg.configuration_id", "configuration_id"),
                };
            }

            var requested = ExtractConfigurationIds(text);
            var ids = requested.Count > 0
                ? requested
                : new List<int> { 115, 124, 1126, 1127, 1555 };

            var result = new QueryResult { Success = true };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var emitted = new List<(string Expr, string Alias, string Key)>();
            var index = 0;
            foreach (var (expr, alias) in columns)
            {
                var original = alias ?? DeriveDatabaseColumnName(expr, index);
                var key = original;
                var suffix = 1;
                while (!seen.Add(key))
                {
                    key = original + "_" + suffix++;
                }

                emitted.Add((expr, original, key));
                result.Columns.Add(key);
                index++;
            }

            foreach (var id in ids)
            {
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (expr, alias, key) in emitted)
                {
                    row[key] = ResolveConfigurationColumn(alias, id);
                }

                result.Rows.Add(row);
            }

            result.RowCount = result.Rows.Count;
            return result;
        }

        private static readonly Regex ConfigurationsFromRe =
            new(@"from\s+sys\.configurations\b", ReOpts);

        private static readonly Dictionary<int, (int Value, string? Name)> ConfigurationValues = new()
        {
            [115] = (1, null),           // nested triggers (enabled)
            [124] = (1033, "us_english"), // default language
            [1126] = (1033, "English"),  // default full-text language
            [1127] = (2049, null),       // two digit year cutoff
            [1555] = (0, null),          // default language alt slot
        };

        private static object? ResolveConfigurationColumn(string alias, int id)
        {
            ConfigurationValues.TryGetValue(id, out var cfg);
            switch (alias.ToLowerInvariant())
            {
                case "value":
                case "value_in_use":
                    return cfg.Value;
                case "name":
                    return cfg.Name ?? (object)TypedNull.NVarChar;
                case "configuration_id":
                    return id;
                case "minimum":
                    return 0;
                case "maximum":
                    return int.MaxValue;
                case "is_dynamic":
                case "is_advanced":
                    return false;
                case "description":
                    return string.Empty;
                default:
                    return cfg.Value;
            }
        }

        /// <summary>Extracts the configuration_id values a sys.configurations query filters on (IN list or =).</summary>
        private static List<int> ExtractConfigurationIds(string text)
        {
            var ids = new List<int>();
            var inList = Regex.Match(text, @"configuration_id\s+in\s*\(([^)]*)\)", ReOpts);
            if (inList.Success)
            {
                foreach (Match m in Regex.Matches(inList.Groups[1].Value, @"-?\d+"))
                {
                    if (int.TryParse(m.Value, out var v))
                    {
                        ids.Add(v);
                    }
                }
            }
            else
            {
                var eq = Regex.Match(text, @"configuration_id\s*=\s*N?'?(-?\d+)'?", ReOpts);
                if (eq.Success && int.TryParse(eq.Groups[1].Value, out var v))
                {
                    ids.Add(v);
                }
            }

            return ids;
        }

        /// <summary>
        /// Extracts a query's depth-0 SELECT column list (as expression/alias pairs) when it projects from
        /// the table matched by <paramref name="fromRe"/>. Shared by the sys.databases and sys.configurations
        /// handlers, both of which must echo the client's exact column shape for its by-ordinal reader.
        /// </summary>
        private static IReadOnlyList<(string Expr, string? Alias)> ExtractSelectColumns(string text, Regex fromRe)
        {
            var from = LastMatch(fromRe, text);
            if (!from.Success)
            {
                return Array.Empty<(string, string?)>();
            }

            var listStart = LastTopLevelSelectEnd(text, from.Index);
            if (listStart < 0 || listStart >= from.Index)
            {
                return Array.Empty<(string, string?)>();
            }

            var columnList = text.Substring(listStart, from.Index - listStart);
            columnList = Regex.Replace(columnList.TrimStart(), @"^(all|distinct)\s+", string.Empty, ReOpts);
            columnList = Regex.Replace(columnList, @"^top\s*\(\s*\d+\s*\)\s+", string.Empty, ReOpts);
            columnList = Regex.Replace(columnList, @"^top\s+\d+\s+", string.Empty, ReOpts);

            var columns = new List<(string, string?)>();
            foreach (var item in SplitTopLevel(columnList))
            {
                var trimmed = item.Trim();
                if (trimmed.Length > 0)
                {
                    columns.Add(SplitAlias(trimmed));
                }
            }

            return columns;
        }


        /// <summary>
        /// Builds the single-row result SMO expects from its Server-properties probe. Values are
        /// plausible constants for a modern on-premises instance; paths point at the TidalDb data root.
        /// Only the column names and types must match what SMO's Server object reads.
        /// </summary>
        private static QueryResult BuildServerSettingsRow()
        {
            var machine = Environment.MachineName;
            const string collation = "SQL_Latin1_General_CP1_CI_AS";
            const string root = @"C:\ProgramData\TidalDb";
            var dataPath = root + @"\data";
            var logPath = root + @"\log";

            return Row(
                ("AuditLevel", 0),
                ("NumberOfLogFiles", -1),
                ("LoginMode", 2),
                ("MailProfile", string.Empty),
                ("BackupDirectory", root + @"\backup"),
                ("PerfMonMode", 0),
                ("InstallDataDirectory", dataPath),
                ("ServiceName", "MSSQLSERVER"),
                ("ErrorLogPath", logPath),
                ("RootDirectory", root),
                ("IsCaseSensitive", false),
                ("MaxPrecision", (byte)38),
                ("IsFullTextInstalled", false),
                ("VersionString", ProductVersion),
                ("Edition", "Developer Edition (64-bit)"),
                ("ProductLevel", "RTM"),
                ("ProductUpdateLevel", string.Empty),
                ("IsSingleUser", false),
                ("EngineEdition", EngineEdition),
                ("Collation", collation),
                ("IsClustered", false),
                ("NetName", machine),
                ("ComputerNamePhysicalNetBIOS", machine),
                ("ServiceStartMode", 2),
                ("MasterDBLogPath", logPath),
                ("MasterDBPath", dataPath),
                ("DefaultFile", dataPath),
                ("DefaultLog", logPath),
                ("ResourceVersionString", ProductVersion),
                ("ResourceLastUpdateDateTime", new DateTime(2022, 1, 1)),
                ("CollationID", 872468488),
                ("ComparisonStyle", 196609),
                ("SqlCharSet", (byte)1),
                ("SqlCharSetName", "iso_1"),
                ("SqlSortOrder", (byte)52),
                ("SqlSortOrderName", "nocase_iso"),
                ("BuildClrVersionString", "v4.0.30319"),
                ("ServiceAccount", string.Empty),
                ("NamedPipesEnabled", false),
                ("TcpEnabled", true),
                ("InstallSharedDirectory", dataPath),
                ("SqlDomainGroup", string.Empty),
                ("PolicyHealthState", 0),
                ("FilestreamLevel", 0),
                ("FilestreamShareName", string.Empty),
                ("TapeLoadWaitTime", -1),
                ("IsHadrEnabled", false),
                ("HadrManagerStatus", TypedNull.Int),
                ("ClusterName", string.Empty),
                ("ClusterQuorumType", 4),
                ("ClusterQuorumState", 3),
                ("ServiceAccountSid", TypedNull.NVarChar),
                ("IsPolyBaseInstalled", false),
                ("Name", machine),
                ("InstanceName", string.Empty),
                ("Status", 1),
                ("PathSeparator", @"\"),
                ("IsContainedAuthentication", 0),
                ("ServerType", TypedNull.Int));
        }

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
