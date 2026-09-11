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
        private Dictionary<string, RowSet>? cteScope;
        private readonly Dictionary<string, ScalarExpression> parsedExpressionCache = new(StringComparer.Ordinal);

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

        /// <summary>
        /// Executes a multi-statement T-SQL script, running each statement in order and returning a
        /// result per statement. GO batch separators are honored. Statements share this visitor's
        /// session, so a CREATE DATABASE / USE / CREATE TABLE / INSERT sequence works as expected.
        /// By default execution stops at the first failing statement (like sqlcmd without -b off).
        /// </summary>
        public List<QueryResult> RunScript(string script, bool continueOnError = false)
        {
            var results = new List<QueryResult>();

            var fragment = this.sqlParser.Parse(new StringReader(script), out IList<ParseError> parseErrors);
            if (parseErrors != null && parseErrors.Count > 0)
            {
                results.Add(new QueryResult
                {
                    Success = false,
                    Message = "Parse error: " + string.Join("; ",
                        parseErrors.Select(e => $"Line {e.Line}, Col {e.Column}: {e.Message}")),
                });
                return results;
            }

            var statements = new List<TSqlStatement>();
            if (fragment is TSqlScript tsqlScript)
            {
                foreach (var batch in tsqlScript.Batches)
                {
                    statements.AddRange(batch.Statements);
                }
            }

            foreach (var statement in statements)
            {
                this.LastResult = new QueryResult();
                try
                {
                    statement.Accept(this);
                }
                catch (Exception ex)
                {
                    this.Fail(ex);
                }

                results.Add(this.LastResult);
                if (!this.LastResult.Success && !continueOnError)
                {
                    break;
                }
            }

            return results;
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
                this.Catalog().SeedDatabase();
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
                var (schema, table) = this.ResolveTableName(node.SchemaObjectName);
                if (!this.Catalog().SchemaExists(schema))
                {
                    throw new Exception($"Schema '{schema}' does not exist. Create it with CREATE SCHEMA first.");
                }

                this.Authorize(Security.SecurityAction.CreateObject, schema, table);

                var tablesDir = this.TablesDirectory();
                Directory.CreateDirectory(tablesDir);
                var filePath = this.ResolveTableFilePath(schema, table);

                var columns = node.Definition.ColumnDefinitions.ToList();
                this.CreateTable(table, filePath, columns);
                this.LastResult.Message = $"Table '{table}' created.";
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
                using var _ = this.EnterCteScope(node.WithCtesAndXmlNamespaces);
                var spec = node.InsertSpecification;

                if (spec.Target is not NamedTableReference tableReference)
                {
                    throw new NotSupportedException("INSERT target must be a table.");
                }

                var (schema, table) = this.ResolveTableName(tableReference.SchemaObject);
                this.Authorize(Security.SecurityAction.Insert, schema, table);
                var filePath = this.ResolveTableFilePath(schema, table);
                if (!File.Exists(filePath))
                {
                    throw new Exception($"Table [{table}] does not exist.");
                }

                var tableData = this.LoadTableAsync(filePath).GetAwaiter().GetResult();
                var meta = LoadMeta(filePath);

                var explicitColumns = spec.Columns
                    .Select(c => c.MultiPartIdentifier.Identifiers[0].Value)
                    .ToList();
                var insertColumns = explicitColumns.Count > 0
                    ? explicitColumns
                    : this.InsertableColumns(tableData, meta);

                foreach (var col in insertColumns)
                {
                    if (!tableData.Types.ContainsKey(col))
                    {
                        throw new ArgumentException($"Column [{col}] does not exist in table [{table}].");
                    }
                }

                List<Dictionary<string, object?>> providedRows;
                if (spec.InsertSource is ValuesInsertSource valuesSource)
                {
                    providedRows = this.EvaluateValuesRows(valuesSource, insertColumns, tableData.Types);
                }
                else if (spec.InsertSource is SelectInsertSource selectSource)
                {
                    providedRows = this.EvaluateSelectRows(selectSource, insertColumns);
                }
                else
                {
                    throw new NotSupportedException("Only INSERT ... VALUES and INSERT ... SELECT are supported.");
                }

                var count = this.AppendRows(filePath, tableData, meta, providedRows);
                this.LastResult.Message = $"{count} row(s) inserted into [{table}].";
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
                using var _ = this.EnterCteScope(node.WithCtesAndXmlNamespaces);
                var rowSet = this.EvaluateQueryExpression(node.QueryExpression, null);
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
        /// Evaluates any query expression (a plain SELECT, a parenthesized query, or a
        /// UNION/EXCEPT/INTERSECT combination) into a <see cref="RowSet"/>.
        /// </summary>
        private RowSet EvaluateQueryExpression(QueryExpression expression, Env? outer, long? limit = null)
        {
            switch (expression)
            {
                case QuerySpecification querySpec:
                    return this.EvaluateQuery(querySpec, outer, limit);

                case QueryParenthesisExpression paren:
                    return this.EvaluateQueryExpression(paren.QueryExpression, outer, limit);

                case BinaryQueryExpression binary:
                {
                    var left = this.EvaluateQueryExpression(binary.FirstQueryExpression, outer);
                    var right = this.EvaluateQueryExpression(binary.SecondQueryExpression, outer);
                    var rows = CombineSetOperation(left.Rows, right.Rows, binary.BinaryQueryExpressionType, binary.All);
                    return new RowSet(left.Schema, rows);
                }

                default:
                    throw new NotSupportedException($"Unsupported query expression: {expression.GetType().Name}");
            }
        }

        private static List<object?[]> CombineSetOperation(
            List<object?[]> left,
            List<object?[]> right,
            BinaryQueryExpressionType type,
            bool all)
        {
            switch (type)
            {
                case BinaryQueryExpressionType.Union:
                {
                    var rows = new List<object?[]>(left);
                    rows.AddRange(right);
                    return all ? rows : DistinctRows(rows);
                }

                case BinaryQueryExpressionType.Except:
                {
                    var rightKeys = new HashSet<string>(right.Select(RowKey));
                    var rows = left.Where(r => !rightKeys.Contains(RowKey(r))).ToList();
                    return all ? rows : DistinctRows(rows);
                }

                case BinaryQueryExpressionType.Intersect:
                {
                    var rightKeys = new HashSet<string>(right.Select(RowKey));
                    var rows = left.Where(r => rightKeys.Contains(RowKey(r))).ToList();
                    return all ? rows : DistinctRows(rows);
                }

                default:
                    throw new NotSupportedException($"Unsupported set operation: {type}");
            }
        }

        private static string RowKey(object?[] row) =>
            string.Join("\u0001", row.Select(v => v?.ToString() ?? "\0NULL"));

        /// <summary>
        /// Evaluates a query specification (top-level SELECT, derived table, or subquery) into a
        /// projected <see cref="RowSet"/>. <paramref name="outer"/> supplies correlated columns.
        /// <paramref name="limit"/> is an optional row cap the caller can push down.
        /// </summary>
        private RowSet EvaluateQuery(QuerySpecification querySpec, Env? outer, long? limit = null)
        {
            var hasAggregate = querySpec.GroupByClause != null || SelectHasAggregate(querySpec);

            // FROM-less SELECT (e.g. a scalar subquery "(SELECT NULL)" or "SELECT 1"): evaluate the
            // projection against a single empty row.
            if (querySpec.FromClause == null || querySpec.FromClause.TableReferences.Count == 0)
            {
                var emptySchema = new List<ColumnRef>();
                var singleRow = new List<object?[]> { Array.Empty<object?>() };
                return hasAggregate
                    ? this.EvaluateAggregate(querySpec, emptySchema, singleRow, outer)
                    : this.ProjectRows(querySpec, emptySchema, singleRow, outer);
            }

            // TOP without WHERE/ORDER/GROUP/DISTINCT/aggregate can be pushed into the source so a
            // large CROSS JOIN generator (e.g. sys.all_objects) does not fully materialize.
            long? pushLimit = limit;
            var topCount = this.GetTopCount(querySpec.TopRowFilter);
            if (pushLimit == null &&
                querySpec.WhereClause == null &&
                querySpec.OrderByClause == null &&
                querySpec.GroupByClause == null &&
                querySpec.UniqueRowFilter != UniqueRowFilter.Distinct &&
                !hasAggregate &&
                topCount.HasValue)
            {
                pushLimit = topCount.Value;
            }

            var source = this.EvaluateTableReference(querySpec.FromClause.TableReferences[0], outer, pushLimit);
            for (var i = 1; i < querySpec.FromClause.TableReferences.Count; i++)
            {
                source = this.LimitedCrossJoin(source, this.EvaluateTableReference(querySpec.FromClause.TableReferences[i], outer, pushLimit), pushLimit);
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

            if (hasAggregate)
            {
                return this.EvaluateAggregate(querySpec, source.Schema, filtered, outer);
            }

            // ORDER BY (resolved against the source rows, before projection). Order keys are
            // precomputed once per row so the comparer does not re-evaluate expressions.
            if (querySpec.OrderByClause != null)
            {
                var elems = querySpec.OrderByClause.OrderByElements;
                var keyed = filtered
                    .Select(row =>
                    {
                        var env = new Env(source.Schema, row, outer);
                        return (Row: row, Keys: elems.Select(oe => this.GetScalarValue(oe.Expression, env)).ToArray());
                    })
                    .ToList();

                keyed.Sort((a, b) =>
                {
                    for (var k = 0; k < elems.Count; k++)
                    {
                        var cmp = CompareForOrder(a.Keys[k], b.Keys[k]);
                        if (elems[k].SortOrder == SortOrder.Descending)
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

                filtered = keyed.Select(x => x.Row).ToList();
            }

            return this.ProjectRows(querySpec, source.Schema, filtered, outer);
        }

        private RowSet ProjectRows(QuerySpecification querySpec, List<ColumnRef> sourceSchema, List<object?[]> filtered, Env? outer)
        {
            var projSchema = new List<ColumnRef>();
            var projectors = new List<Func<object?[], int, object?>>();

            foreach (var element in querySpec.SelectElements)
            {
                if (element is SelectStarExpression star)
                {
                    var starQualifier = star.Qualifier?.Identifiers.LastOrDefault()?.Value;
                    for (var i = 0; i < sourceSchema.Count; i++)
                    {
                        var column = sourceSchema[i];
                        if (starQualifier != null &&
                            !string.Equals(column.Qualifier, starQualifier, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var index = i;
                        projSchema.Add(new ColumnRef(column.Qualifier, column.Name));
                        projectors.Add((values, _) => values[index]);
                    }
                }
                else if (element is SelectScalarExpression scalar)
                {
                    var alias = scalar.ColumnName?.Value;

                    if (scalar.Expression is FunctionCall { OverClause: { } } window)
                    {
                        var numbers = this.ComputeWindowValues(window, sourceSchema, filtered, outer);
                        projSchema.Add(new ColumnRef(string.Empty, alias ?? "column"));
                        projectors.Add((_, rowIndex) => numbers[rowIndex]);
                        continue;
                    }

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
                    projectors.Add((values, _) => this.GetScalarValue(expr, new Env(sourceSchema, values, outer)));
                }
                else
                {
                    throw new NotSupportedException($"Unsupported select element: {element.GetType().Name}");
                }
            }

            var projRows = new List<object?[]>();
            for (var r = 0; r < filtered.Count; r++)
            {
                var projected = new object?[projectors.Count];
                for (var i = 0; i < projectors.Count; i++)
                {
                    projected[i] = projectors[i](filtered[r], r);
                }

                projRows.Add(projected);
            }

            if (querySpec.UniqueRowFilter == UniqueRowFilter.Distinct)
            {
                projRows = DistinctRows(projRows);
            }

            var topN = this.GetTopCount(querySpec.TopRowFilter);
            if (topN.HasValue && projRows.Count > topN.Value)
            {
                projRows = projRows.Take(topN.Value).ToList();
            }

            return new RowSet(projSchema, projRows);
        }

        /// <summary>Evaluates a TOP (n) clause to a constant row count, unwrapping parentheses. Ignores TOP PERCENT.</summary>
        private int? GetTopCount(TopRowFilter? top)
        {
            if (top == null || top.Percent)
            {
                return null;
            }

            var expr = top.Expression;
            while (expr is ParenthesisExpression paren)
            {
                expr = paren.Expression;
            }

            if (expr is IntegerLiteral literal)
            {
                return int.Parse(literal.Value, CultureInfo.InvariantCulture);
            }

            var value = this.GetScalarValue(top.Expression, EmptyRowEnv);
            return value == null ? null : ToInt(value);
        }

        private RowSet EvaluateTableReference(TableReference tableReference, Env? outer, long? limit = null)
        {
            switch (tableReference)
            {
                case NamedTableReference named:
                {
                    var qualifier = named.Alias?.Value ?? named.SchemaObject.BaseIdentifier.Value;

                    // A common table expression (WITH ...) shadows physical tables when unqualified.
                    if (named.SchemaObject.SchemaIdentifier == null &&
                        this.cteScope != null &&
                        this.cteScope.TryGetValue(named.SchemaObject.BaseIdentifier.Value, out var cte))
                    {
                        var cteSchema = cte.Schema.Select(c => new ColumnRef(qualifier, c.Name)).ToList();
                        return new RowSet(cteSchema, cte.Rows);
                    }

                    if (IsSystemCatalog(named, "all_objects") || IsSystemCatalog(named, "objects_generator"))
                    {
                        return BuildAllObjectsRowSet(qualifier, limit);
                    }

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

                    var securityView = this.TryBuildSecurityCatalog(named, qualifier);
                    if (securityView != null)
                    {
                        return securityView;
                    }

                    var (schemaName, tableName) = this.ResolveTableName(named.SchemaObject);
                    this.Authorize(Security.SecurityAction.Select, schemaName, tableName);

                    var filePath = this.ResolveTableFilePath(schemaName, tableName);
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
                    var alias = derived.Alias?.Value
                        ?? throw new NotSupportedException("A derived table (subquery in FROM) requires an alias.");

                    var inner = this.EvaluateQueryExpression(derived.QueryExpression, outer);
                    var schema = inner.Schema.Select(c => new ColumnRef(alias, c.Name)).ToList();
                    return new RowSet(schema, inner.Rows);
                }

                case InlineDerivedTable inlineTable:
                {
                    var alias = inlineTable.Alias?.Value
                        ?? throw new NotSupportedException("An inline table (VALUES in FROM) requires an alias.");

                    var columnCount = inlineTable.RowValues.FirstOrDefault()?.ColumnValues.Count ?? 0;
                    var names = inlineTable.Columns.Count > 0
                        ? inlineTable.Columns.Select(c => c.Value).ToList()
                        : Enumerable.Range(1, columnCount).Select(i => "column" + i).ToList();
                    var schema = names.Select(n => new ColumnRef(alias, n)).ToList();

                    var rows = new List<object?[]>();
                    foreach (var rowValue in inlineTable.RowValues)
                    {
                        rows.Add(rowValue.ColumnValues
                            .Select(v => v is NullLiteral ? null : this.GetScalarValue(v, outer ?? EmptyRowEnv))
                            .ToArray());
                    }

                    return new RowSet(schema, rows);
                }

                case QualifiedJoin qualifiedJoin:
                {
                    var left = this.EvaluateTableReference(qualifiedJoin.FirstTableReference, outer);
                    var right = this.EvaluateTableReference(qualifiedJoin.SecondTableReference, outer);
                    return this.JoinRowSets(left, right, qualifiedJoin.SearchCondition, qualifiedJoin.QualifiedJoinType, outer);
                }

                case UnqualifiedJoin unqualifiedJoin:
                {
                    if (unqualifiedJoin.UnqualifiedJoinType == UnqualifiedJoinType.CrossJoin)
                    {
                        var left = this.EvaluateTableReference(unqualifiedJoin.FirstTableReference, outer, limit);
                        var right = this.EvaluateTableReference(unqualifiedJoin.SecondTableReference, outer, limit);
                        return this.LimitedCrossJoin(left, right, limit);
                    }

                    throw new NotSupportedException($"Unsupported join: {unqualifiedJoin.UnqualifiedJoinType}");
                }

                case JoinParenthesisTableReference parenthesis:
                    return this.EvaluateTableReference(parenthesis.Join, outer, limit);

                default:
                    throw new NotSupportedException($"Unsupported table reference: {tableReference.GetType().Name}");
            }
        }

        private RowSet JoinRowSets(RowSet left, RowSet right, BooleanExpression? on, QualifiedJoinType joinType, Env? outer)
        {
            var schema = left.Schema.Concat(right.Schema).ToList();
            var leftWidth = left.Schema.Count;
            var rightWidth = right.Schema.Count;

            // Fast path: INNER equi-join evaluated with a hash lookup instead of a nested loop.
            if (joinType == QualifiedJoinType.Inner && on != null &&
                this.TryHashJoin(left, right, on, outer, out var hashed))
            {
                return new RowSet(schema, hashed);
            }

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

        private RowSet LimitedCrossJoin(RowSet left, RowSet right, long? limit)
        {
            var schema = left.Schema.Concat(right.Schema).ToList();
            var rows = new List<object?[]>();
            foreach (var leftRow in left.Rows)
            {
                foreach (var rightRow in right.Rows)
                {
                    rows.Add(Concat(leftRow, rightRow));
                    if (limit.HasValue && rows.Count >= limit.Value)
                    {
                        return new RowSet(schema, rows);
                    }
                }
            }

            return new RowSet(schema, rows);
        }

        /// <summary>
        /// Attempts an inner equi-join via hashing. Succeeds only when every top-level AND term of
        /// the ON condition is an equality whose two sides reference exactly one input each (a pure
        /// left key vs a pure right key). Returns false to fall back to the generic nested loop.
        /// </summary>
        private bool TryHashJoin(RowSet left, RowSet right, BooleanExpression on, Env? outer, out List<object?[]> rows)
        {
            rows = new List<object?[]>();

            var leftQualifiers = left.Schema.Select(c => c.Qualifier).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rightQualifiers = right.Schema.Select(c => c.Qualifier).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var leftKeyExprs = new List<ScalarExpression>();
            var rightKeyExprs = new List<ScalarExpression>();

            foreach (var term in SplitConjunction(on))
            {
                if (term is not BooleanComparisonExpression { ComparisonType: BooleanComparisonType.Equals } cmp)
                {
                    return false;
                }

                var firstSide = ClassifySide(cmp.FirstExpression, leftQualifiers, rightQualifiers);
                var secondSide = ClassifySide(cmp.SecondExpression, leftQualifiers, rightQualifiers);

                if (firstSide == Side.Left && secondSide == Side.Right)
                {
                    leftKeyExprs.Add(cmp.FirstExpression);
                    rightKeyExprs.Add(cmp.SecondExpression);
                }
                else if (firstSide == Side.Right && secondSide == Side.Left)
                {
                    leftKeyExprs.Add(cmp.SecondExpression);
                    rightKeyExprs.Add(cmp.FirstExpression);
                }
                else
                {
                    return false;
                }
            }

            if (leftKeyExprs.Count == 0)
            {
                return false;
            }

            var lookup = new Dictionary<string, List<object?[]>>();
            foreach (var rightRow in right.Rows)
            {
                var env = new Env(right.Schema, rightRow, outer);
                var key = string.Join("\u0001", rightKeyExprs.Select(e => Stringify(this.GetScalarValue(e, env))));
                if (!lookup.TryGetValue(key, out var bucket))
                {
                    bucket = new List<object?[]>();
                    lookup[key] = bucket;
                }

                bucket.Add(rightRow);
            }

            foreach (var leftRow in left.Rows)
            {
                var env = new Env(left.Schema, leftRow, outer);
                var key = string.Join("\u0001", leftKeyExprs.Select(e => Stringify(this.GetScalarValue(e, env))));
                if (lookup.TryGetValue(key, out var bucket))
                {
                    foreach (var rightRow in bucket)
                    {
                        rows.Add(Concat(leftRow, rightRow));
                    }
                }
            }

            return true;
        }

        private enum Side
        {
            Left,
            Right,
            Both,
            Unknown,
        }

        private static IEnumerable<BooleanExpression> SplitConjunction(BooleanExpression expr)
        {
            if (expr is BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } and)
            {
                foreach (var e in SplitConjunction(and.FirstExpression))
                {
                    yield return e;
                }

                foreach (var e in SplitConjunction(and.SecondExpression))
                {
                    yield return e;
                }
            }
            else
            {
                yield return expr;
            }
        }

        /// <summary>Determines whether an expression references only the left input, only the right, both, or is not analyzable.</summary>
        private static Side ClassifySide(ScalarExpression expr, HashSet<string> leftQualifiers, HashSet<string> rightQualifiers)
        {
            var quals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!TryCollectQualifiers(expr, quals))
            {
                return Side.Unknown;
            }

            var referencesLeft = quals.Any(leftQualifiers.Contains);
            var referencesRight = quals.Any(rightQualifiers.Contains);

            // A constant (no column references) can be evaluated on either side; treat as left.
            if (!referencesLeft && !referencesRight)
            {
                return Side.Left;
            }

            if (referencesLeft && referencesRight)
            {
                return Side.Both;
            }

            return referencesLeft ? Side.Left : Side.Right;
        }

        /// <summary>Collects column qualifiers referenced by an expression. Returns false if the expression contains an unqualified column or a node type we can't safely analyze.</summary>
        private static bool TryCollectQualifiers(ScalarExpression? expr, HashSet<string> qualifiers)
        {
            switch (expr)
            {
                case null:
                case IntegerLiteral:
                case NumericLiteral:
                case MoneyLiteral:
                case StringLiteral:
                case NullLiteral:
                    return true;

                case ColumnReferenceExpression col:
                {
                    var ids = col.MultiPartIdentifier.Identifiers;
                    if (ids.Count < 2)
                    {
                        return false;
                    }

                    qualifiers.Add(ids[ids.Count - 2].Value);
                    return true;
                }

                case ParenthesisExpression paren:
                    return TryCollectQualifiers(paren.Expression, qualifiers);

                case UnaryExpression unary:
                    return TryCollectQualifiers(unary.Expression, qualifiers);

                case BinaryExpression binary:
                    return TryCollectQualifiers(binary.FirstExpression, qualifiers) &&
                           TryCollectQualifiers(binary.SecondExpression, qualifiers);

                case CastCall cast:
                    return TryCollectQualifiers(cast.Parameter, qualifiers);

                case ConvertCall convert:
                    return TryCollectQualifiers(convert.Parameter, qualifiers);

                case FunctionCall fn:
                    return fn.Parameters.All(p => TryCollectQualifiers(p, qualifiers));

                case LeftFunctionCall left:
                    return left.Parameters.All(p => TryCollectQualifiers(p, qualifiers));

                case RightFunctionCall right:
                    return right.Parameters.All(p => TryCollectQualifiers(p, qualifiers));

                default:
                    return false;
            }
        }

        private RowSet CrossJoin(RowSet left, RowSet right)
            => this.JoinRowSets(left, right, null, QualifiedJoinType.Inner, null);

        /// <summary>
        /// Synthetic row source approximating <c>sys.all_objects</c>: a wide-enough sequence of
        /// rows used by scripts that generate data via ROW_NUMBER() over a cross join.
        /// </summary>
        private static RowSet BuildAllObjectsRowSet(string qualifier, long? limit)
        {
            const int baseCount = 2000;
            var count = baseCount;
            if (limit.HasValue && limit.Value < count)
            {
                count = (int)Math.Max(1, limit.Value);
            }

            var schema = new List<ColumnRef>
            {
                new(qualifier, "object_id"),
                new(qualifier, "name"),
                new(qualifier, "type"),
            };

            var rows = new List<object?[]>(count);
            for (var i = 1; i <= count; i++)
            {
                rows.Add(new object?[] { i, "obj_" + i, "U " });
            }

            return new RowSet(schema, rows);
        }

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
                    return this.EvaluateQueryExpression(exists.Subquery.QueryExpression, env).Rows.Count > 0;
                }

                default:
                    throw new NotSupportedException($"Unsupported WHERE expression: {expr.GetType().Name}");
            }
        }

        private List<object?> EvaluateSubqueryColumn(ScalarSubquery subquery, Env outer)
        {
            var rowSet = this.EvaluateQueryExpression(subquery.QueryExpression, outer);
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

                case BinaryExpression binary:
                    return this.EvalBinary(binary, env);

                case FunctionCall functionCall:
                    return this.EvalFunction(functionCall, env);

                case LeftFunctionCall leftCall:
                {
                    var s = Stringify(this.GetScalarValue(leftCall.Parameters[0], env));
                    var n = ToInt(this.GetScalarValue(leftCall.Parameters[1], env));
                    return n <= 0 ? string.Empty : (n >= s.Length ? s : s.Substring(0, n));
                }

                case RightFunctionCall rightCall:
                {
                    var s = Stringify(this.GetScalarValue(rightCall.Parameters[0], env));
                    var n = ToInt(this.GetScalarValue(rightCall.Parameters[1], env));
                    return n <= 0 ? string.Empty : (n >= s.Length ? s : s.Substring(s.Length - n));
                }

                case CastCall cast:
                    return ConvertToType(this.GetScalarValue(cast.Parameter, env), cast.DataType);

                case ConvertCall convert:
                    return ConvertToType(this.GetScalarValue(convert.Parameter, env), convert.DataType);

                case SearchedCaseExpression searchedCase:
                    return this.EvalSearchedCase(searchedCase, env);

                case SimpleCaseExpression simpleCase:
                    return this.EvalSimpleCase(simpleCase, env);

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

        private void CreateTable(string tableName, string filePath, List<ColumnDefinition> columns)
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
            var meta = new TableMeta();

            foreach (var column in columns)
            {
                var name = column.ColumnIdentifier.Value;
                var columnMeta = new ColumnMeta { Name = name, Nullable = true };

                if (column.ComputedColumnExpression != null)
                {
                    // Computed column: no declared type. Persist the expression and infer a
                    // physical type (from a CAST/CONVERT target when present, else decimal).
                    columnMeta.ComputedSql = GenerateSql(column.ComputedColumnExpression);
                    columnMeta.Type = InferComputedType(column.ComputedColumnExpression);
                }
                else
                {
                    if (column.DataType?.Name?.BaseIdentifier?.Value is not string sqlType)
                    {
                        throw new ArgumentException($"Column '{name}' has no data type.");
                    }

                    columnMeta.Type = MapSqlType(sqlType);

                    if (column.IdentityOptions != null)
                    {
                        columnMeta.IsIdentity = true;
                        columnMeta.IdentitySeed = LiteralToLong(column.IdentityOptions.IdentitySeed, 1);
                        columnMeta.IdentityIncrement = LiteralToLong(column.IdentityOptions.IdentityIncrement, 1);
                        columnMeta.IdentityCurrent = columnMeta.IdentitySeed - columnMeta.IdentityIncrement;
                        columnMeta.Nullable = false;
                    }

                    foreach (var constraint in column.Constraints)
                    {
                        switch (constraint)
                        {
                            case NullableConstraintDefinition nullable:
                                columnMeta.Nullable = nullable.Nullable;
                                break;
                            case DefaultConstraintDefinition def:
                                columnMeta.DefaultSql = GenerateSql(def.Expression);
                                break;
                            case UniqueConstraintDefinition unique when unique.IsPrimaryKey:
                                columnMeta.Nullable = false;
                                break;
                        }
                    }
                }

                types[name] = columnMeta.Type;
                order.Add(name);
                meta.Columns.Add(columnMeta);
            }

            if (File.Exists(filePath))
            {
                throw new Exception($"Table [{tableName}] already exists.");
            }

            var data = order.ToDictionary(c => c, _ => new List<object?>());
            this.WriteTableAsync(filePath, order, types, data).GetAwaiter().GetResult();
            SaveMeta(filePath, meta);
        }

        private static long LiteralToLong(ScalarExpression? expression, long fallback)
        {
            return expression switch
            {
                IntegerLiteral i => long.Parse(i.Value, CultureInfo.InvariantCulture),
                NumericLiteral n => (long)decimal.Parse(n.Value, CultureInfo.InvariantCulture),
                UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative } u => -LiteralToLong(u.Expression, fallback),
                null => fallback,
                _ => fallback,
            };
        }

        private static string InferComputedType(ScalarExpression expression)
        {
            var target = expression switch
            {
                ConvertCall c => c.DataType,
                CastCall c => c.DataType,
                _ => null,
            };

            return target?.Name?.BaseIdentifier?.Value is string sqlType ? MapSqlType(sqlType) : "decimal";
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

        private static string DatabasesRoot() => ParqBase.DatabasesRoot;

        private static bool IsSystemCatalog(NamedTableReference tableRef, string viewName)
        {
            var schema = tableRef.SchemaObject.SchemaIdentifier?.Value;
            var name = tableRef.SchemaObject.BaseIdentifier.Value;
            return string.Equals(schema, "sys", StringComparison.OrdinalIgnoreCase)
                && string.Equals(name, viewName, StringComparison.OrdinalIgnoreCase);
        }

        private string TablesDirectory() => Path.Combine(this.RequireDatabasePath(), "tables");

        /// <summary>
        /// Splits a table reference into (schema, table), defaulting an unqualified name to dbo.
        /// </summary>
        private (string Schema, string Table) ResolveTableName(SchemaObjectName name)
        {
            var schema = name.SchemaIdentifier?.Value ?? Security.SecurityCatalog.DboSchema;
            return (schema, name.BaseIdentifier.Value);
        }

        /// <summary>
        /// Maps a (schema, table) pair to its Parquet file. dbo tables keep their historical
        /// flat name (tables/&lt;table&gt;.parquet); other schemas are namespaced by prefixing
        /// the schema (tables/&lt;schema&gt;.&lt;table&gt;.parquet).
        /// </summary>
        private string ResolveTableFilePath(string schema, string table)
        {
            var fileName = string.Equals(schema, Security.SecurityCatalog.DboSchema, StringComparison.OrdinalIgnoreCase)
                ? $"{table}.parquet"
                : $"{schema}.{table}.parquet";
            return Path.Combine(this.TablesDirectory(), fileName);
        }

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
