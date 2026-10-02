namespace TidalDbApi
{
    using System;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using TidalDbServer.Tds;

    /// <summary>
    /// Runs the TDS (SQL Server wire protocol) listener alongside the web host, so a single process
    /// serves both the web UI / REST API and native <c>Microsoft.Data.SqlClient</c> connections. The
    /// listen port defaults to 1433 and can be overridden with the <c>TIDALDB_TDS_PORT</c> environment
    /// variable; setting it to 0 disables the TDS endpoint.
    ///
    /// <para>Milestone 1 is unencrypted, so clients must connect with <c>Encrypt=False</c>.</para>
    /// </summary>
    public sealed class TdsHostedService : IHostedService
    {
        private readonly ILogger<TdsHostedService> logger;
        private TdsServer? server;

        public TdsHostedService(ILogger<TdsHostedService> logger) => this.logger = logger;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            var port = ResolvePort();
            if (port <= 0)
            {
                this.logger.LogInformation("TDS endpoint disabled (TIDALDB_TDS_PORT={Port}).", port);
                return Task.CompletedTask;
            }

            try
            {
                this.server = new TdsServer();
                this.server.Start(new IPEndPoint(IPAddress.Any, port));
                this.logger.LogInformation(
                    "TDS server listening on port {Port} (Encrypt=False). Connect with Microsoft.Data.SqlClient.",
                    this.server.Endpoint.Port);
            }
            catch (Exception ex)
            {
                // A failed TDS bind (e.g. port in use) must not stop the web host from serving.
                this.logger.LogError(ex, "Failed to start the TDS server on port {Port}; continuing without it.", port);
                this.server?.Dispose();
                this.server = null;
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            this.server?.Dispose();
            this.server = null;
            return Task.CompletedTask;
        }

        private static int ResolvePort()
        {
            var env = Environment.GetEnvironmentVariable("TIDALDB_TDS_PORT");
            return int.TryParse(env, out var port) ? port : 1433;
        }
    }
}
