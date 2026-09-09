namespace ParqBaseLib
{
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.SqlServer.TransactSql.ScriptDom;
    using Parquet.Schema;
    using Parquet;
    using Parquet.Data;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;

    internal partial class ParqBaseStatementVisitor : TSqlFragmentVisitor
    {
        private string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        private IServiceProvider serviceProvider;
        private readonly TSql160Parser sqlParser = new TSql160Parser(true, SqlEngineType.All);
        public QueryResult LastResult { get; private set; } = new();

        public ParqBaseStatementVisitor(IServiceProvider provider)
        {
            this.serviceProvider = provider;
        }

        public QueryResult StartVisitor(string statement)
        {
            this.LastResult = new QueryResult();
            var tsqlFragment = (TSqlScript)this.sqlParser.Parse(new StringReader(statement), out IList<ParseError> parseErrors);
            tsqlFragment.Accept(this);
            return this.LastResult;
        }

        public override void Visit(CreateTableStatement node)
        {
            IList<ColumnDefinition>? list = [];
            foreach(var column in node.Definition.ColumnDefinitions)
            {
                list.Add(column);
            }

            var current = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory("tables");

            try
            {
                this.CreateTable(node.SchemaObjectName.BaseIdentifier.Value, list.ToList());
                this.LastResult.Message = $"Table '{node.SchemaObjectName.BaseIdentifier.Value}' created.";
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                this.LastResult.Message = ex.Message;
                this.LastResult.Success = false;
            }
            finally
            {
                Directory.SetCurrentDirectory(current);
            }
        }

        public override void Visit(CreateDatabaseStatement node)
        {
            Console.WriteLine("CreateDatabaseStatement");
            
            this.baseDirectory = $@"{baseDirectory}\databases";
            if (!Directory.Exists(this.baseDirectory))
            {
                Directory.CreateDirectory(this.baseDirectory);
            }

            var databaseDirectory = $@"{this.baseDirectory}\{node.DatabaseName.Value}";

            if (!Directory.Exists(databaseDirectory))
            {
                Directory.CreateDirectory(databaseDirectory);
            }

            Directory.SetCurrentDirectory(databaseDirectory);
            Console.WriteLine(this.baseDirectory);

            if (!Directory.Exists("tables"))
            {
                Directory.CreateDirectory("tables");
            }

            if (!Directory.Exists("views"))
            {
                Directory.CreateDirectory("views");
            }   

            if (!Directory.Exists("procedures"))
            {
                Directory.CreateDirectory("procedures");
            }

            if (!Directory.Exists("functions"))
            {
                Directory.CreateDirectory("functions");
            }

            if (!Directory.Exists("security"))
            {
                Directory.CreateDirectory("security");
            }

            if (!Directory.Exists("users"))
            {
                Directory.CreateDirectory("users");
            }

            this.LastResult.Message = $"Database '{node.DatabaseName.Value}' created.";
        }

        public override void Visit(UseStatement node)
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

            baseDirectory = $@"{baseDirectory}\databases";
            if (!Directory.Exists(baseDirectory))
            {
                throw new Exception("Databases directory does not exist");
            }

            var databaseDirectory = $@"{baseDirectory}\{node.DatabaseName.Value}";

            if (!Directory.Exists(databaseDirectory))
            {
                throw new Exception($"Database {node.DatabaseName.Value} does not exist");
            }

            Directory.SetCurrentDirectory(databaseDirectory);

            this.LastResult.Message = $"Changed database context to '{node.DatabaseName.Value}'.";
        }

        public override void Visit(InsertStatement node)
        {
            Console.WriteLine("InsertStatement");

            // Get column names
            var columnNames = new List<string>();
            foreach (var column in node.InsertSpecification.Columns)
            {
                columnNames.Add(column.MultiPartIdentifier.Identifiers[0].Value);
            }

            // Get row values
            var rowValues = ((ValuesInsertSource)node.InsertSpecification.InsertSource).RowValues[0].ColumnValues;
            var rowValuesList = new List<ScalarExpression>(rowValues);

            var tableReference = node.InsertSpecification.Target as NamedTableReference;
            if (tableReference != null)
            {
                var tableName = tableReference.SchemaObject.BaseIdentifier.Value;
                var currentDirectory = Directory.GetCurrentDirectory();
                Directory.SetCurrentDirectory("tables");
                try
                {
                    this.InsertIntoParquetFile($"{tableName}.parquet", tableName, columnNames, rowValuesList);
                    this.LastResult.Message = $"Row inserted into [{tableName}].";
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to insert into [{tableName}]. Error: {ex.Message}");
                    this.LastResult.Message = $"Failed to insert into [{tableName}]. Error: {ex.Message}";
                    this.LastResult.Success = false;
                }
                finally
                {
                    Directory.SetCurrentDirectory(currentDirectory);
                }
            }
        }

        public override void Visit(SelectStatement node)
        {
            Console.WriteLine("SelectStatement");

            var querySpec = node.QueryExpression as QuerySpecification;
            if (querySpec == null) return;

            // Get table name from FROM clause
            var tableRef = querySpec.FromClause.TableReferences[0] as NamedTableReference;
            if (tableRef == null) return;
            var tableName = tableRef.SchemaObject.BaseIdentifier.Value;

            // Determine selected columns
            var selectAll = false;
            var selectedColumns = new List<string>();
            foreach (var element in querySpec.SelectElements)
            {
                if (element is SelectStarExpression)
                {
                    selectAll = true;
                    break;
                }
                else if (element is SelectScalarExpression scalar &&
                         scalar.Expression is ColumnReferenceExpression colRef)
                {
                    selectedColumns.Add(colRef.MultiPartIdentifier.Identifiers.Last().Value);
                }
            }

            var currentDirectory = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory("tables");
            try
            {
                this.SelectFromParquetFile($"{tableName}.parquet", tableName, selectAll, selectedColumns);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to select from [{tableName}]. Error: {ex.Message}");
            }
            finally
            {
                Directory.SetCurrentDirectory(currentDirectory);
            }
        }

        private void CreateTable(string tableName, List<ColumnDefinition> columns)
        {
            if (string.IsNullOrEmpty(tableName))
            {
                throw new ArgumentException("Table name cannot be null or empty.", nameof(tableName));
            }

            if (columns == null || columns.Count == 0)
            {
                throw new ArgumentException("Columns must be provided.", nameof(columns));
            }

            try
            {
                var memoryCache = this.serviceProvider.GetService<ITableColumnCache>();

                // Create a list to hold schema fields
                var fields = new List<Field>();
                var cacheFields = new Dictionary<string, string>();

                // Loop through each column and create the corresponding field
                foreach (var column in columns)
                {
                    switch (column.DataType.Name.BaseIdentifier.Value.ToUpper())
                    {
                        case "INT":
                            fields.Add(new DataField<int>(column.ColumnIdentifier.Value));
                            cacheFields.Add(column.ColumnIdentifier.Value, "int");
                            break;
                        case "VARCHAR":
                        case "NVARCHAR":
                            fields.Add(new DataField<string>(column.ColumnIdentifier.Value));
                            cacheFields.Add(column.ColumnIdentifier.Value, "string");
                            break;
                        case "DATE":
                            fields.Add(new DataField<DateTime>(column.ColumnIdentifier.Value));
                            cacheFields.Add(column.ColumnIdentifier.Value, "datetime");
                            break;
                        case "MONEY":
                        case "DECIMAL":
                            fields.Add(new DataField<decimal>(column.ColumnIdentifier.Value));
                            cacheFields.Add(column.ColumnIdentifier.Value, "decimal");
                            break;
                        default:
                            throw new ArgumentException($"Unsupported data type: {column.ColumnIdentifier.Value}");
                    }

                };

                if (memoryCache != null)
                {
                    memoryCache.Add(tableName, cacheFields);
                }

                var schema = new ParquetSchema(fields);
                if (!File.Exists($"{tableName}.parquet"))
                {
                    Console.WriteLine($"Preparing to create table {tableName}.");
                    using (Stream fileStream = File.OpenWrite($"{tableName}.parquet"))
                    {
                        Console.WriteLine($"Creating Parquet table [{tableName}].");

                        Task.Run(async () =>
                        {
                            using (ParquetWriter parquetWriter = await ParquetWriter.CreateAsync(schema, fileStream))
                            {
                                parquetWriter.CompressionMethod = CompressionMethod.Gzip;
                                parquetWriter.CompressionLevel = System.IO.Compression.CompressionLevel.Optimal;
                            }
                        })
                        .Wait();

                        Console.WriteLine($"Parquet table [{tableName}] created.");
                        fileStream.Flush();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to create table {tableName}. Error {ex.Message}");
            }
        }

        private void InsertIntoParquetFile(string filePath, string tableName, List<string> columnNames, List<ScalarExpression> values)
        {
            var cache = this.serviceProvider.GetService<ITableColumnCache>();
            if (cache == null)
            {
                throw new Exception("Table column cache is not available.");
            }

            var columnTypes = cache.Get(tableName);

            // Initialize column data storage for all table columns
            var columnData = new Dictionary<string, List<object>>();
            foreach (var kvp in columnTypes)
            {
                columnData[kvp.Key] = new List<object>();
            }

            // Read existing rows from parquet file
            if (File.Exists(filePath))
            {
                Task.Run(async () =>
                {
                    using (Stream readStream = File.OpenRead(filePath))
                    {
                        using (var reader = await ParquetReader.CreateAsync(readStream))
                        {
                            for (int g = 0; g < reader.RowGroupCount; g++)
                            {
                                using (var groupReader = reader.OpenRowGroupReader(g))
                                {
                                    foreach (var field in reader.Schema.DataFields)
                                    {
                                        var dc = await groupReader.ReadColumnAsync(field);
                                        if (columnData.ContainsKey(field.Name))
                                        {
                                            foreach (var val in dc.Data)
                                            {
                                                columnData[field.Name].Add(val);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }).Wait();
            }

            // Add new row values for specified columns
            for (int i = 0; i < columnNames.Count; i++)
            {
                var colName = columnNames[i];
                var colType = columnTypes[colName];
                columnData[colName].Add(this.ConvertSqlValue(values[i], colType));
            }

            // Build schema from cached column types
            var fields = new List<Parquet.Schema.Field>();
            foreach (var kvp in columnTypes)
            {
                switch (kvp.Value)
                {
                    case "int":
                        fields.Add(new DataField<int>(kvp.Key));
                        break;
                    case "string":
                        fields.Add(new DataField<string>(kvp.Key));
                        break;
                    case "datetime":
                        fields.Add(new DataField<DateTime>(kvp.Key));
                        break;
                    case "decimal":
                        fields.Add(new DataField<decimal>(kvp.Key));
                        break;
                }
            }

            var schema = new ParquetSchema(fields);

            // Write all data back to the parquet file
            Task.Run(async () =>
            {
                using (Stream writeStream = File.Create(filePath))
                {
                    using (var writer = await ParquetWriter.CreateAsync(schema, writeStream))
                    {
                        writer.CompressionMethod = CompressionMethod.Gzip;
                        writer.CompressionLevel = System.IO.Compression.CompressionLevel.Optimal;

                        using (var groupWriter = writer.CreateRowGroup())
                        {
                            foreach (var field in schema.DataFields)
                            {
                                var colType = columnTypes[field.Name];
                                var data = columnData[field.Name];

                                switch (colType)
                                {
                                    case "int":
                                        await groupWriter.WriteColumnAsync(
                                            new DataColumn(field, data.Cast<int>().ToArray()));
                                        break;
                                    case "string":
                                        await groupWriter.WriteColumnAsync(
                                            new DataColumn(field, data.Cast<string>().ToArray()));
                                        break;
                                    case "datetime":
                                        await groupWriter.WriteColumnAsync(
                                            new DataColumn(field, data.Cast<DateTime>().ToArray()));
                                        break;
                                    case "decimal":
                                        await groupWriter.WriteColumnAsync(
                                            new DataColumn(field, data.Cast<decimal>().ToArray()));
                                        break;
                                }
                            }
                        }
                    }
                }
            }).Wait();

            Console.WriteLine($"Row inserted into [{tableName}].");
        }

        private void SelectFromParquetFile(string filePath, string tableName, bool selectAll, List<string> selectedColumns)
        {
            if (!File.Exists(filePath))
            {
                throw new Exception($"Table [{tableName}] does not exist.");
            }

            // Read all column data from the parquet file
            var columnData = new Dictionary<string, List<object>>();
            var columnOrder = new List<string>();
            int rowCount = 0;

            Task.Run(async () =>
            {
                using (Stream readStream = File.OpenRead(filePath))
                {
                    using (var reader = await ParquetReader.CreateAsync(readStream))
                    {
                        foreach (var field in reader.Schema.DataFields)
                        {
                            columnData[field.Name] = new List<object>();
                            columnOrder.Add(field.Name);
                        }

                        for (int g = 0; g < reader.RowGroupCount; g++)
                        {
                            using (var groupReader = reader.OpenRowGroupReader(g))
                            {
                                foreach (var field in reader.Schema.DataFields)
                                {
                                    var dc = await groupReader.ReadColumnAsync(field);
                                    foreach (var val in dc.Data)
                                    {
                                        columnData[field.Name].Add(val);
                                    }
                                }
                            }
                        }

                        if (columnOrder.Count > 0 && columnData[columnOrder[0]].Count > 0)
                        {
                            rowCount = columnData[columnOrder[0]].Count;
                        }
                    }
                }
            }).Wait();

            // Filter to selected columns
            var displayColumns = selectAll ? columnOrder : selectedColumns;

            // Calculate column widths for formatting
            var colWidths = new Dictionary<string, int>();
            foreach (var col in displayColumns)
            {
                int maxWidth = col.Length;
                if (columnData.ContainsKey(col))
                {
                    foreach (var val in columnData[col])
                    {
                        int valLen = (val?.ToString() ?? "NULL").Length;
                        if (valLen > maxWidth) maxWidth = valLen;
                    }
                }
                colWidths[col] = maxWidth;
            }

            // Print header
            var header = string.Join(" | ", displayColumns.Select(c => c.PadRight(colWidths[c])));
            Console.WriteLine(header);
            Console.WriteLine(new string('-', header.Length));

            // Print rows
            for (int i = 0; i < rowCount; i++)
            {
                var row = string.Join(" | ", displayColumns.Select(c =>
                {
                    var val = columnData.ContainsKey(c) && i < columnData[c].Count
                        ? columnData[c][i]?.ToString() ?? "NULL"
                        : "NULL";
                    return val.PadRight(colWidths[c]);
                }));
                Console.WriteLine(row);
            }

            Console.WriteLine($"\n({rowCount} row(s) returned)");

            // Populate query result
            this.LastResult.Columns = displayColumns.ToList();
            this.LastResult.RowCount = rowCount;
            this.LastResult.Message = $"({rowCount} row(s) returned)";
            for (int i = 0; i < rowCount; i++)
            {
                var row = new Dictionary<string, object?>();
                foreach (var col in displayColumns)
                {
                    row[col] = columnData.ContainsKey(col) && i < columnData[col].Count
                        ? columnData[col][i]
                        : null;
                }
                this.LastResult.Rows.Add(row);
            }
        }

        private object ConvertSqlValue(ScalarExpression expression, string targetType)
        {
            switch (targetType)
            {
                case "int":
                    if (expression is IntegerLiteral intLiteral)
                        return int.Parse(intLiteral.Value);
                    break;
                case "string":
                    if (expression is StringLiteral strLiteral)
                        return strLiteral.Value;
                    break;
                case "datetime":
                    if (expression is StringLiteral dateLiteral)
                        return DateTime.Parse(dateLiteral.Value);
                    break;
                case "decimal":
                    if (expression is IntegerLiteral decIntLiteral)
                        return decimal.Parse(decIntLiteral.Value);
                    if (expression is NumericLiteral numLiteral)
                        return decimal.Parse(numLiteral.Value);
                    if (expression is MoneyLiteral moneyLiteral)
                        return decimal.Parse(moneyLiteral.Value);
                    break;
            }
            throw new ArgumentException($"Cannot convert value to {targetType}.");
        }

    }
}
