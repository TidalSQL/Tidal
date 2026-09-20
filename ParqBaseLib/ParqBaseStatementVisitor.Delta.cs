using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet;

namespace ParqBaseLib
{
    internal partial class ParqBaseStatementVisitor
    {
        /// <summary>
        /// Loads a partitioned Delta snapshot into a single <see cref="TableData"/>. Data columns are
        /// read from each active Parquet file; partition columns (which Delta stores only in the log,
        /// not in the files) are synthesized per file from its recorded partition values, typed from
        /// the Delta schema. Column order follows the table's logical schema when available.
        /// </summary>
        private async Task<TableData> LoadDeltaAsync(DeltaSnapshot snapshot)
        {
            var data = new Dictionary<string, List<object?>>(StringComparer.Ordinal);
            var order = new List<string>();
            var types = new Dictionary<string, string>(StringComparer.Ordinal);
            var partitionSet = new HashSet<string>(snapshot.PartitionColumns, StringComparer.OrdinalIgnoreCase);

            var fileColumnNames = new List<string>();      // data columns physically present in the files
            var partitionColumnNames = new List<string>(); // partition columns to synthesize
            var initialized = false;

            foreach (var file in snapshot.Files)
            {
                this.ThrowIfCancelled();

                using Stream readStream = File.OpenRead(file.AbsolutePath);
                using var reader = await ParquetReader.CreateAsync(readStream);
                var physical = reader.Schema.DataFields;

                if (!initialized)
                {
                    var physicalNames = new HashSet<string>(physical.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
                    foreach (var field in physical)
                    {
                        fileColumnNames.Add(field.Name);
                        types[field.Name] = MapClrType(field.ClrType);
                    }

                    // Partition columns absent from the files must be projected from the log.
                    foreach (var pc in snapshot.PartitionColumns)
                    {
                        if (!physicalNames.Contains(pc))
                        {
                            partitionColumnNames.Add(pc);
                            types[pc] = DeltaPartitionType(snapshot, pc);
                        }
                    }

                    // Present columns in logical (schema) order when we have it; otherwise data then partitions.
                    if (snapshot.Schema.Count > 0)
                    {
                        foreach (var col in snapshot.Schema)
                        {
                            if (types.ContainsKey(col.Name) && !order.Contains(col.Name))
                            {
                                order.Add(col.Name);
                            }
                        }
                    }

                    foreach (var name in fileColumnNames.Concat(partitionColumnNames))
                    {
                        if (!order.Contains(name))
                        {
                            order.Add(name);
                        }
                    }

                    foreach (var name in order)
                    {
                        data[name] = new List<object?>();
                    }

                    initialized = true;
                }

                // Read this file's physical columns and track how many rows it contributed.
                var fileRowCount = 0;
                for (var g = 0; g < reader.RowGroupCount; g++)
                {
                    this.ThrowIfCancelled();
                    using var groupReader = reader.OpenRowGroupReader(g);
                    var groupRows = 0;
                    foreach (var field in physical)
                    {
                        if (!data.TryGetValue(field.Name, out var column))
                        {
                            continue;
                        }

                        var dc = await groupReader.ReadColumnAsync(field);
                        groupRows = dc.Data.Length;
                        foreach (var val in dc.Data)
                        {
                            column.Add(val);
                        }
                    }

                    fileRowCount += groupRows;
                }

                // Append the constant partition values for every row this file contributed.
                foreach (var pc in partitionColumnNames)
                {
                    file.PartitionValues.TryGetValue(pc, out var raw);
                    var value = ConvertPartitionValue(raw, types[pc]);
                    var column = data[pc];
                    for (var i = 0; i < fileRowCount; i++)
                    {
                        column.Add(value);
                    }
                }
            }

            if (!initialized)
            {
                return new TableData(data, order, types, 0);
            }

            var rowCount = order.Count > 0 ? data[order[0]].Count : 0;
            return new TableData(data, order, types, rowCount);
        }

        /// <summary>Maps a Delta partition column's declared type to the engine's storage bucket.</summary>
        private static string DeltaPartitionType(DeltaSnapshot snapshot, string columnName)
        {
            var col = snapshot.Schema.FirstOrDefault(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));
            var deltaType = col?.DeltaType ?? "string";

            if (deltaType.StartsWith("decimal", StringComparison.OrdinalIgnoreCase))
            {
                return "decimal";
            }

            return deltaType.ToLowerInvariant() switch
            {
                "long" or "integer" or "short" or "byte" => "int",
                "double" or "float" => "decimal",
                "boolean" => "int",
                "date" or "timestamp" or "timestamp_ntz" => "datetime",
                _ => "string",
            };
        }

        /// <summary>Converts a Delta partition value (always stored as a string) to a typed CLR value.</summary>
        private static object? ConvertPartitionValue(string? raw, string storageType)
        {
            if (raw == null)
            {
                return null;
            }

            switch (storageType)
            {
                case "int":
                    if (bool.TryParse(raw, out var b))
                    {
                        return b ? 1L : 0L;
                    }

                    return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : raw;

                case "decimal":
                    return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : raw;

                case "datetime":
                    return DateTime.TryParse(
                        raw,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var dt)
                        ? dt
                        : raw;

                default:
                    return raw;
            }
        }
    }
}
