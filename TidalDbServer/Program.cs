namespace TidalDbServer
{
    using System;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using TidalDbLib;
    using TidalDbServer.Tds;

    /// <summary>
    /// Hosts the TidalDb TDS endpoint so that Microsoft.Data.SqlClient applications and SQL Server
    /// Management Studio can connect and run text SQL batches. Encryption is negotiated per connection:
    /// clients that request it (SSMS, <c>Encrypt=Mandatory</c>) get a TLS session over a self-signed
    /// certificate, so they should pass <c>TrustServerCertificate=True</c>; clients that opt out connect
    /// in plaintext.
    /// </summary>
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            var port = ResolvePort(args);

            // Ensure the security catalog has an administrator so a fresh install can authenticate.
            var adminPassword = Environment.GetEnvironmentVariable("TIDALDB_ADMIN_PASSWORD");
            if (!string.IsNullOrEmpty(adminPassword))
            {
                var init = new TidalDb().InitializeServer(adminPassword);
                if (init.Created)
                {
                    Console.WriteLine($"Created default admin login '{init.AdminLogin}'.");
                }
            }

            using var server = new TdsServer();
            server.Start(new IPEndPoint(IPAddress.Any, port));
            Console.WriteLine($"TidalDb TDS server listening on port {server.Endpoint.Port} (TLS negotiated per connection).");

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

            Console.WriteLine("Shutting down TidalDb TDS server...");
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

            var env = Environment.GetEnvironmentVariable("TIDALDB_TDS_PORT");
            return int.TryParse(env, out var envPort) ? envPort : 1433;
        }
    }
}
