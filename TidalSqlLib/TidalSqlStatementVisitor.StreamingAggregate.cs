namespace TidalSqlLib
{
    using Microsoft.SqlServer.TransactSql.ScriptDom;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Parquet;

    /// <summary>
    /// Fast path for whole-table aggregate queries such as
    /// <c>SELECT SUM(l_quantity), COUNT(*) FROM lineitem</c>. Instead of materializing every column of
    /// every row into boxed object arrays (the general path in <see cref="EvaluateQuery"/>), this
    /// streams each Parquet part a row group at a time, decodes <b>only the columns the aggregates
    /// reference</b> (projection pushdown), and folds the values straight into running accumulators
    /// (no row materialization). <c>COUNT(*)</c> reads no column data at all — it sums row-group
    /// counts. Combined, this turns an O(rows × columns) scan with millions of boxed allocations into
    /// an O(rows × referenced-columns) scan with none.
    /// </summary>
    internal partial class TidalSqlStatementVisitor
    {
        /// <summary>Aggregate functions this fast path can evaluate in a single streaming pass.</summary>
        private static readonly HashSet<string> StreamableAggregates =
            new(StringComparer.OrdinalIgnoreCase) { "COUNT", "SUM", "MIN", "MAX", "AVG" };

        /// <summary>
        /// Attempts to answer a query with a single streaming aggregate pass. Returns false (and does
        /// nothing) when the query is not a whole-table aggregate over a single physical table, in
        /// which case the caller falls back to the general materializing path. On success the fully
        /// computed single-row result is returned via <paramref name="result"/>.
        /// </summary>
        private bool TryStreamingAggregate(QuerySpecification querySpec, Env? outer, out RowSet result)
        {
            result = null!;

            // Only an unfiltered, ungrouped aggregate over exactly one physical table qualifies.
            if (querySpec.GroupByClause != null ||
                querySpec.HavingClause != null ||
                querySpec.WhereClause != null ||
                querySpec.TopRowFilter != null ||
                querySpec.UniqueRowFilter == UniqueRowFilter.Distinct ||
                querySpec.FromClause == null ||
                querySpec.FromClause.TableReferences.Count != 1 ||
                querySpec.FromClause.TableReferences[0] is not NamedTableReference named)
            {
                return false;
            }

            // Exclude CTEs and every system/security catalog (all live under the sys schema).
            var baseName = named.SchemaObject.BaseIdentifier.Value;
            if (named.SchemaObject.SchemaIdentifier == null &&
                this.cteScope != null &&
                this.cteScope.ContainsKey(baseName))
            {
                return false;
            }

            if (string.Equals(named.SchemaObject.SchemaIdentifier?.Value, "sys", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Every select element must be a supported aggregate over '*' or a single plain column.
            var specs = new List<AggSpec>(querySpec.SelectElements.Count);
            foreach (var element in querySpec.SelectElements)
            {
                if (element is not SelectScalarExpression scalar ||
                    !TryDescribeAggregate(scalar, out var spec))
                {
                    return false;
                }

                specs.Add(spec);
            }

            if (specs.Count == 0)
            {
                return false;
            }

            var (schemaName, tableName) = this.ResolveTableName(named.SchemaObject);
            this.Authorize(Security.SecurityAction.Select, schemaName, tableName);

            var source = this.ResolveTableSource(schemaName, tableName);
            if (!source.Exists)
            {
                return false;
            }

            TableAccessStats.RecordQuery(this.RequireDatabasePath(), schemaName, tableName);

            using (TableLock.Read(source.LockKey))
            {
                if (!this.TryComputeStreamingAggregate(source.Files, specs))
                {
                    // A referenced column was missing from the file schema; let the general path
                    // produce its usual "column not found" style error rather than guessing here.
                    return false;
                }
            }

            var qualifier = named.Alias?.Value ?? baseName;
            var projSchema = specs.Select(s => new ColumnRef(string.Empty, s.OutputName)).ToList();
            var row = specs.Select(s => s.Finish()).ToArray();
            result = new RowSet(projSchema, new List<object?[]> { row });
            return true;
        }

        /// <summary>Classifies one select element as a streamable aggregate, or rejects it.</summary>
        private static bool TryDescribeAggregate(SelectScalarExpression scalar, out AggSpec spec)
        {
            spec = null!;

            if (scalar.Expression is not FunctionCall fn ||
                fn.OverClause != null ||
                !StreamableAggregates.Contains(fn.FunctionName.Value))
            {
                return false;
            }

            // COUNT(DISTINCT ...) / SUM(DISTINCT ...) need de-duplication and are not streamable here.
            if (fn.UniqueRowFilter == UniqueRowFilter.Distinct)
            {
                return false;
            }

            var name = fn.FunctionName.Value.ToUpperInvariant();
            var arg = fn.Parameters.FirstOrDefault();
            var isStar = arg is ColumnReferenceExpression { ColumnType: ColumnType.Wildcard };

            string? column = null;
            if (!isStar)
            {
                // The only supported argument shape is a single, plain column reference.
                if (arg is not ColumnReferenceExpression { ColumnType: ColumnType.Regular } colRef)
                {
                    return false;
                }

                column = colRef.MultiPartIdentifier.Identifiers.Last().Value;
            }
            else if (name != "COUNT")
            {
                // '*' is only valid for COUNT.
                return false;
            }

            var alias = scalar.ColumnName?.Value ?? "column";
            spec = new AggSpec(name, column, alias);
            return true;
        }

        /// <summary>
        /// Streams every part/row group, decoding only the referenced columns, and folds values into
        /// each spec's accumulators. Returns false if a referenced column is absent from the schema.
        /// </summary>
        private bool TryComputeStreamingAggregate(IReadOnlyList<string> files, List<AggSpec> specs)
        {
            // Columns actually needed (COUNT(*) needs none); grouped so each is decoded once per group.
            var neededColumns = specs
                .Where(s => s.Column != null)
                .Select(s => s.Column!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var filePath in files)
            {
                using Stream readStream = File.OpenRead(filePath);
                using var reader = ParquetReader.CreateAsync(readStream).GetAwaiter().GetResult();

                // Resolve each needed column name to its schema field (case-insensitive).
                var fieldByColumn = new Dictionary<string, Parquet.Schema.DataField>(StringComparer.OrdinalIgnoreCase);
                foreach (var column in neededColumns)
                {
                    var field = reader.Schema.DataFields.FirstOrDefault(f =>
                        string.Equals(f.Name, column, StringComparison.OrdinalIgnoreCase));
                    if (field == null)
                    {
                        return false;
                    }

                    fieldByColumn[column] = field;
                }

                for (var g = 0; g < reader.RowGroupCount; g++)
                {
                    this.ThrowIfCancelled();
                    using var groupReader = reader.OpenRowGroupReader(g);

                    var groupRows = groupReader.RowCount;
                    foreach (var spec in specs)
                    {
                        spec.RowCount += groupRows;
                    }

                    // Decode each needed column once, then feed it to every spec that references it.
                    foreach (var column in neededColumns)
                    {
                        var dataColumn = groupReader.ReadColumnAsync(fieldByColumn[column]).GetAwaiter().GetResult();
                        var values = dataColumn.Data;

                        foreach (var spec in specs)
                        {
                            if (spec.Column != null &&
                                string.Equals(spec.Column, column, StringComparison.OrdinalIgnoreCase))
                            {
                                for (var i = 0; i < values.Length; i++)
                                {
                                    spec.Accumulate(values.GetValue(i));
                                }
                            }
                        }
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Running state for one aggregate in the streaming pass. Semantics mirror
        /// <see cref="ComputeAggregate"/> exactly: NULLs are ignored by SUM/AVG/MIN/MAX and by
        /// COUNT(col); SUM is integer-typed only when every summed value is an integer; AVG divides a
        /// decimal total by the non-null count; MIN/MAX use the engine's ordering comparison.
        /// </summary>
        private sealed class AggSpec
        {
            private readonly string kind;
            private long nonNull;
            private decimal decimalSum;
            private bool allIntegral = true;
            private object? extreme;

            public AggSpec(string kind, string? column, string outputName)
            {
                this.kind = kind;
                this.Column = column;
                this.OutputName = outputName;
            }

            /// <summary>Column this aggregate reads, or null for COUNT(*).</summary>
            public string? Column { get; }

            public string OutputName { get; }

            /// <summary>Total rows scanned (feeds COUNT(*)).</summary>
            public long RowCount { get; set; }

            public void Accumulate(object? value)
            {
                if (value == null)
                {
                    return;
                }

                this.nonNull++;

                switch (this.kind)
                {
                    case "SUM":
                    case "AVG":
                        this.allIntegral = this.allIntegral && (value is int || value is long);
                        this.decimalSum += ToDecimal(value);
                        break;

                    case "MIN":
                        if (this.extreme == null || CompareForOrder(value, this.extreme) < 0)
                        {
                            this.extreme = value;
                        }

                        break;

                    case "MAX":
                        if (this.extreme == null || CompareForOrder(value, this.extreme) > 0)
                        {
                            this.extreme = value;
                        }

                        break;
                }
            }

            /// <summary>Produces the final scalar value for this aggregate.</summary>
            public object? Finish()
            {
                switch (this.kind)
                {
                    case "COUNT":
                        // COUNT(*) counts all rows; COUNT(col) counts non-null values.
                        var count = this.Column == null ? this.RowCount : this.nonNull;
                        return count <= int.MaxValue ? (int)count : count;

                    case "SUM":
                        if (this.nonNull == 0)
                        {
                            return null;
                        }

                        // Cast to object so the ternary does not unify long and decimal to decimal,
                        // which would re-box an integral SUM as System.Decimal.
                        return this.allIntegral ? (object)Convert.ToInt64(this.decimalSum) : this.decimalSum;

                    case "AVG":
                        return this.nonNull == 0 ? null : this.decimalSum / this.nonNull;

                    case "MIN":
                    case "MAX":
                        return this.extreme;

                    default:
                        throw new NotSupportedException($"Unsupported aggregate function: {this.kind}");
                }
            }
        }
    }
}
