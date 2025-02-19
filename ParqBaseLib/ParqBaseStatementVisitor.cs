namespace ParqBaseLib
{
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.SqlServer.TransactSql.ScriptDom;
    using Parquet.Schema;
    using Parquet;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using ParquetSharp;

    internal partial class ParqBaseStatementVisitor : TSqlFragmentVisitor
    {
        private string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        private ServiceProvider serviceProvider;
        private readonly TSql160Parser sqlParser = new TSql160Parser(true, SqlEngineType.All);

        public ParqBaseStatementVisitor(ServiceProvider provider)
        {
            this.serviceProvider = provider;
        }

        public void StartVisitor(string statement)
        {
            var tsqlFragment = (TSqlScript)this.sqlParser.Parse(new StringReader(statement), out IList<ParseError> parseErrors);
            tsqlFragment.Accept(this);
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
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
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
        }

        public override void Visit(InsertStatement node)
        {
            Console.WriteLine("InsertStatement");
            var insertDictionary = new Dictionary<ColumnReferenceExpression, ScalarExpression>();


            // Get column names
            var columnName = new List<string>();
            var columnValues = node.InsertSpecification.Columns;
            if (columnValues != null)
            {
                foreach (var column in columnValues)
                {
                    columnName.Add(column.MultiPartIdentifier.Identifiers[0].Value);
                }
            }

            // Get column values
            var rowValuesList = new List<ScalarExpression>();
            var rowValues = ((ValuesInsertSource)node.InsertSpecification.InsertSource).RowValues[0].ColumnValues;
            if (rowValues != null)
            {
                foreach (var row in rowValues)
                {
                    rowValuesList.Add(row);
                }
            }

            var tableReference = node.InsertSpecification.Target as NamedTableReference;

            if (tableReference != null)
            {
                var currentDirectory = Directory.GetCurrentDirectory();

                Directory.SetCurrentDirectory("tables");
                this.ReadParquetFile($"{tableReference.SchemaObject.BaseIdentifier.Value}.parquet");
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

        private List<Dictionary<string, object>> ReadParquetFile(string filePath)
        {
            var rows = new List<Dictionary<string, object>>();
            var cache = this.serviceProvider.GetService<ITableColumnCache>();

            using (var fileReader = new ParquetFileReader(filePath))
            {
                if (fileReader.FileMetaData.NumRowGroups == 0)
                {
                    var schema = new ParquetSchema(
                        new DataField<int>("Id"),
                        new DataField<string>("Name"),
                        new DataField<int>("Age"));

                    /*
                    using (var writer = new ParquetWriter(schema, filePath))
                    {
                        writer.Close();
                    }
                    */

                    // Open a MemoryStream to hold the data (if working in memory)
                    using (var memoryStream = new MemoryStream())
                    {

                        /*
                       using ( var writer = new ParquetFileWriter(filePath, schema))
                       {

                       }


                           using (var fileWriter = new ParquetFileWriter(memoryStream, columns))
                           {
                           using (var rowGroupWriter = fileWriter.AppendRowGroup())
                           {
                               foreach (var column in new[] { "Id", "Name", "Age" })
                               {
                                   var values = newData.Select(row => row[column]).ToArray();
                                   using (var columnWriter = rowGroupWriter.NextColumn().LogicalWriter<object>())
                                   {
                                       columnWriter.WriteBatch(values);
                                   }
                               }
                           }
                       fileWriter.Close();
                       }

                       // If the file is empty, write the memory stream to a new file
                       /*
                       if (isFileEmpty)
                       {
                           File.WriteAllBytes(filePath, memoryStream.ToArray());
                       }
                       else
                       {
                           // Merge old and new data (overwrite or save separately)
                           var oldData = ReadParquetFile(filePath);
                           oldData.AddRange(newData);

                           WriteParquetFile(filePath, oldData);
                       }
                       */
                    }
                }
                else
                {
                    using (var rowGroupReader = fileReader.RowGroup(0)) // Read first row group
                    {
                        /*
                        var schema = fileReader.Fields;
                        foreach (var field in schema)
                        {
                            using (var columnReader = rowGroupReader.Column(field.Index))
                            {
                                object[] values = columnReader.ReadAll<object>(); // Read column data
                                for (int i = 0; i < values.Length; i++)
                                {
                                    if (rows.Count <= i)
                                        rows.Add(new Dictionary<string, object>());

                                    rows[i][field.Name] = values[i];
                                }
                            }
                        }
                        */
                    }
                }
            }

            return rows;
        }

    }
}
