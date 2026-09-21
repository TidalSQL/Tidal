namespace TidalSqlServer
{
    using System;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using TidalSqlLib;
    using TidalSqlServer.Tds;

    /// <summary>
    /// Hosts the TidalSql TDS endpoint so that Microsoft.Data.SqlClient applications can connect and
    /// run text SQL batches. Milestone 1 is unencrypted, so clients must use
    /// <c>Encrypt=False;TrustServerCertificate=True</c>.
    /// </summary>
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            var port = ResolvePort(args);

            // Ensure the security catalog has an administrator so a fresh install can authenticate.
            var adminPassword = Environment.GetEnvironmentVariable("TIDALSQL_ADMIN_PASSWORD");
            if (!string.IsNullOrEmpty(adminPassword))
            {
                var init = new TidalSql().InitializeServer(adminPassword);
                if (init.Created)
                {
                    Console.WriteLine($"Created default admin login '{init.AdminLogin}'.");
                }
            }

            using var server = new TdsServer();
            server.Start(new IPEndPoint(IPAddress.Any, port));
            Console.WriteLine($"TidalSql TDS server listening on port {server.Endpoint.Port} (Encrypt=False).");

            using var shutdown = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                shutdown.Cancel();
            };

            try
            {
                await Task.Delay(Timeout.Infinite, shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C: fall through to graceful shutdown.
            }

            Console.WriteLine("Shutting down TidalSql TDS server...");
            server.Stop();
        }

        private static int ResolvePort(string[] args)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if ((args[i] == "--port" || args[i] == "-p") && int.TryParse(args[i + 1], out var p))
                {
                    return p;
                }
            }

            var env = Environment.GetEnvironmentVariable("TIDALSQL_TDS_PORT");
            return int.TryParse(env, out var envPort) ? envPort : 1433;
        }
    }
}
