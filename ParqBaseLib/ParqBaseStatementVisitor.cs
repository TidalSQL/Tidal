namespace ParqBaseLib
{
    using Microsoft.SqlServer.TransactSql.ScriptDom;
    using Parquet;
    using Parquet.Data;
    using Parquet.Schema;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;

    internal partial class ParqBaseStatementVisitor : TSqlFragmentVisitor
    {
        private readonly TSql160Parser sqlParser = new TSql160Parser(true, SqlEngineType.All);
        private readonly SessionContext session;

        public QueryResult LastResult { get; private set; } = new();

        public ParqBaseStatementVisitor(SessionContext session)
        {
            this.session = session;
        }

        public QueryResult Run(string statement)
        {
            this.LastResult = new QueryResult();

            var fragment = this.sqlParser.Parse(new StringReader(statement), out IList<ParseError> parseErrors);
            if (parseErrors != null && parseErrors.Count > 0)
            {
                this.LastResult.Success = false;
                this.LastResult.Message = "Parse error: " + string.Join("; ",
                    parseErrors.Select(e => $"Line {e.Line}, Col {e.Column}: {e.Message}"));
                return this.LastResult;
            }

            fragment.Accept(this);
            return this.LastResult;
        }

        public override void Visit(CreateDatabaseStatement node)
        {
            try
            {
                var root = DatabasesRoot();
                Directory.CreateDirectory(root);

                var databaseDirectory = Path.Combine(root, node.DatabaseName.Value);
                Directory.CreateDirectory(databaseDirectory);

                foreach (var sub in new[] { "tables", "views", "procedures", "functions", "security", "users" })
                {
                    Directory.CreateDirectory(Path.Combine(databaseDirectory, sub));
                }

                this.session.CurrentDatabasePath = databaseDirectory;
                this.LastResult.Message = $"Database '{node.DatabaseName.Value}' created.";
            }
            catch (Exception ex)
            {
                this.Fail(ex);
            }
        }

        public override void Visit(UseStatement node)
        {
            try
            {
                var databaseDirectory = Path.Combine(DatabasesRoot(), node.DatabaseName.Value);
                if (!Directory.Exists(databaseDirectory))
                {
                    throw new Exception($"Database {node.DatabaseName.Value} does not exist");
                }

                this.session.CurrentDatabasePath = databaseDirectory;
                this.LastResult.Message = $"Changed database context to '{node.DatabaseName.Value}'.";
            }
            catch (Exception ex)
            {
                this.Fail(ex);
            }
        }

        public override void Visit(CreateTableStatement node)
        {
            try
            {
                var tablesDir = this.TablesDirectory();
                Directory.CreateDirectory(tablesDir);

                var columns = node.Definition.ColumnDefinitions.ToList();
                this.CreateTable(node.SchemaObjectName.BaseIdentifier.Value, columns, tablesDir);
                this.LastResult.Message = $"Table '{node.SchemaObjectName.BaseIdentifier.Value}' created.";
            }
            catch (Exception ex)
            {
                this.Fail(ex);
            }
        }

        public override void Visit(InsertStatement node)
        {
            try
            {
                var tablesDir = this.TablesDirectory();

                var columnNames = node.InsertSpecification.Columns
                    .Select(c => c.MultiPartIdentifier.Identifiers[0].Value)
                    .ToList();

                if (node.InsertSpecification.InsertSource is not ValuesInsertSource valuesSource)
                {
                    throw new NotSupportedException("Only INSERT ... VALUES is supported.");
                }

                var rows = valuesSource.RowValues
                    .Select(rv => rv.ColumnValues.ToList())
                    .ToList();

                if (node.InsertSpecification.Target is not NamedTableReference tableReference)
                {
                    throw new NotSupportedException("INSERT target must be a table.");
                }

                var tableName = tableReference.SchemaObject.BaseIdentifier.Value;
                var filePath = Path.Combine(tablesDir, $"{tableName}.parquet");

                this.InsertRows(filePath, tableName, columnNames, rows);
                this.LastResult.Message = $"{rows.Count} row(s) inserted into [{tableName}].";
            }
            catch (Exception ex)
            {
                this.Fail(ex);
            }
        }

        public override void Visit(SelectStatement node)
        {
            try
            {
                if (node.QueryExpression is not QuerySpecification querySpec)
                {
                    throw new NotSupportedException("Only SELECT queries are supported.");
                }

                var rowSet = this.EvaluateQuery(querySpec, null);
                var columns = BuildDisplayNames(rowSet.Schema);

                var rows = new List<Dictionary<string, object?>>();
                foreach (var values in rowSet.Rows)
                {
                    var row = new Dictionary<string, object?>();
                    for (var i = 0; i < columns.Count; i++)
                    {
                        row[columns[i]] = i < values.Length ? values[i] : null;
                    }

                    rows.Add(row);
                }

                this.LastResult.Columns = columns;
                this.LastResult.Rows = rows;
                this.LastResult.RowCount = rows.Count;
                this.LastResult.Message = $"({rows.Count} row(s) returned)";
            }
            catch (Exception ex)
            {
                this.Fail(ex);
            }
        }

        /// <summary>
        /// Evaluates a query specification (top-level SELECT, derived table, or subquery) into a
        /// projected <see cref="RowSet"/>. <paramref name="outer"/> supplies correlated columns.
        /// </summary>
        private RowSet EvaluateQuery(QuerySpecification querySpec, Env? outer)
        {
            if (querySpec.GroupByClause != null || querySpec.HavingClause != null)
            {
                throw new NotSupportedException("GROUP BY / HAVING are not supported.");
            }

            if (querySpec.FromClause == null || querySpec.FromClause.TableReferences.Count == 0)
            {
                throw new NotSupportedException("SELECT requires a FROM clause.");
            }

            var source = this.EvaluateTableReference(querySpec.FromClause.TableReferences[0], outer);
            for (var i = 1; i < querySpec.FromClause.TableReferences.Count; i++)
            {
                source = this.CrossJoin(source, this.EvaluateTableReference(querySpec.FromClause.TableReferences[i], outer));
            }

            // WHERE
            var filtered = new List<object?[]>();
            foreach (var row in source.Rows)
            {
                if (querySpec.WhereClause == null ||
                    this.EvaluateBoolean(querySpec.WhereClause.SearchCondition, new Env(source.Schema, row, outer)))
                {
                    filtered.Add(row);
                }
            }

            // ORDER BY (resolved against the source rows, before projection)
            if (querySpec.OrderByClause != null)
            {
                var elems = querySpec.OrderByClause.OrderByElements;
                filtered.Sort((a, b) =>
                {
                    foreach (var oe in elems)
                    {
                        var av = this.GetScalarValue(oe.Expression, new Env(source.Schema, a, outer));
                        var bv = this.GetScalarValue(oe.Expression, new Env(source.Schema, b, outer));
                        var cmp = CompareForOrder(av, bv);
                        if (oe.SortOrder == SortOrder.Descending)
                        {
                            cmp = -cmp;
                        }

                        if (cmp != 0)
                        {
                            return cmp;
                        }
                    }

                    return 0;
                });
            }

            // Projection
            var projSchema = new List<ColumnRef>();
            var projectors = new List<Func<object?[], object?>>();
            foreach (var element in querySpec.SelectElements)
            {
                if (element is SelectStarExpression star)
                {
                    var starQualifier = star.Qualifier?.Identifiers.LastOrDefault()?.Value;
                    for (var i = 0; i < source.Schema.Count; i++)
                    {
                        var column = source.Schema[i];
                        if (starQualifier != null &&
                            !string.Equals(column.Qualifier, starQualifier, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var index = i;
                        projSchema.Add(new ColumnRef(column.Qualifier, column.Name));
                        projectors.Add(values => values[index]);
                    }
                }
                else if (element is SelectScalarExpression scalar)
                {
                    var alias = scalar.ColumnName?.Value;
                    string qualifier = string.Empty;
                    string name;
                    if (scalar.Expression is ColumnReferenceExpression colRef)
                    {
                        var ids = colRef.MultiPartIdentifier.Identifiers;
                        name = alias ?? ids.Last().Value;
                        qualifier = ids.Count > 1 ? ids[ids.Count - 2].Value : string.Empty;
                    }
                    else
                    {
                        name = alias ?? "column";
                    }

                    var expr = scalar.Expression;
                    projSchema.Add(new ColumnRef(qualifier, name));
                    projectors.Add(values => this.GetScalarValue(expr, new Env(source.Schema, values, outer)));
                }
                else
                {
                    throw new NotSupportedException($"Unsupported select element: {element.GetType().Name}");
                }
            }

            var projRows = new List<object?[]>();
            foreach (var row in filtered)
            {
                var projected = new object?[projectors.Count];
                for (var i = 0; i < projectors.Count; i++)
                {
                    projected[i] = projectors[i](row);
                }

                projRows.Add(projected);
            }

            if (querySpec.UniqueRowFilter == UniqueRowFilter.Distinct)
            {
                projRows = DistinctRows(projRows);
            }

            if (querySpec.TopRowFilter?.Expression is IntegerLiteral topLiteral)
            {
                var n = int.Parse(topLiteral.Value, CultureInfo.InvariantCulture);
                if (projRows.Count > n)
                {
                    projRows = projRows.Take(n).ToList();
                }
            }

            return new RowSet(projSchema, projRows);
        }

        private RowSet EvaluateTableReference(TableReference tableReference, Env? outer)
        {
            switch (tableReference)
            {
                case NamedTableReference named:
                {
                    var qualifier = named.Alias?.Value ?? named.SchemaObject.BaseIdentifier.Value;

                    if (IsSystemCatalog(named, "databases"))
                    {
                        return this.BuildDatabasesRowSet(qualifier);
                    }

                    if (IsSystemCatalog(named, "objects"))
                    {
                        return this.BuildObjectsRowSet(qualifier);
                    }

                    if (IsSystemCatalog(named, "tables"))
                    {
                        return this.BuildTablesRowSet(qualifier);
                    }

                    var tableName = named.SchemaObject.BaseIdentifier.Value;
                    var filePath = Path.Combine(this.TablesDirectory(), $"{tableName}.parquet");
                    if (!File.Exists(filePath))
                    {
                        throw new Exception($"Table [{tableName}] does not exist.");
                    }

                    var table = this.LoadTableAsync(filePath).GetAwaiter().GetResult();
                    var schema = table.Order.Select(c => new ColumnRef(qualifier, c)).ToList();
                    var rows = new List<object?[]>();
                    for (var r = 0; r < table.RowCount; r++)
                    {
                        rows.Add(table.Order.Select(c => table.Data[c][r]).ToArray());
                    }

                    return new RowSet(schema, rows);
                }

                case QueryDerivedTable derived:
                {
                    if (derived.QueryExpression is not QuerySpecification innerSpec)
                    {
                        throw new NotSupportedException("Only simple subqueries are supported as derived tables.");
                    }

                    var alias = derived.Alias?.Value
                        ?? throw new NotSupportedException("A derived table (subquery in FROM) requires an alias.");

                    var inner = this.EvaluateQuery(innerSpec, outer);
                    var schema = inner.Schema.Select(c => new ColumnRef(alias, c.Name)).ToList();
                    return new RowSet(schema, inner.Rows);
                }

                case QualifiedJoin qualifiedJoin:
                {
                    var left = this.EvaluateTableReference(qualifiedJoin.FirstTableReference, outer);
                    var right = this.EvaluateTableReference(qualifiedJoin.SecondTableReference, outer);
                    return this.JoinRowSets(left, right, qualifiedJoin.SearchCondition, qualifiedJoin.QualifiedJoinType, outer);
                }

                case UnqualifiedJoin unqualifiedJoin:
                {
                    var left = this.EvaluateTableReference(unqualifiedJoin.FirstTableReference, outer);
                    var right = this.EvaluateTableReference(unqualifiedJoin.SecondTableReference, outer);
                    if (unqualifiedJoin.UnqualifiedJoinType == UnqualifiedJoinType.CrossJoin)
                    {
                        return this.CrossJoin(left, right);
                    }

                    throw new NotSupportedException($"Unsupported join: {unqualifiedJoin.UnqualifiedJoinType}");
                }

                case JoinParenthesisTableReference parenthesis:
                    return this.EvaluateTableReference(parenthesis.Join, outer);

                default:
                    throw new NotSupportedException($"Unsupported table reference: {tableReference.GetType().Name}");
            }
        }

        private RowSet JoinRowSets(RowSet left, RowSet right, BooleanExpression? on, QualifiedJoinType joinType, Env? outer)
        {
            var schema = left.Schema.Concat(right.Schema).ToList();
            var leftWidth = left.Schema.Count;
            var rightWidth = right.Schema.Count;
            var rows = new List<object?[]>();
            var rightMatched = new bool[right.Rows.Count];

            foreach (var leftRow in left.Rows)
            {
                var matched = false;
                for (var j = 0; j < right.Rows.Count; j++)
                {
                    var combined = Concat(leftRow, right.Rows[j]);
                    if (on == null || this.EvaluateBoolean(on, new Env(schema, combined, outer)))
                    {
                        rows.Add(combined);
                        matched = true;
                        rightMatched[j] = true;
                    }
                }

                if (!matched && (joinType == QualifiedJoinType.LeftOuter || joinType == QualifiedJoinType.FullOuter))
                {
                    rows.Add(Concat(leftRow, new object?[rightWidth]));
                }
            }

            if (joinType == QualifiedJoinType.RightOuter || joinType == QualifiedJoinType.FullOuter)
            {
                for (var j = 0; j < right.Rows.Count; j++)
                {
                    if (!rightMatched[j])
                    {
                        rows.Add(Concat(new object?[leftWidth], right.Rows[j]));
                    }
                }
            }

            return new RowSet(schema, rows);
        }

        private RowSet CrossJoin(RowSet left, RowSet right)
            => this.JoinRowSets(left, right, null, QualifiedJoinType.Inner, null);

        private RowSet BuildDatabasesRowSet(string qualifier)
        {
            var root = DatabasesRoot();
            var names = Directory.Exists(root)
                ? Directory.GetDirectories(root)
                    .Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();

            var schema = new List<ColumnRef> { new(qualifier, "name") };
            var rows = names.Select(n => new object?[] { n }).ToList();
            return new RowSet(schema, rows);
        }

        private RowSet BuildObjectsRowSet(string qualifier)
        {
            var dbPath = this.RequireDatabasePath();
            var objects = new List<(string Name, string Type)>();
            CollectObjects(dbPath, "tables", "*.parquet", "table", objects);
            CollectObjects(dbPath, "views", "*.*", "view", objects);
            CollectObjects(dbPath, "procedures", "*.*", "procedure", objects);
            CollectObjects(dbPath, "functions", "*.*", "function", objects);

            var schema = new List<ColumnRef> { new(qualifier, "name"), new(qualifier, "type") };
            var rows = objects
                .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .Select(o => new object?[] { o.Name, o.Type })
                .ToList();
            return new RowSet(schema, rows);
        }

        private RowSet BuildTablesRowSet(string qualifier)
        {
            var dbPath = this.RequireDatabasePath();
            var tables = new List<(string Name, string Type)>();
            CollectObjects(dbPath, "tables", "*.parquet", "table", tables);

            var schema = new List<ColumnRef> { new(qualifier, "name") };
            var rows = tables
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(t => new object?[] { t.Name })
                .ToList();
            return new RowSet(schema, rows);
        }

        private bool EvaluateBoolean(BooleanExpression expr, Env env)
        {
            switch (expr)
            {
                case BooleanParenthesisExpression p:
                    return this.EvaluateBoolean(p.Expression, env);

                case BooleanBinaryExpression b:
                    var left = this.EvaluateBoolean(b.FirstExpression, env);
                    return b.BinaryExpressionType == BooleanBinaryExpressionType.And
                        ? left && this.EvaluateBoolean(b.SecondExpression, env)
                        : left || this.EvaluateBoolean(b.SecondExpression, env);

                case BooleanIsNullExpression isNull:
                {
                    var value = this.GetScalarValue(isNull.Expression, env);
                    return isNull.IsNot ? value != null : value == null;
                }

                case BooleanComparisonExpression cmp:
                {
                    var a = this.GetScalarValue(cmp.FirstExpression, env);
                    var b = this.GetScalarValue(cmp.SecondExpression, env);
                    if (a == null || b == null)
                    {
                        return false;
                    }

                    return ApplyOperator(CompareTyped(a, b), cmp.ComparisonType);
                }

                case InPredicate inPredicate:
                {
                    var value = this.GetScalarValue(inPredicate.Expression, env);
                    IEnumerable<object?> candidates = inPredicate.Subquery != null
                        ? this.EvaluateSubqueryColumn(inPredicate.Subquery, env)
                        : inPredicate.Values.Select(v => this.GetScalarValue(v, env));

                    var found = value != null &&
                        candidates.Any(c => c != null && CompareTyped(value, c) == 0);
                    return inPredicate.NotDefined ? !found : found;
                }

                case ExistsPredicate exists:
                {
                    if (exists.Subquery.QueryExpression is not QuerySpecification subSpec)
                    {
                        throw new NotSupportedException("Only simple subqueries are supported in EXISTS.");
                    }

                    return this.EvaluateQuery(subSpec, env).Rows.Count > 0;
                }

                default:
                    throw new NotSupportedException($"Unsupported WHERE expression: {expr.GetType().Name}");
            }
        }

        private List<object?> EvaluateSubqueryColumn(ScalarSubquery subquery, Env outer)
        {
            if (subquery.QueryExpression is not QuerySpecification subSpec)
            {
                throw new NotSupportedException("Only simple subqueries are supported.");
            }

            var rowSet = this.EvaluateQuery(subSpec, outer);
            if (rowSet.Schema.Count != 1)
            {
                throw new NotSupportedException("A subquery used here must return exactly one column.");
            }

            return rowSet.Rows.Select(r => r[0]).ToList();
        }

        private object? GetScalarValue(ScalarExpression expression, Env env)
        {
            switch (expression)
            {
                case ColumnReferenceExpression colRef:
                {
                    var ids = colRef.MultiPartIdentifier.Identifiers;
                    var name = ids.Last().Value;
                    var qualifier = ids.Count > 1 ? ids[ids.Count - 2].Value : null;
                    if (env.TryResolve(qualifier, name, out var value))
                    {
                        return value;
                    }

                    var display = string.Join(".", ids.Select(i => i.Value));
                    throw new ArgumentException($"Column [{display}] does not exist.");
                }

                case IntegerLiteral intLiteral:
                    return int.Parse(intLiteral.Value, CultureInfo.InvariantCulture);

                case NumericLiteral numLiteral:
                    return decimal.Parse(numLiteral.Value, CultureInfo.InvariantCulture);

                case MoneyLiteral moneyLiteral:
                    return decimal.Parse(moneyLiteral.Value.TrimStart('$'), CultureInfo.InvariantCulture);

                case StringLiteral strLiteral:
                    return strLiteral.Value;

                case NullLiteral:
                    return null;

                case ParenthesisExpression paren:
                    return this.GetScalarValue(paren.Expression, env);

                case UnaryExpression unary:
                {
                    var inner = this.GetScalarValue(unary.Expression, env);
                    if (unary.UnaryExpressionType == UnaryExpressionType.Positive || inner == null)
                    {
                        return inner;
                    }

                    if (unary.UnaryExpressionType == UnaryExpressionType.Negative)
                    {
                        return inner switch
                        {
                            int i => (object)(-i),
                            decimal d => (object)(-d),
                            _ => throw new ArgumentException("Cannot negate a non-numeric value.")
                        };
                    }

                    throw new NotSupportedException($"Unsupported unary operator: {unary.UnaryExpressionType}");
                }

                case ScalarSubquery subquery:
                {
                    var values = this.EvaluateSubqueryColumn(subquery, env);
                    if (values.Count == 0)
                    {
                        return null;
                    }

                    if (values.Count > 1)
                    {
                        throw new NotSupportedException("A scalar subquery returned more than one row.");
                    }

                    return values[0];
                }

                default:
                    throw new NotSupportedException($"Unsupported expression: {expression.GetType().Name}");
            }
        }

        private static bool ApplyOperator(int cmp, BooleanComparisonType op) => op switch
        {
            BooleanComparisonType.Equals => cmp == 0,
            BooleanComparisonType.NotEqualToBrackets => cmp != 0,
            BooleanComparisonType.NotEqualToExclamation => cmp != 0,
            BooleanComparisonType.GreaterThan => cmp > 0,
            BooleanComparisonType.LessThan => cmp < 0,
            BooleanComparisonType.GreaterThanOrEqualTo => cmp >= 0,
            BooleanComparisonType.LessThanOrEqualTo => cmp <= 0,
            _ => throw new NotSupportedException($"Unsupported comparison operator: {op}")
        };

        private static bool IsNumeric(object value) => value is int || value is decimal;

        private static int CompareTyped(object a, object b)
        {
            if (a is int ai && b is int bi)
            {
                return ai.CompareTo(bi);
            }

            if (IsNumeric(a) && IsNumeric(b))
            {
                return Convert.ToDecimal(a, CultureInfo.InvariantCulture)
                    .CompareTo(Convert.ToDecimal(b, CultureInfo.InvariantCulture));
            }

            if (a is DateTime ad)
            {
                if (b is DateTime bd)
                {
                    return ad.CompareTo(bd);
                }

                if (b is string bs)
                {
                    return ad.CompareTo(DateTime.Parse(bs, CultureInfo.InvariantCulture));
                }
            }

            if (a is string sa && b is DateTime bd2)
            {
                return DateTime.Parse(sa, CultureInfo.InvariantCulture).CompareTo(bd2);
            }

            if (a is string s1 && b is string s2)
            {
                return string.Compare(s1, s2, StringComparison.Ordinal);
            }

            return string.Compare(a.ToString(), b.ToString(), StringComparison.Ordinal);
        }

        private static int CompareForOrder(object? a, object? b)
        {
            if (a == null && b == null)
            {
                return 0;
            }

            if (a == null)
            {
                return -1;
            }

            if (b == null)
            {
                return 1;
            }

            return CompareTyped(a, b);
        }

        private static List<object?[]> DistinctRows(List<object?[]> rows)
        {
            var seen = new HashSet<string>();
            var result = new List<object?[]>();
            foreach (var row in rows)
            {
                var key = string.Join("\u0001", row.Select(v => v?.ToString() ?? "\0NULL"));
                if (seen.Add(key))
                {
                    result.Add(row);
                }
            }

            return result;
        }

        private static List<string> BuildDisplayNames(List<ColumnRef> schema)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in schema)
            {
                counts[column.Name] = counts.TryGetValue(column.Name, out var c) ? c + 1 : 1;
            }

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            foreach (var column in schema)
            {
                var baseName = counts[column.Name] > 1 && !string.IsNullOrEmpty(column.Qualifier)
                    ? $"{column.Qualifier}.{column.Name}"
                    : column.Name;

                var name = baseName;
                var suffix = 1;
                while (used.Contains(name))
                {
                    name = $"{baseName}_{++suffix}";
                }

                used.Add(name);
                names.Add(name);
            }

            return names;
        }

        private static object?[] Concat(object?[] left, object?[] right)
        {
            var result = new object?[left.Length + right.Length];
            Array.Copy(left, 0, result, 0, left.Length);
            Array.Copy(right, 0, result, left.Length, right.Length);
            return result;
        }

        private void CreateTable(string tableName, List<ColumnDefinition> columns, string tablesDir)
        {
            if (string.IsNullOrEmpty(tableName))
            {
                throw new ArgumentException("Table name cannot be null or empty.", nameof(tableName));
            }

            if (columns == null || columns.Count == 0)
            {
                throw new ArgumentException("Columns must be provided.", nameof(columns));
            }

            var types = new Dictionary<string, string>();
            var order = new List<string>();

            foreach (var column in columns)
            {
                var sqlType = column.DataType.Name.BaseIdentifier.Value.ToUpperInvariant();
                var mapped = sqlType switch
                {
                    "INT" or "INTEGER" or "BIGINT" or "SMALLINT" or "TINYINT" => "int",
                    "VARCHAR" or "NVARCHAR" or "CHAR" or "NCHAR" or "TEXT" or "NTEXT" => "string",
                    "DATE" or "DATETIME" or "DATETIME2" or "SMALLDATETIME" => "datetime",
                    "MONEY" or "SMALLMONEY" or "DECIMAL" or "NUMERIC" or "FLOAT" or "REAL" => "decimal",
                    _ => throw new ArgumentException($"Unsupported data type: {column.DataType.Name.BaseIdentifier.Value}")
                };

                var name = column.ColumnIdentifier.Value;
                types[name] = mapped;
                order.Add(name);
            }

            var filePath = Path.Combine(tablesDir, $"{tableName}.parquet");
            if (File.Exists(filePath))
            {
                throw new Exception($"Table [{tableName}] already exists.");
            }

            var data = order.ToDictionary(c => c, _ => new List<object?>());
            this.WriteTableAsync(filePath, order, types, data).GetAwaiter().GetResult();
        }

        private void InsertRows(string filePath, string tableName, List<string> columnNames, List<List<ScalarExpression>> rows)
        {
            if (!File.Exists(filePath))
            {
                throw new Exception($"Table [{tableName}] does not exist.");
            }

            var table = this.LoadTableAsync(filePath).GetAwaiter().GetResult();
            var insertColumns = columnNames.Count > 0 ? columnNames : table.Order;

            foreach (var col in insertColumns)
            {
                if (!table.Types.ContainsKey(col))
                {
                    throw new ArgumentException($"Column [{col}] does not exist in table [{tableName}].");
                }
            }

            foreach (var rowValues in rows)
            {
                if (rowValues.Count != insertColumns.Count)
                {
                    throw new ArgumentException(
                        $"Column count ({insertColumns.Count}) does not match value count ({rowValues.Count}).");
                }

                var provided = new Dictionary<string, object?>();
                for (var i = 0; i < insertColumns.Count; i++)
                {
                    provided[insertColumns[i]] = ConvertSqlValue(rowValues[i], table.Types[insertColumns[i]]);
                }

                foreach (var col in table.Order)
                {
                    table.Data[col].Add(provided.TryGetValue(col, out var v) ? v : null);
                }
            }

            this.WriteTableAsync(filePath, table.Order, table.Types, table.Data).GetAwaiter().GetResult();
        }

        private async Task<TableData> LoadTableAsync(string filePath)
        {
            var data = new Dictionary<string, List<object?>>();
            var order = new List<string>();
            var types = new Dictionary<string, string>();
            var rowCount = 0;

            using (Stream readStream = File.OpenRead(filePath))
            using (var reader = await ParquetReader.CreateAsync(readStream))
            {
                foreach (var field in reader.Schema.DataFields)
                {
                    data[field.Name] = new List<object?>();
                    order.Add(field.Name);
                    types[field.Name] = MapClrType(field.ClrType);
                }

                for (var g = 0; g < reader.RowGroupCount; g++)
                {
                    using var groupReader = reader.OpenRowGroupReader(g);
                    foreach (var field in reader.Schema.DataFields)
                    {
                        var dc = await groupReader.ReadColumnAsync(field);
                        foreach (var val in dc.Data)
                        {
                            data[field.Name].Add(val);
                        }
                    }
                }

                if (order.Count > 0)
                {
                    rowCount = data[order[0]].Count;
                }
            }

            return new TableData(data, order, types, rowCount);
        }

        private async Task WriteTableAsync(string filePath, List<string> order, Dictionary<string, string> types, Dictionary<string, List<object?>> data)
        {
            var fields = order.Select(c => BuildField(c, types[c])).ToList();
            var schema = new ParquetSchema(fields.Cast<Field>().ToList());

            using Stream writeStream = File.Create(filePath);
            using var writer = await ParquetWriter.CreateAsync(schema, writeStream);
            writer.CompressionMethod = CompressionMethod.Gzip;
            writer.CompressionLevel = System.IO.Compression.CompressionLevel.Optimal;

            if (order.Count == 0 || data[order[0]].Count == 0)
            {
                // Schema-only (empty) table: no row group to write.
                return;
            }

            using var groupWriter = writer.CreateRowGroup();
            foreach (var field in schema.DataFields)
            {
                var col = data[field.Name];
                Array array = types[field.Name] switch
                {
                    "int" => col.Select(v => (int?)v).ToArray(),
                    "string" => col.Select(v => (string?)v).ToArray(),
                    "datetime" => col.Select(v => (DateTime?)v).ToArray(),
                    "decimal" => col.Select(v => (decimal?)v).ToArray(),
                    _ => throw new NotSupportedException($"Unsupported column type: {types[field.Name]}")
                };

                await groupWriter.WriteColumnAsync(new DataColumn(field, array));
            }
        }

        private static object? ConvertSqlValue(ScalarExpression expression, string targetType)
        {
            if (expression is NullLiteral)
            {
                return null;
            }

            if (expression is UnaryExpression unary)
            {
                var inner = ConvertSqlValue(unary.Expression, targetType);
                if (unary.UnaryExpressionType == UnaryExpressionType.Positive)
                {
                    return inner;
                }

                if (unary.UnaryExpressionType == UnaryExpressionType.Negative)
                {
                    return inner switch
                    {
                        int i => (object)(-i),
                        decimal d => (object)(-d),
                        _ => throw new ArgumentException($"Cannot negate value for type {targetType}.")
                    };
                }
            }

            switch (targetType)
            {
                case "int":
                    if (expression is IntegerLiteral intLiteral)
                    {
                        return int.Parse(intLiteral.Value, CultureInfo.InvariantCulture);
                    }

                    break;
                case "string":
                    if (expression is StringLiteral strLiteral)
                    {
                        return strLiteral.Value;
                    }

                    break;
                case "datetime":
                    if (expression is StringLiteral dateLiteral)
                    {
                        return DateTime.Parse(dateLiteral.Value, CultureInfo.InvariantCulture);
                    }

                    break;
                case "decimal":
                    if (expression is IntegerLiteral decIntLiteral)
                    {
                        return decimal.Parse(decIntLiteral.Value, CultureInfo.InvariantCulture);
                    }

                    if (expression is NumericLiteral numLiteral)
                    {
                        return decimal.Parse(numLiteral.Value, CultureInfo.InvariantCulture);
                    }

                    if (expression is MoneyLiteral moneyLiteral)
                    {
                        return decimal.Parse(moneyLiteral.Value.TrimStart('$'), CultureInfo.InvariantCulture);
                    }

                    break;
            }

            throw new ArgumentException($"Cannot convert value to {targetType}.");
        }

        private static DataField BuildField(string name, string type) => type switch
        {
            "int" => new DataField<int?>(name),
            "string" => new DataField<string>(name),
            "datetime" => new DataField<DateTime?>(name),
            "decimal" => new DataField<decimal?>(name),
            _ => throw new NotSupportedException($"Unsupported column type: {type}")
        };

        private static string MapClrType(Type clrType)
        {
            var underlying = Nullable.GetUnderlyingType(clrType) ?? clrType;
            if (underlying == typeof(int))
            {
                return "int";
            }

            if (underlying == typeof(string))
            {
                return "string";
            }

            if (underlying == typeof(DateTime))
            {
                return "datetime";
            }

            if (underlying == typeof(decimal))
            {
                return "decimal";
            }

            throw new NotSupportedException($"Unsupported column CLR type: {clrType.Name}");
        }

        private static string DatabasesRoot() => Path.Combine(AppContext.BaseDirectory, "databases");

        private static bool IsSystemCatalog(NamedTableReference tableRef, string viewName)
        {
            var schema = tableRef.SchemaObject.SchemaIdentifier?.Value;
            var name = tableRef.SchemaObject.BaseIdentifier.Value;
            return string.Equals(schema, "sys", StringComparison.OrdinalIgnoreCase)
                && string.Equals(name, viewName, StringComparison.OrdinalIgnoreCase);
        }

        private string TablesDirectory() => Path.Combine(this.RequireDatabasePath(), "tables");

        /// <summary>
        /// Returns the currently selected database directory, throwing a descriptive
        /// exception when no database has been selected with USE.
        /// </summary>
        private string RequireDatabasePath()
        {
            if (string.IsNullOrEmpty(this.session.CurrentDatabasePath) ||
                !Directory.Exists(this.session.CurrentDatabasePath))
            {
                throw new Exception("No database selected. Use 'USE <database>;' to select a database first.");
            }

            return this.session.CurrentDatabasePath!;
        }

        private static void CollectObjects(string dbPath, string subDirectory, string pattern, string type, List<(string Name, string Type)> sink)
        {
            var dir = Path.Combine(dbPath, subDirectory);
            if (!Directory.Exists(dir))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(dir, pattern))
            {
                sink.Add((Path.GetFileNameWithoutExtension(file), type));
            }
        }

        private void Fail(Exception ex)
        {
            this.LastResult.Success = false;
            this.LastResult.Message = ex.Message;
        }

        private sealed class TableData
        {
            public TableData(Dictionary<string, List<object?>> data, List<string> order, Dictionary<string, string> types, int rowCount)
            {
                this.Data = data;
                this.Order = order;
                this.Types = types;
                this.RowCount = rowCount;
            }

            public Dictionary<string, List<object?>> Data { get; }

            public List<string> Order { get; }

            public Dictionary<string, string> Types { get; }

            public int RowCount { get; }
        }

        private sealed class ColumnRef
        {
            public ColumnRef(string qualifier, string name)
            {
                this.Qualifier = qualifier;
                this.Name = name;
            }

            public string Qualifier { get; }

            public string Name { get; }
        }

        private sealed class RowSet
        {
            public RowSet(List<ColumnRef> schema, List<object?[]> rows)
            {
                this.Schema = schema;
                this.Rows = rows;
            }

            public List<ColumnRef> Schema { get; }

            public List<object?[]> Rows { get; }
        }

        /// <summary>
        /// Evaluation scope for a single row, with an optional parent scope used to resolve
        /// correlated columns referenced from a subquery.
        /// </summary>
        private sealed class Env
        {
            private readonly List<ColumnRef> schema;
            private readonly object?[] values;
            private readonly Env? parent;

            public Env(List<ColumnRef> schema, object?[] values, Env? parent)
            {
                this.schema = schema;
                this.values = values;
                this.parent = parent;
            }

            public bool TryResolve(string? qualifier, string name, out object? value)
            {
                var found = -1;
                for (var i = 0; i < this.schema.Count; i++)
                {
                    var column = this.schema[i];
                    if (!string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (qualifier != null &&
                        !string.Equals(column.Qualifier, qualifier, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (found >= 0)
                    {
                        var display = qualifier == null ? name : $"{qualifier}.{name}";
                        throw new ArgumentException($"Column [{display}] is ambiguous.");
                    }

                    found = i;
                }

                if (found >= 0)
                {
                    value = this.values[found];
                    return true;
                }

                if (this.parent != null)
                {
                    return this.parent.TryResolve(qualifier, name, out value);
                }

                value = null;
                return false;
            }
        }
    }
}
