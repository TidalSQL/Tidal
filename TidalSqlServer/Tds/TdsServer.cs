namespace TidalSqlServer.Tds
{
    using System;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Accepts TDS client connections on a TCP endpoint and services each on its own task. Exposes the
    /// bound endpoint after <see cref="Start"/> so tests can listen on an ephemeral port (port 0) and
    /// discover the actual port that was assigned.
    /// </summary>
    public sealed class TdsServer : IDisposable
    {
        private readonly string serverName;
        private TcpListener? listener;
        private CancellationTokenSource? cts;
        private Task? acceptLoop;

        public TdsServer(string? serverName = null) =>
            this.serverName = serverName ?? Environment.MachineName;

        /// <summary>The endpoint the listener is bound to; valid only after <see cref="Start"/>.</summary>
        public IPEndPoint Endpoint =>
            (IPEndPoint)(this.listener?.LocalEndpoint ?? throw new InvalidOperationException("Server not started."));

        /// <summary>Binds and begins accepting connections. Use port 0 to bind an ephemeral port.</summary>
        public void Start(IPEndPoint endpoint)
        {
            if (this.listener is not null)
            {
                throw new InvalidOperationException("Server already started.");
            }

            this.cts = new CancellationTokenSource();
            this.listener = new TcpListener(endpoint);
            this.listener.Start();
            this.acceptLoop = AcceptLoopAsync(this.cts.Token);
        }

        public void Stop()
        {
            this.cts?.Cancel();
            this.listener?.Stop();
            try
            {
                this.acceptLoop?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // Accept loop faults with an expected socket/cancellation error on shutdown.
            }
        }

        public void Dispose()
        {
            Stop();
            this.cts?.Dispose();
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            var server = this.listener!;
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await server.AcceptTcpClientAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }

                _ = ServeClientAsync(client, ct);
            }
        }

        private async Task ServeClientAsync(TcpClient client, CancellationToken ct)
        {
            try
            {
                var connection = new TdsConnection(client, this.serverName);
                await connection.HandleAsync(ct);
            }
            catch (Exception)
            {
                // A single misbehaving or disconnected client must not take down the listener.
            }
        }
    }
}
