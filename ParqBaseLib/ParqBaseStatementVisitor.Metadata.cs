namespace ParqBaseLib
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using Microsoft.SqlServer.TransactSql.ScriptDom;

    /// <summary>
    /// Per-table column metadata that Parquet cannot express on its own: identity columns,
    /// column defaults, computed (persisted) columns and nullability. Stored as a JSON sidecar
    /// next to the table's Parquet file (&lt;table&gt;.parquet.meta.json). Tables created before
    /// metadata existed simply have no sidecar and are treated as plain nullable columns.
    /// </summary>
    internal sealed class ColumnMeta
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>Physical storage type: int, string, datetime or decimal.</summary>
        public string Type { get; set; } = "string";

        public bool Nullable { get; set; } = true;

        public bool IsIdentity { get; set; }

        public long IdentitySeed { get; set; }

        public long IdentityIncrement { get; set; }

        public long IdentityCurrent { get; set; }

        /// <summary>SQL text of a DEFAULT expression, or null.</summary>
        public string? DefaultSql { get; set; }

        /// <summary>SQL text of a computed-column expression, or null.</summary>
        public string? ComputedSql { get; set; }
    }

    internal sealed class TableMeta
    {
        public List<ColumnMeta> Columns { get; set; } = new();

        public ColumnMeta? Find(string name) =>
            this.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    internal partial class ParqBaseStatementVisitor
    {
        private static readonly JsonSerializerOptions MetaJsonOptions = new() { WriteIndented = true };

        private static string MetaFilePath(string tableFilePath) => tableFilePath + ".meta.json";

        /// <summary>Loads the sidecar metadata for a table, or null when none exists (legacy table).</summary>
        private static TableMeta? LoadMeta(string tableFilePath)
        {
            var path = MetaFilePath(tableFilePath);
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<TableMeta>(json, MetaJsonOptions);
        }

        private static void SaveMeta(string tableFilePath, TableMeta meta)
        {
            File.WriteAllText(MetaFilePath(tableFilePath), JsonSerializer.Serialize(meta, MetaJsonOptions));
        }

        // ---- SQL expression (de)serialization ----------------------------------

        private static readonly Sql160ScriptGenerator ScriptGen = new(new SqlScriptGeneratorOptions
        {
            IncludeSemicolons = false,
            NewLineBeforeFromClause = false,
        });

        /// <summary>Renders a scalar expression back to T-SQL text so it can be persisted.</summary>
        private static string GenerateSql(TSqlFragment fragment)
        {
            ScriptGen.GenerateScript(fragment, out var sql);
            return sql.Trim();
        }

        /// <summary>Parses a scalar expression from stored SQL text (wrapped in a throwaway SELECT).</summary>
        private ScalarExpression ParseScalarExpression(string sql)
        {
            if (this.parsedExpressionCache.TryGetValue(sql, out var cached))
            {
                return cached;
            }

            var fragment = this.sqlParser.Parse(new StringReader($"SELECT ({sql})"), out IList<ParseError> errors);
            if (errors != null && errors.Count > 0)
            {
                throw new Exception($"Could not parse stored expression '{sql}': {errors[0].Message}");
            }

            if (fragment is TSqlScript { Batches: { Count: > 0 } batches } &&
                batches[0].Statements.FirstOrDefault() is SelectStatement { QueryExpression: QuerySpecification qs } &&
                qs.SelectElements.FirstOrDefault() is SelectScalarExpression scalar)
            {
                this.parsedExpressionCache[sql] = scalar.Expression;
                return scalar.Expression;
            }

            throw new Exception($"Could not extract expression from '{sql}'.");
        }

        /// <summary>
        /// Maps a SQL type name to the engine's physical storage type. Unsupported types throw.
        /// BIT is stored as int (0/1); wide integer and character families collapse to int/string.
        /// </summary>
        private static string MapSqlType(string sqlTypeName) => sqlTypeName.ToUpperInvariant() switch
        {
            "INT" or "INTEGER" or "BIGINT" or "SMALLINT" or "TINYINT" or "BIT" => "int",
            "VARCHAR" or "NVARCHAR" or "CHAR" or "NCHAR" or "TEXT" or "NTEXT" or "SYSNAME" or "UNIQUEIDENTIFIER" => "string",
            "DATE" or "DATETIME" or "DATETIME2" or "SMALLDATETIME" or "TIME" or "DATETIMEOFFSET" => "datetime",
            "MONEY" or "SMALLMONEY" or "DECIMAL" or "NUMERIC" or "FLOAT" or "REAL" => "decimal",
            _ => throw new ArgumentException($"Unsupported data type: {sqlTypeName}"),
        };
    }
}
