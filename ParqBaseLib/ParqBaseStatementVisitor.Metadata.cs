namespace ParqBaseLib
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using Microsoft.SqlServer.TransactSql.ScriptDom;
    using Parquet;

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

        /// <summary>
        /// Declared SQL type name as written in CREATE TABLE (for example "varchar",
        /// "datetime2", "int"). Null for legacy tables created before this was captured.
        /// </summary>
        public string? SqlType { get; set; }

        /// <summary>Declared length for character/binary types; -1 for MAX; 0 when not applicable.</summary>
        public int Length { get; set; }

        /// <summary>Declared numeric precision, or datetime fractional-seconds source; 0 when not applicable.</summary>
        public int DeclaredPrecision { get; set; }

        /// <summary>Declared numeric/datetime scale; 0 when not applicable.</summary>
        public int DeclaredScale { get; set; }
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

        /// <summary>Public-facade accessor for a table's metadata sidecar path (schema-change tracking).</summary>
        internal static string MetaSidecarPath(string tableFilePath) => MetaFilePath(tableFilePath);

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

        /// <summary>Internal accessor so the public facade can read a table's column metadata.</summary>
        internal static TableMeta? LoadTableMeta(string tableFilePath) => LoadMeta(tableFilePath);

        /// <summary>
        /// Returns a table's metadata sidecar, creating and persisting one from the Parquet schema
        /// when it does not yet exist (for example a raw Parquet file imported into a database).
        /// This upgrades a "legacy" table to a first-class one so later reads are consistent.
        /// </summary>
        internal static TableMeta EnsureTableMeta(string tableFilePath)
        {
            var meta = LoadMeta(tableFilePath);
            if (meta != null)
            {
                return meta;
            }

            meta = new TableMeta { Columns = ReadSchemaColumns(tableFilePath).ToList() };
            SaveMeta(tableFilePath, meta);
            return meta;
        }

        /// <summary>
        /// Derives column metadata straight from a table's Parquet schema, used as a fallback for
        /// tables that have no metadata sidecar (for example raw Parquet files imported into a
        /// database). Physical storage types are inferred from the schema's CLR types.
        /// </summary>
        internal static IReadOnlyList<ColumnMeta> ReadSchemaColumns(string tableFilePath)
        {
            using Stream stream = File.OpenRead(tableFilePath);
            using var reader = ParquetReader.CreateAsync(stream).GetAwaiter().GetResult();

            var columns = new List<ColumnMeta>();
            foreach (var field in reader.Schema.DataFields)
            {
                columns.Add(new ColumnMeta
                {
                    Name = field.Name,
                    Type = MapClrType(field.ClrType),
                    Nullable = field.IsNullable,
                });
            }

            return columns;
        }

        /// <summary>
        /// Counts the rows in a Parquet table by summing row-group counts, without materializing
        /// column data. Returns 0 for an empty (schema-only) file.
        /// </summary>
        internal static long CountRows(string tableFilePath)
        {
            using Stream stream = File.OpenRead(tableFilePath);
            using var reader = ParquetReader.CreateAsync(stream).GetAwaiter().GetResult();
            long total = 0;
            for (var g = 0; g < reader.RowGroupCount; g++)
            {
                using var groupReader = reader.OpenRowGroupReader(g);
                total += groupReader.RowCount;
            }

            return total;
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

        /// <summary>
        /// Records the column's declared SQL type (name plus length/precision/scale) onto the
        /// metadata so the catalog can report faithful type information later. MAX is stored as -1.
        /// </summary>
        private static void CaptureDeclaredType(DataTypeReference? dataType, ColumnMeta meta)
        {
            if (dataType?.Name?.BaseIdentifier?.Value is not string baseName)
            {
                return;
            }

            meta.SqlType = baseName.ToLowerInvariant();

            if (dataType is not ParameterizedDataTypeReference parameterized || parameterized.Parameters.Count == 0)
            {
                return;
            }

            var upper = baseName.ToUpperInvariant();
            var values = parameterized.Parameters;

            switch (upper)
            {
                case "CHAR" or "VARCHAR" or "NCHAR" or "NVARCHAR" or "BINARY" or "VARBINARY":
                    meta.Length = values[0] is MaxLiteral ? -1 : ParseIntLiteral(values[0]);
                    break;
                case "DECIMAL" or "NUMERIC":
                    meta.DeclaredPrecision = ParseIntLiteral(values[0]);
                    meta.DeclaredScale = values.Count > 1 ? ParseIntLiteral(values[1]) : 0;
                    break;
                case "DATETIME2" or "TIME" or "DATETIMEOFFSET" or "FLOAT":
                    meta.DeclaredScale = ParseIntLiteral(values[0]);
                    break;
            }
        }

        private static int ParseIntLiteral(Literal literal) =>
            int.TryParse(literal.Value, out var value) ? value : 0;

        /// <summary>
        /// Produces catalog-style type facts (display name, maximum length in bytes, precision and
        /// scale) for a column, approximating SQL Server's <c>sys.columns</c> semantics. Legacy
        /// columns without a declared SQL type fall back to their physical storage type.
        /// </summary>
        internal static (string DataType, int MaxLength, int Precision, int Scale) DescribeColumnType(ColumnMeta column)
        {
            var name = column.SqlType;
            if (string.IsNullOrEmpty(name))
            {
                // Legacy table: only the physical storage type is known.
                return column.Type switch
                {
                    "int" => ("int", 4, 10, 0),
                    "datetime" => ("datetime2", 8, 27, 7),
                    "decimal" => ("decimal", 9, 18, 0),
                    _ => ("varchar", -1, 0, 0),
                };
            }

            var upper = name.ToUpperInvariant();
            var length = column.Length;
            var scale = column.DeclaredScale;

            switch (upper)
            {
                case "CHAR" or "VARCHAR" or "BINARY" or "VARBINARY":
                {
                    var max = length == 0 ? 1 : length;
                    return ($"{name}({LengthText(length, 1)})", max, 0, 0);
                }

                case "NCHAR" or "NVARCHAR":
                {
                    var declared = length == 0 ? 1 : length;
                    var max = declared < 0 ? -1 : declared * 2;
                    return ($"{name}({LengthText(length, 1)})", max, 0, 0);
                }

                case "DECIMAL" or "NUMERIC":
                {
                    var precision = column.DeclaredPrecision == 0 ? 18 : column.DeclaredPrecision;
                    var storage = precision <= 9 ? 5 : precision <= 19 ? 9 : precision <= 28 ? 13 : 17;
                    return ($"{name}({precision},{scale})", storage, precision, scale);
                }

                case "DATETIME2":
                {
                    var s = scale == 0 ? 7 : scale;
                    var storage = s <= 2 ? 6 : s <= 4 ? 7 : 8;
                    return ($"{name}({s})", storage, 20 + s, s);
                }

                case "TIME":
                {
                    var s = scale == 0 ? 7 : scale;
                    var storage = s <= 2 ? 3 : s <= 4 ? 4 : 5;
                    return ($"{name}({s})", storage, 8 + s, s);
                }

                case "DATETIMEOFFSET":
                {
                    var s = scale == 0 ? 7 : scale;
                    var storage = s <= 2 ? 8 : s <= 4 ? 9 : 10;
                    return ($"{name}({s})", storage, 26 + s, s);
                }

                case "INT" or "INTEGER":
                    return ("int", 4, 10, 0);
                case "BIGINT":
                    return ("bigint", 8, 19, 0);
                case "SMALLINT":
                    return ("smallint", 2, 5, 0);
                case "TINYINT":
                    return ("tinyint", 1, 3, 0);
                case "BIT":
                    return ("bit", 1, 1, 0);
                case "FLOAT":
                    return ("float", 8, 53, 0);
                case "REAL":
                    return ("real", 4, 24, 0);
                case "MONEY":
                    return ("money", 8, 19, 4);
                case "SMALLMONEY":
                    return ("smallmoney", 4, 10, 4);
                case "DATE":
                    return ("date", 3, 10, 0);
                case "DATETIME":
                    return ("datetime", 8, 23, 3);
                case "SMALLDATETIME":
                    return ("smalldatetime", 4, 16, 0);
                case "UNIQUEIDENTIFIER":
                    return ("uniqueidentifier", 16, 0, 0);
                default:
                    return (name, 0, 0, 0);
            }
        }

        private static string LengthText(int length, int fallback) =>
            length < 0 ? "max" : (length == 0 ? fallback : length).ToString();
    }
}
