namespace TidalSqlClientDemo
{
    using System;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;

    /// <summary>
    /// A minimal console client that connects to the TidalSql TDS server with the standard
    /// <see cref="SqlConnection"/> from Microsoft.Data.SqlClient and runs a query.
    ///
    /// <para>TidalSql Milestone 1 speaks unencrypted TDS, so the connection must use
    /// <c>Encrypt=False;TrustServerCertificate=True</c>. Start the server first with
    /// <c>dotnet run --project TidalSqlServer</c> (it listens on port 1433 by default).</para>
    /// </summary>
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var options = DemoOptions.Parse(args);

            var builder = new SqlConnectionStringBuilder
            {
                DataSource = $"{options.Host},{options.Port}",
                UserID = options.User,
                Password = options.Password,
                InitialCatalog = options.Database,
                Encrypt = SqlConnectionEncryptOption.Optional, // Milestone 1 is unencrypted.
                TrustServerCertificate = true,
                ConnectTimeout = 360,
                Pooling = false,
            };

            Console.WriteLine($"Connecting to TidalSql at {options.Host}:{options.Port} as '{options.User}'...");

            try
            {
                await using var connection = new SqlConnection(builder.ConnectionString);
                await connection.OpenAsync();
                Console.WriteLine($"Connected. Current database: {connection.Database}");
                Console.WriteLine();

                await RunQueryAsync(connection, options.Query);
                return 0;
            }
            catch (SqlException ex)
            {
                Console.Error.WriteLine($"SQL error {ex.Number}: {ex.Message}");
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Connection failed: {ex.Message}");
                return 2;
            }
        }

        private static async Task RunQueryAsync(SqlConnection connection, string sql)
        {
            Console.WriteLine($"> {sql}");
            Console.WriteLine();

            await using var command = new SqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();

            do
            {
                if (reader.FieldCount == 0)
                {
                    continue;
                }

                // Header row.
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    Console.Write(reader.GetName(i));
                    Console.Write(i == reader.FieldCount - 1 ? Environment.NewLine : " | ");
                }

                // Data rows.
                var rows = 0;
                while (await reader.ReadAsync())
                {
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        var value = await reader.IsDBNullAsync(i) ? "NULL" : reader.GetValue(i)?.ToString();
                        Console.Write(value);
                        Console.Write(i == reader.FieldCount - 1 ? Environment.NewLine : " | ");
                    }

                    rows++;
                }

                Console.WriteLine();
                Console.WriteLine($"({rows} row(s) returned)");
            }
            while (await reader.NextResultAsync());
        }
    }

    /// <summary>Parses the demo's command-line options, falling back to sensible defaults.</summary>
    internal sealed class DemoOptions
    {
        public string Host { get; private set; } = "127.0.0.1";

        public int Port { get; private set; } = 1433;

        public string User { get; private set; } = "admin";

        public string Password { get; private set; } = "admin";

        public string Database { get; private set; } = "master";

        public string Query { get; private set; } = "SELECT 1 AS Answer;";

        public static DemoOptions Parse(string[] args)
        {
            var options = new DemoOptions();
            for (var i = 0; i + 1 < args.Length; i += 2)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--host": options.Host = args[i + 1]; break;
                    case "--port": options.Port = int.Parse(args[i + 1]); break;
                    case "--user": options.User = args[i + 1]; break;
                    case "--password": options.Password = args[i + 1]; break;
                    case "--database": options.Database = args[i + 1]; break;
                    case "--query": options.Query = args[i + 1]; break;
                }
            }

            return options;
        }
    }
}
