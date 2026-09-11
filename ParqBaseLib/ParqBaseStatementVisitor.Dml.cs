namespace ParqBaseLib
{
    using Microsoft.SqlServer.TransactSql.ScriptDom;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    internal partial class ParqBaseStatementVisitor
    {
        private static readonly Env EmptyRowEnv = new(new List<ColumnRef>(), Array.Empty<object?>(), null);

        /// <summary>Columns eligible for a positional INSERT (excludes identity and computed columns).</summary>
        private List<string> InsertableColumns(TableData table, TableMeta? meta)
        {
            return table.Order.Where(c =>
            {
                var cm = meta?.Find(c);
                return cm == null || (!cm.IsIdentity && cm.ComputedSql == null);
            }).ToList();
        }

        private List<Dictionary<string, object?>> EvaluateValuesRows(
            ValuesInsertSource source,
            List<string> insertColumns,
            Dictionary<string, string> types)
        {
            var rows = new List<Dictionary<string, object?>>();
            foreach (var rowValue in source.RowValues)
            {
                if (rowValue.ColumnValues.Count != insertColumns.Count)
                {
                    throw new ArgumentException(
                        $"Column count ({insertColumns.Count}) does not match value count ({rowValue.ColumnValues.Count}).");
                }

                var provided = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < insertColumns.Count; i++)
                {
                    var expr = rowValue.ColumnValues[i];
                    provided[insertColumns[i]] = expr is NullLiteral
                        ? null
                        : this.GetScalarValue(expr, EmptyRowEnv);
                }

                rows.Add(provided);
            }

            return rows;
        }

        private List<Dictionary<string, object?>> EvaluateSelectRows(SelectInsertSource source, List<string> insertColumns)
        {
            var rowSet = this.EvaluateQueryExpression(source.Select, null);
            if (rowSet.Schema.Count != insertColumns.Count)
            {
                throw new ArgumentException(
                    $"INSERT column count ({insertColumns.Count}) does not match SELECT column count ({rowSet.Schema.Count}).");
            }

            var rows = new List<Dictionary<string, object?>>();
            foreach (var values in rowSet.Rows)
            {
                var provided = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < insertColumns.Count; i++)
                {
                    provided[insertColumns[i]] = values[i];
                }

                rows.Add(provided);
            }

            return rows;
        }

        /// <summary>
        /// Appends fully-materialized rows to a table, filling omitted columns from identity
        /// sequences, defaults or null, then evaluating computed columns. Persists the table and,
        /// when identity values advanced, the metadata sidecar.
        /// </summary>
        private int AppendRows(string filePath, TableData table, TableMeta? meta, List<Dictionary<string, object?>> providedRows)
        {
            foreach (var provided in providedRows)
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

                // Pass 1: non-computed columns.
                foreach (var col in table.Order)
                {
                    var cm = meta?.Find(col);
                    if (cm?.ComputedSql != null)
                    {
                        continue;
                    }

                    if (provided.TryGetValue(col, out var value))
                    {
                        row[col] = value == null ? null : ConvertToPhysical(value, table.Types[col]);
                    }
                    else if (cm?.IsIdentity == true)
                    {
                        cm.IdentityCurrent += cm.IdentityIncrement;
                        row[col] = (int)cm.IdentityCurrent;
                    }
                    else if (cm?.DefaultSql != null)
                    {
                        var def = this.GetScalarValue(this.ParseScalarExpression(cm.DefaultSql), EmptyRowEnv);
                        row[col] = def == null ? null : ConvertToPhysical(def, table.Types[col]);
                    }
                    else
                    {
                        row[col] = null;
                    }
                }

                this.ApplyComputedColumns(table, meta, row);

                foreach (var col in table.Order)
                {
                    table.Data[col].Add(row[col]);
                }
            }

            this.WriteTableAsync(filePath, table.Order, table.Types, table.Data).GetAwaiter().GetResult();
            if (meta != null)
            {
                SaveMeta(filePath, meta);
            }

            return providedRows.Count;
        }

        /// <summary>Evaluates computed (PERSISTED) columns from the other values in <paramref name="row"/>.</summary>
        private void ApplyComputedColumns(TableData table, TableMeta? meta, Dictionary<string, object?> row)
        {
            if (meta == null)
            {
                return;
            }

            foreach (var col in table.Order)
            {
                var cm = meta.Find(col);
                if (cm?.ComputedSql == null)
                {
                    continue;
                }

                var schema = table.Order.Select(c => new ColumnRef(string.Empty, c)).ToList();
                var values = table.Order.Select(c => row.TryGetValue(c, out var v) ? v : null).ToArray();
                var env = new Env(schema, values, null);
                var result = this.GetScalarValue(this.ParseScalarExpression(cm.ComputedSql), env);
                row[col] = result == null ? null : ConvertToPhysical(result, table.Types[col]);
            }
        }

        private static object? ConvertToPhysical(object? value, string type)
        {
            if (value == null)
            {
                return null;
            }

            return type switch
            {
                "int" => ToInt(value),
                "decimal" => ToDecimal(value),
                "datetime" => ToDateTime(value),
                "string" => Stringify(value),
                _ => throw new NotSupportedException($"Unsupported physical type: {type}"),
            };
        }

        // ---- UPDATE ------------------------------------------------------------

        public override void Visit(UpdateStatement node)
        {
            try
            {
                using var _ = this.EnterCteScope(node.WithCtesAndXmlNamespaces);
                var spec = node.UpdateSpecification;

                if (spec.Target is not NamedTableReference targetRef)
                {
                    throw new NotSupportedException("UPDATE target must be a table.");
                }

                var targetAlias = targetRef.Alias?.Value ?? targetRef.SchemaObject.BaseIdentifier.Value;

                // Resolve the physical table behind the target alias.
                var aliasMap = new Dictionary<string, (string Schema, string Table)>(StringComparer.OrdinalIgnoreCase);
                RowSet joined;
                if (spec.FromClause != null)
                {
                    joined = this.EvaluateTableReference(spec.FromClause.TableReferences[0], null);
                    for (var i = 1; i < spec.FromClause.TableReferences.Count; i++)
                    {
                        joined = this.LimitedCrossJoin(joined, this.EvaluateTableReference(spec.FromClause.TableReferences[i], null), null);
                    }

                    foreach (var tr in spec.FromClause.TableReferences)
                    {
                        this.CollectTableAliases(tr, aliasMap);
                    }
                }
                else
                {
                    joined = this.EvaluateTableReference(targetRef, null);
                    this.CollectTableAliases(targetRef, aliasMap);
                }

                if (!aliasMap.TryGetValue(targetAlias, out var targetName))
                {
                    throw new NotSupportedException($"Could not resolve UPDATE target '{targetAlias}' to a physical table.");
                }

                this.Authorize(Security.SecurityAction.Update, targetName.Schema, targetName.Table);
                var filePath = this.ResolveTableFilePath(targetName.Schema, targetName.Table);
                if (!File.Exists(filePath))
                {
                    throw new Exception($"Table [{targetName.Table}] does not exist.");
                }

                var table = this.LoadTableAsync(filePath).GetAwaiter().GetResult();
                var meta = LoadMeta(filePath);

                // Indices of the target table's columns within the joined schema (in table order).
                var targetColumnIndex = new List<int>();
                foreach (var col in table.Order)
                {
                    var idx = joined.Schema.FindIndex(c =>
                        string.Equals(c.Qualifier, targetAlias, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(c.Name, col, StringComparison.OrdinalIgnoreCase));
                    targetColumnIndex.Add(idx);
                }

                var setColumns = spec.SetClauses.OfType<AssignmentSetClause>()
                    .Select(s => s.Column.MultiPartIdentifier.Identifiers.Last().Value)
                    .ToList();

                // Map: target row key -> new SET values.
                var updates = new Dictionary<string, Dictionary<string, object?>>();
                foreach (var jrow in joined.Rows)
                {
                    if (spec.WhereClause != null &&
                        !this.EvaluateBoolean(spec.WhereClause.SearchCondition, new Env(joined.Schema, jrow, null)))
                    {
                        continue;
                    }

                    var key = TargetKey(jrow, targetColumnIndex);
                    var setValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var assignment in spec.SetClauses.OfType<AssignmentSetClause>())
                    {
                        var colName = assignment.Column.MultiPartIdentifier.Identifiers.Last().Value;
                        setValues[colName] = assignment.NewValue == null
                            ? null
                            : this.GetScalarValue(assignment.NewValue, new Env(joined.Schema, jrow, null));
                    }

                    updates[key] = setValues;
                }

                var updated = 0;
                for (var r = 0; r < table.RowCount; r++)
                {
                    var physicalKey = string.Join("\u0001", table.Order.Select(c => Stringify(table.Data[c][r])));
                    if (!updates.TryGetValue(physicalKey, out var setValues))
                    {
                        continue;
                    }

                    var row = table.Order.ToDictionary(
                        c => c,
                        c => table.Data[c][r],
                        StringComparer.OrdinalIgnoreCase);

                    foreach (var kvp in setValues)
                    {
                        row[kvp.Key] = kvp.Value == null ? null : ConvertToPhysical(kvp.Value, table.Types[kvp.Key]);
                    }

                    this.ApplyComputedColumns(table, meta, row);

                    foreach (var col in table.Order)
                    {
                        table.Data[col][r] = row[col];
                    }

                    updated++;
                }

                this.WriteTableAsync(filePath, table.Order, table.Types, table.Data).GetAwaiter().GetResult();
                this.LastResult.Message = $"{updated} row(s) updated in [{targetName.Table}].";
            }
            catch (Exception ex)
            {
                this.Fail(ex);
            }
        }

        private static string TargetKey(object?[] joinedRow, List<int> indices)
        {
            return string.Join("\u0001", indices.Select(i => i >= 0 ? Stringify(joinedRow[i]) : "\0"));
        }

        /// <summary>Records alias -> (schema, table) for physical named tables in a FROM clause.</summary>
        private void CollectTableAliases(TableReference reference, Dictionary<string, (string Schema, string Table)> map)
        {
            switch (reference)
            {
                case NamedTableReference named:
                {
                    var baseName = named.SchemaObject.BaseIdentifier.Value;
                    var isCte = named.SchemaObject.SchemaIdentifier == null &&
                                this.cteScope != null && this.cteScope.ContainsKey(baseName);
                    if (isCte || IsSystemCatalog(named, "all_objects"))
                    {
                        return;
                    }

                    var alias = named.Alias?.Value ?? baseName;
                    map[alias] = this.ResolveTableName(named.SchemaObject);
                    break;
                }

                case QualifiedJoin qualified:
                    this.CollectTableAliases(qualified.FirstTableReference, map);
                    this.CollectTableAliases(qualified.SecondTableReference, map);
                    break;

                case UnqualifiedJoin unqualified:
                    this.CollectTableAliases(unqualified.FirstTableReference, map);
                    this.CollectTableAliases(unqualified.SecondTableReference, map);
                    break;

                case JoinParenthesisTableReference paren:
                    this.CollectTableAliases(paren.Join, map);
                    break;
            }
        }
    }
}
