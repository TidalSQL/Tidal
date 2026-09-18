namespace ParqBaseLib
{
    using Microsoft.SqlServer.TransactSql.ScriptDom;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;

    internal partial class ParqBaseStatementVisitor
    {
        /// <summary>Materializes a WITH clause into <see cref="cteScope"/> and restores it on dispose.</summary>
        private IDisposable EnterCteScope(WithCtesAndXmlNamespaces? with)
        {
            var previous = this.cteScope;
            if (with != null)
            {
                var scope = new Dictionary<string, RowSet>(StringComparer.OrdinalIgnoreCase);
                this.cteScope = scope;
                foreach (var cte in with.CommonTableExpressions)
                {
                    var rs = this.EvaluateQueryExpression(cte.QueryExpression, null);
                    var names = cte.Columns.Count > 0
                        ? cte.Columns.Select(c => c.Value).ToList()
                        : rs.Schema.Select(c => c.Name).ToList();
                    var schema = names.Select(n => new ColumnRef(cte.ExpressionName.Value, n)).ToList();
                    scope[cte.ExpressionName.Value] = new RowSet(schema, rs.Rows);
                }
            }

            return new ScopeReset(this, previous);
        }

        private sealed class ScopeReset : IDisposable
        {
            private readonly ParqBaseStatementVisitor owner;
            private readonly Dictionary<string, RowSet>? previous;

            public ScopeReset(ParqBaseStatementVisitor owner, Dictionary<string, RowSet>? previous)
            {
                this.owner = owner;
                this.previous = previous;
            }

            public void Dispose() => this.owner.cteScope = this.previous;
        }

        private static bool SelectHasAggregate(QuerySpecification querySpec) =>
            querySpec.SelectElements.OfType<SelectScalarExpression>()
                .Any(e => ContainsAggregate(e.Expression));

        private static bool ContainsAggregate(ScalarExpression? expression)
        {
            switch (expression)
            {
                case null:
                    return false;
                case FunctionCall fn when fn.OverClause == null &&
                                          AggregateFunctions.Contains(fn.FunctionName.Value):
                    return true;
                case FunctionCall fn:
                    return fn.Parameters.Any(ContainsAggregate);
                case BinaryExpression bin:
                    return ContainsAggregate(bin.FirstExpression) || ContainsAggregate(bin.SecondExpression);
                case ParenthesisExpression paren:
                    return ContainsAggregate(paren.Expression);
                case UnaryExpression unary:
                    return ContainsAggregate(unary.Expression);
                case CastCall cast:
                    return ContainsAggregate(cast.Parameter);
                case ConvertCall conv:
                    return ContainsAggregate(conv.Parameter);
                default:
                    return false;
            }
        }

        private RowSet EvaluateAggregate(QuerySpecification querySpec, List<ColumnRef> schema, List<object?[]> rows, Env? outer)
        {
            var groupExprs = querySpec.GroupByClause?.GroupingSpecifications
                .OfType<ExpressionGroupingSpecification>()
                .Select(g => g.Expression)
                .ToList() ?? new List<ScalarExpression>();

            // Build the groups (one implicit group when there is no GROUP BY).
            var groups = new List<(object?[]? Sample, List<object?[]> Rows)>();
            if (groupExprs.Count == 0)
            {
                groups.Add((rows.Count > 0 ? rows[0] : null, rows));
            }
            else
            {
                var map = new Dictionary<string, (object?[] Sample, List<object?[]> Rows)>();
                var order = new List<string>();
                foreach (var row in rows)
                {
                    var env = new Env(schema, row, outer);
                    var key = string.Join("\u0001", groupExprs.Select(g => this.GetScalarValue(g, env)?.ToString() ?? "\0"));
                    if (!map.TryGetValue(key, out var bucket))
                    {
                        bucket = (row, new List<object?[]>());
                        map[key] = bucket;
                        order.Add(key);
                    }

                    bucket.Rows.Add(row);
                }

                foreach (var key in order)
                {
                    groups.Add((map[key].Sample, map[key].Rows));
                }
            }

            var projSchema = new List<ColumnRef>();
            foreach (var element in querySpec.SelectElements)
            {
                if (element is not SelectScalarExpression scalar)
                {
                    throw new NotSupportedException("Only scalar select elements are supported with GROUP BY / aggregates.");
                }

                var alias = scalar.ColumnName?.Value;
                if (alias == null && scalar.Expression is ColumnReferenceExpression col)
                {
                    alias = col.MultiPartIdentifier.Identifiers.Last().Value;
                }

                projSchema.Add(new ColumnRef(string.Empty, alias ?? "column"));
            }

            var resultRows = new List<object?[]>();

            foreach (var (sample, groupRows) in groups)
            {
                if (querySpec.HavingClause != null &&
                    !this.HavingSatisfied(querySpec.HavingClause.SearchCondition, schema, groupRows, sample, outer))
                {
                    continue;
                }

                var values = new object?[querySpec.SelectElements.Count];
                for (var i = 0; i < querySpec.SelectElements.Count; i++)
                {
                    var expr = ((SelectScalarExpression)querySpec.SelectElements[i]).Expression;
                    values[i] = this.EvaluateAggregateExpression(expr, schema, groupRows, sample, outer);
                }

                resultRows.Add(values);
            }

            return new RowSet(projSchema, resultRows);
        }

        // Evaluates a HAVING search condition for one group. Comparison operands are evaluated with
        // aggregate semantics (SUM/COUNT/... over the group, or a group column via the sample row),
        // mirroring EvaluateBoolean's structure for the connectives TPC-H uses.
        private bool HavingSatisfied(
            BooleanExpression expr, List<ColumnRef> schema, List<object?[]> groupRows, object?[]? sample, Env? outer)
        {
            switch (expr)
            {
                case BooleanParenthesisExpression p:
                    return this.HavingSatisfied(p.Expression, schema, groupRows, sample, outer);

                case BooleanBinaryExpression b:
                {
                    var left = this.HavingSatisfied(b.FirstExpression, schema, groupRows, sample, outer);
                    return b.BinaryExpressionType == BooleanBinaryExpressionType.And
                        ? left && this.HavingSatisfied(b.SecondExpression, schema, groupRows, sample, outer)
                        : left || this.HavingSatisfied(b.SecondExpression, schema, groupRows, sample, outer);
                }

                case BooleanNotExpression n:
                    return !this.HavingSatisfied(n.Expression, schema, groupRows, sample, outer);

                case BooleanComparisonExpression cmp:
                {
                    var a = this.EvaluateAggregateExpression(cmp.FirstExpression, schema, groupRows, sample, outer);
                    var b = this.EvaluateAggregateExpression(cmp.SecondExpression, schema, groupRows, sample, outer);
                    if (a == null || b == null)
                    {
                        return false;
                    }

                    return ApplyOperator(CompareTyped(a, b), cmp.ComparisonType);
                }

                case BooleanTernaryExpression t:
                {
                    var v = this.EvaluateAggregateExpression(t.FirstExpression, schema, groupRows, sample, outer);
                    var lo = this.EvaluateAggregateExpression(t.SecondExpression, schema, groupRows, sample, outer);
                    var hi = this.EvaluateAggregateExpression(t.ThirdExpression, schema, groupRows, sample, outer);
                    var between = v != null && lo != null && hi != null &&
                        CompareTyped(v, lo) >= 0 && CompareTyped(v, hi) <= 0;
                    return t.TernaryExpressionType == BooleanTernaryExpressionType.NotBetween ? !between : between;
                }

                case BooleanIsNullExpression isNull:
                {
                    var v = this.EvaluateAggregateExpression(isNull.Expression, schema, groupRows, sample, outer);
                    return isNull.IsNot ? v != null : v == null;
                }

                default:
                    throw new NotSupportedException($"Unsupported HAVING expression: {expr.GetType().Name}");
            }
        }

        private object? EvaluateAggregateExpression(ScalarExpression expr, List<ColumnRef> schema, List<object?[]> groupRows, object?[]? sample, Env? outer)
        {
            if (expr is FunctionCall fn && fn.OverClause == null && AggregateFunctions.Contains(fn.FunctionName.Value))
            {
                return this.ComputeAggregate(fn, schema, groupRows, outer);
            }

            // Compound expressions that contain aggregates must be combined at the group level, e.g.
            // SUM(x) / SUM(y) or 100.0 * SUM(...). Recurse structurally; leaves without aggregates fall
            // through to the sample-row evaluation below.
            if (ContainsAggregate(expr))
            {
                switch (expr)
                {
                    case ParenthesisExpression paren:
                        return this.EvaluateAggregateExpression(paren.Expression, schema, groupRows, sample, outer);

                    case UnaryExpression unary:
                    {
                        var v = this.EvaluateAggregateExpression(unary.Expression, schema, groupRows, sample, outer);
                        if (unary.UnaryExpressionType == UnaryExpressionType.Negative && v != null)
                        {
                            return ApplyBinaryValues(0, v, BinaryExpressionType.Subtract);
                        }

                        return v;
                    }

                    case BinaryExpression binary:
                    {
                        var l = this.EvaluateAggregateExpression(binary.FirstExpression, schema, groupRows, sample, outer);
                        var r = this.EvaluateAggregateExpression(binary.SecondExpression, schema, groupRows, sample, outer);
                        return ApplyBinaryValues(l, r, binary.BinaryExpressionType);
                    }
                }
            }

            // Non-aggregate expression: evaluate against a representative (sample) row.
            var sampleRow = sample ?? new object?[schema.Count];
            return this.GetScalarValue(expr, new Env(schema, sampleRow, outer));
        }

        private object? ComputeAggregate(FunctionCall fn, List<ColumnRef> schema, List<object?[]> groupRows, Env? outer)
        {
            var name = fn.FunctionName.Value.ToUpperInvariant();
            var arg = fn.Parameters.FirstOrDefault();
            var isStar = arg is ColumnReferenceExpression { ColumnType: ColumnType.Wildcard };

            if (name == "COUNT")
            {
                if (isStar || arg == null)
                {
                    return groupRows.Count;
                }

                return groupRows.Count(r => this.GetScalarValue(arg, new Env(schema, r, outer)) != null);
            }

            var values = groupRows
                .Select(r => this.GetScalarValue(arg!, new Env(schema, r, outer)))
                .Where(v => v != null)
                .ToList();

            switch (name)
            {
                case "SUM":
                {
                    if (values.Count == 0)
                    {
                        return null;
                    }

                    if (values.All(v => v is int || v is long))
                    {
                        return values.Sum(v => Convert.ToInt64(v, CultureInfo.InvariantCulture));
                    }

                    return values.Sum(v => ToDecimal(v));
                }

                case "AVG":
                {
                    if (values.Count == 0)
                    {
                        return null;
                    }

                    var total = values.Sum(v => ToDecimal(v));
                    return total / values.Count;
                }

                case "MIN":
                    return values.Count == 0 ? null : values.Aggregate((a, b) => CompareForOrder(a, b) <= 0 ? a : b);

                case "MAX":
                    return values.Count == 0 ? null : values.Aggregate((a, b) => CompareForOrder(a, b) >= 0 ? a : b);

                default:
                    throw new NotSupportedException($"Unsupported aggregate function: {name}");
            }
        }

        /// <summary>
        /// Computes window function values (currently ROW_NUMBER) aligned to <paramref name="rows"/>.
        /// </summary>
        private object?[] ComputeWindowValues(FunctionCall fn, List<ColumnRef> schema, List<object?[]> rows, Env? outer)
        {
            var name = fn.FunctionName.Value.ToUpperInvariant();
            if (name != "ROW_NUMBER")
            {
                throw new NotSupportedException($"Unsupported window function: {name}");
            }

            var partitions = fn.OverClause?.Partitions ?? new List<ScalarExpression>();
            var orderElems = fn.OverClause?.OrderByClause?.OrderByElements ?? new List<ExpressionWithSortOrder>();

            var indices = Enumerable.Range(0, rows.Count).ToList();

            string PartitionKey(int idx) =>
                string.Join("\u0001", partitions.Select(p => this.GetScalarValue(p, new Env(schema, rows[idx], outer))?.ToString() ?? "\0"));

            // Precompute each row's ORDER BY key once (avoids O(n log n) expression evaluations).
            var orderKeys = new object?[rows.Count][];
            if (orderElems.Count > 0)
            {
                for (var i = 0; i < rows.Count; i++)
                {
                    var env = new Env(schema, rows[i], outer);
                    orderKeys[i] = orderElems.Select(oe => this.GetScalarValue(oe.Expression, env)).ToArray();
                }
            }

            var result = new object?[rows.Count];
            foreach (var partition in indices.GroupBy(PartitionKey))
            {
                var ordered = partition.ToList();
                if (orderElems.Count > 0)
                {
                    ordered.Sort((a, b) =>
                    {
                        for (var k = 0; k < orderElems.Count; k++)
                        {
                            var cmp = CompareForOrder(orderKeys[a][k], orderKeys[b][k]);
                            if (orderElems[k].SortOrder == SortOrder.Descending)
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

                var n = 1;
                foreach (var idx in ordered)
                {
                    result[idx] = n++;
                }
            }

            return result;
        }
    }
}
