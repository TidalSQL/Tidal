namespace ParqBaseLib
{
    using System;
    using System.Collections.Generic;
    using Parquet;
    using Parquet.Schema;
    using Microsoft.SqlServer.TransactSql.ScriptDom;

    internal class CreateParquetTable
    {
        async public static Task CreateTable(string tableName, List<ColumnDefinition> columns)
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
                // Create a list to hold schema fields
                var fields = new List<Field>();

                // Loop through each column and create the corresponding field
                foreach (var column in columns)
                {
                    switch (column.DataType.Name.BaseIdentifier.Value.ToUpper())
                    {
                        case "INT":
                            fields.Add(new DataField<int>(column.ColumnIdentifier.Value));
                            break;
                        case "VARCHAR":
                        case "NVARCHAR":
                            fields.Add(new DataField<string>(column.ColumnIdentifier.Value));
                            break;
                        case "DATE":
                            fields.Add(new DataField<DateTime>(column.ColumnIdentifier.Value));
                            break;
                        case "MONEY":
                        case "DECIMAL":
                            fields.Add(new DataField<decimal>(column.ColumnIdentifier.Value));
                            break;
                        default:
                            throw new ArgumentException($"Unsupported data type: {column.ColumnIdentifier.Value}");
                    }

                };

                var schema = new ParquetSchema(fields);
                if (!File.Exists($"{tableName}.parquet"))
                {
                    Console.WriteLine($"Preparing to create table {tableName}.");
                    using (Stream fileStream = File.OpenWrite($"{tableName}.parquet"))
                    {
                        Console.WriteLine($"Creating Parquet table [{tableName}].");

                        using (ParquetWriter parquetWriter = await ParquetWriter.CreateAsync(schema, fileStream))
                        {
                            parquetWriter.CompressionMethod = CompressionMethod.Gzip;
                            parquetWriter.CompressionLevel = System.IO.Compression.CompressionLevel.Optimal;
                        }

                        Console.WriteLine($"Parquet table [{tableName}] created.");

                        fileStream.Flush();
                    }
                }
            }
            catch(Exception ex)
            {
                Console.WriteLine($"Failed to create table {tableName}. Error {ex.Message}");
            }
        }
    }
}
