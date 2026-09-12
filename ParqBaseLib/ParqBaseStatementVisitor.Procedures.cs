namespace ParqBaseLib
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using Microsoft.SqlServer.TransactSql.ScriptDom;

    /// <summary>
    /// Persisted definition of a stored procedure. Stored as JSON in the database's
    /// <c>procedures</c> folder (&lt;name&gt;.json for dbo, &lt;schema&gt;.&lt;name&gt;.json otherwise).
    /// The body is the regenerated T-SQL of the statements after AS, re-parsed and executed on EXEC.
    /// </summary>
    internal sealed class ProcedureMeta
    {
        public string Schema { get; set; } = SecurityCatalogDbo;
        public string Name { get; set; } = string.Empty;
        public List<ProcedureParamMeta> Parameters { get; set; } = new();
        public string Body { get; set; } = string.Empty;

        private const string SecurityCatalogDbo = "dbo";
    }

    internal sealed class ProcedureParamMeta
    {
        public string Name { get; set; } = string.Empty;
        public string? TypeSql { get; set; }
        public string? DefaultSql { get; set; }
        public bool IsOutput { get; set; }
    }

    internal partial class ParqBaseStatementVisitor
    {
        private static readonly JsonSerializerOptions ProcJsonOptions = new() { WriteIndented = true };

        // Batch/procedure-scoped scalar variables (@name). Swapped out for a fresh scope while a
        // procedure body runs so a procedure has its own local variables.
        private Dictionary<string, object?> variables = new(StringComparer.OrdinalIgnoreCase);

        // ---- variables ---------------------------------------------------------

        private object? ResolveVariable(string name)
        {
            if (this.variables.TryGetValue(name, out var value))
            {
                return value;
            }

            throw new Exception($"Must declare the scalar variable \"{name}\".");
        }

        public override void Visit(DeclareVariableStatement node)
        {
            try
            {
                foreach (var declaration in node.Declarations)
                {
                    object? initial = declaration.Value != null
                        ? this.GetScalarValue(declaration.Value, EmptyRowEnv)
                        : null;

                    if (initial != null && declaration.DataType != null)
                    {
                        initial = ConvertToType(initial, declaration.DataType);
                    }

                    this.variables[declaration.VariableName.Value] = initial;
                }

                this.LastResult.Message = string.Empty;
            }
            catch (Exception ex)
            {
                this.Fail(ex);
            }
        }

        public override void Visit(SetVariableStatement node)
        {
            try
            {
                var name = node.Variable.Name;
                if (!this.variables.ContainsKey(name))
                {
                    throw new Exception($"Must declare the scalar variable \"{name}\".");
                }

                if (node.AssignmentKind != AssignmentKind.Equals)
                {
                    throw new NotSupportedException(
                        "Only simple assignment (SET @var = <expr>) is supported. Use SET @x = @x + 1 instead of compound operators.");
                }

                if (node.Expression == null)
                {
                    throw new NotSupportedException("This form of SET is not supported.");
                }

                this.variables[name] = this.GetScalarValue(node.Expression, EmptyRowEnv);
                this.LastResult.Message = string.Empty;
            }
            catch (Exception ex)
            {
                this.Fail(ex);
            }
        }

        // ---- CREATE / ALTER / DROP PROCEDURE -----------------------------------

        public override void ExplicitVisit(CreateProcedureStatement node)
        {
            // Override ExplicitVisit (not Visit) so ScriptDom does NOT recurse into the
            // procedure body and execute its statements at definition time.
            try
            {
                this.SaveProcedure(node.ProcedureReference.Name, node.Parameters, node.StatementList, overwrite: false, verb: "created");
            }
            catch (Exception ex)
            {
                this.Fail(ex);
            }
        }

        public override void ExplicitVisit(AlterProcedureStatement node)
        {
            try
            {
                this.SaveProcedure(node.ProcedureReference.Name, node.Parameters, node.StatementList, overwrite: true, verb: "altered");
            }
            catch (Exception ex)
            {
                this.Fail(ex);
            }
        }

        public override void ExplicitVisit(CreateOrAlterProcedureStatement node)
        {
            // CREATE OR ALTER: save regardless of whether the procedure already exists.
            try
            {
                var exists = File.Exists(this.ProcedureFilePathFor(node.ProcedureReference.Name));
                this.SaveProcedure(node.ProcedureReference.Name, node.Parameters, node.StatementList, overwrite: true, verb: exists ? "altered" : "created", requireExisting: false);
            }
            catch (Exception ex)
            {
                this.Fail(ex);
            }
        }

        public override void Visit(DropProcedureStatement node)
        {
            try
            {
                foreach (var name in node.Objects)
                {
                    var (schema, procName) = this.ResolveTableName(name);
                    var path = this.ProcedureFilePath(schema, procName);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                    else if (!node.IsIfExists)
                    {
                        throw new Exception($"Cannot drop the procedure '{procName}', because it does not exist.");
                    }
                }

                this.LastResult.Message = "Procedure dropped.";
            }
            catch (Exception ex)
            {
                this.Fail(ex);
            }
        }

        private void SaveProcedure(SchemaObjectName name, IList<ProcedureParameter> parameters, StatementList body, bool overwrite, string verb, bool requireExisting = true)
        {
            var (schema, procName) = this.ResolveTableName(name);
            var directory = Path.Combine(this.RequireDatabasePath(), "procedures");
            Directory.CreateDirectory(directory);

            var path = this.ProcedureFilePath(schema, procName);
            if (!overwrite && File.Exists(path))
            {
                throw new Exception($"Procedure [{procName}] already exists.");
            }

            if (overwrite && requireExisting && !File.Exists(path))
            {
                throw new Exception($"Cannot alter the procedure '{procName}', because it does not exist.");
            }

            var meta = new ProcedureMeta
            {
                Schema = schema,
                Name = procName,
                Parameters = (parameters ?? new List<ProcedureParameter>()).Select(p => new ProcedureParamMeta
                {
                    Name = p.VariableName.Value,
                    TypeSql = p.DataType != null ? GenerateSql(p.DataType) : null,
                    DefaultSql = p.Value != null ? GenerateSql(p.Value) : null,
                    IsOutput = p.Modifier == ParameterModifier.Output,
                }).ToList(),
                Body = GenerateBody(body),
            };

            File.WriteAllText(path, JsonSerializer.Serialize(meta, ProcJsonOptions));
            this.LastResult.Message = $"Procedure '{procName}' {verb}.";
        }

        // ---- EXECUTE -----------------------------------------------------------

        public override void Visit(ExecuteStatement node)
        {
            try
            {
                if (node.ExecuteSpecification?.ExecutableEntity is not ExecutableProcedureReference procRef)
                {
                    throw new NotSupportedException("Only EXECUTE of a stored procedure is supported.");
                }

                var name = procRef.ProcedureReference?.ProcedureReference?.Name
                    ?? throw new NotSupportedException("Could not resolve the procedure name.");

                var (schema, procName) = this.ResolveTableName(name);
                var path = this.ProcedureFilePath(schema, procName);
                if (!File.Exists(path))
                {
                    throw new Exception($"Could not find stored procedure '{procName}'.");
                }

                var meta = JsonSerializer.Deserialize<ProcedureMeta>(File.ReadAllText(path), ProcJsonOptions)
                    ?? throw new Exception($"Procedure '{procName}' could not be loaded.");

                this.ExecuteProcedure(meta, procRef.Parameters);
            }
            catch (Exception ex)
            {
                this.Fail(ex);
            }
        }

        private void ExecuteProcedure(ProcedureMeta meta, IList<ExecuteParameter> arguments)
        {
            var callerScope = this.variables;
            var scope = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var provided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var outputBindings = new List<(string ProcParam, string CallerVar)>();

            var args = arguments ?? new List<ExecuteParameter>();
            var named = args.Any(a => a.Variable != null);

            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                ProcedureParamMeta param;
                if (named)
                {
                    if (arg.Variable == null)
                    {
                        throw new Exception("Cannot mix positional and named arguments in one EXECUTE.");
                    }

                    param = meta.Parameters.FirstOrDefault(
                        p => string.Equals(p.Name, arg.Variable.Name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new Exception($"Procedure '{meta.Name}' has no parameter named '{arg.Variable.Name}'.");
                }
                else
                {
                    if (i >= meta.Parameters.Count)
                    {
                        throw new Exception($"Procedure '{meta.Name}' expects {meta.Parameters.Count} parameter(s), but more were supplied.");
                    }

                    param = meta.Parameters[i];
                }

                scope[param.Name] = this.GetScalarValue(arg.ParameterValue, EmptyRowEnv);
                provided.Add(param.Name);

                if (arg.IsOutput && arg.ParameterValue is VariableReference callerVar)
                {
                    outputBindings.Add((param.Name, callerVar.Name));
                }
            }

            foreach (var param in meta.Parameters)
            {
                if (provided.Contains(param.Name))
                {
                    continue;
                }

                if (param.DefaultSql != null)
                {
                    scope[param.Name] = this.GetScalarValue(this.ParseScalarExpression(param.DefaultSql), EmptyRowEnv);
                }
                else
                {
                    throw new Exception($"Procedure '{meta.Name}' expects parameter '{param.Name}', which was not supplied.");
                }
            }

            this.variables = scope;
            QueryResult? lastWithColumns = null;
            var final = new QueryResult { Message = string.Empty };
            try
            {
                var fragment = this.sqlParser.Parse(new StringReader(meta.Body), out IList<ParseError> errors);
                if (errors != null && errors.Count > 0)
                {
                    throw new Exception("Procedure body parse error: " + errors[0].Message);
                }

                if (fragment is TSqlScript script)
                {
                    foreach (var batch in script.Batches)
                    {
                        foreach (var statement in batch.Statements)
                        {
                            this.LastResult = new QueryResult();
                            statement.Accept(this);
                            final = this.LastResult;

                            if (!this.LastResult.Success)
                            {
                                throw new Exception(this.LastResult.Message);
                            }

                            if (this.LastResult.Columns.Count > 0)
                            {
                                lastWithColumns = this.LastResult;
                            }
                        }
                    }
                }
            }
            finally
            {
                foreach (var (procParam, callerVar) in outputBindings)
                {
                    if (callerScope.ContainsKey(callerVar) && scope.TryGetValue(procParam, out var value))
                    {
                        callerScope[callerVar] = value;
                    }
                }

                this.variables = callerScope;
            }

            this.LastResult = lastWithColumns ?? final;
        }

        // ---- helpers -----------------------------------------------------------

        private string ProcedureFilePath(string schema, string name)
        {
            return Path.Combine(this.RequireDatabasePath(), "procedures", ProcedureFileName(schema, name));
        }

        /// <summary>File name a procedure is persisted under (dbo is flat, other schemas are prefixed).</summary>
        internal static string ProcedureFileName(string schema, string name) =>
            string.Equals(schema, Security.SecurityCatalog.DboSchema, StringComparison.OrdinalIgnoreCase)
                ? $"{name}.json"
                : $"{schema}.{name}.json";

        /// <summary>
        /// Reconstructs a CREATE OR ALTER PROCEDURE definition (header + parameters + body) from a
        /// persisted procedure JSON file, for tooling such as the web UI "script as definition".
        /// </summary>
        internal static string BuildProcedureDefinition(string procFilePath)
        {
            var meta = JsonSerializer.Deserialize<ProcedureMeta>(File.ReadAllText(procFilePath), ProcJsonOptions)
                ?? throw new Exception("Procedure definition could not be loaded.");

            var builder = new StringBuilder();
            builder.Append("CREATE OR ALTER PROCEDURE ").Append(meta.Schema).Append('.').Append(meta.Name);

            for (var i = 0; i < meta.Parameters.Count; i++)
            {
                var p = meta.Parameters[i];
                builder.AppendLine(i == 0 ? string.Empty : ",");
                builder.Append("    ").Append(p.Name);
                if (!string.IsNullOrWhiteSpace(p.TypeSql))
                {
                    builder.Append(' ').Append(p.TypeSql);
                }

                if (!string.IsNullOrWhiteSpace(p.DefaultSql))
                {
                    builder.Append(" = ").Append(p.DefaultSql);
                }

                if (p.IsOutput)
                {
                    builder.Append(" OUTPUT");
                }
            }

            builder.AppendLine();
            builder.AppendLine("AS");
            builder.Append(meta.Body.TrimEnd());
            return builder.ToString();
        }

        private string ProcedureFilePathFor(SchemaObjectName name)
        {
            var (schema, procName) = this.ResolveTableName(name);
            return this.ProcedureFilePath(schema, procName);
        }

        private static string GenerateBody(StatementList? body)
        {
            if (body == null)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var statement in body.Statements)
            {
                builder.AppendLine(GenerateSql(statement) + ";");
            }

            return builder.ToString();
        }

        private RowSet BuildProceduresRowSet(string qualifier)
        {
            var dbPath = this.RequireDatabasePath();
            var procedures = new List<(string Name, string Type)>();
            CollectObjects(dbPath, "procedures", "*.json", "procedure", procedures);

            var schema = new List<ColumnRef> { new(qualifier, "name") };
            var rows = procedures
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p => new object?[] { p.Name })
                .ToList();
            return new RowSet(schema, rows);
        }
    }
}
