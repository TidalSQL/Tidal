namespace TidalSqlServer.Tds
{
    using System;
    using System.Buffers.Binary;
    using System.IO;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using TidalSqlLib;

    /// <summary>
    /// Drives one client connection through the TDS handshake and request loop: PRELOGIN, LOGIN7
    /// (authenticated against the TidalSql security catalog), then a loop that executes SQL batches
    /// and streams their results back as TDS token streams. Each connection owns its own
    /// <see cref="TidalSql"/> session, so logins and the current database are isolated per connection.
    /// Milestone 1 is unencrypted: the server advertises "encryption not supported", so clients must
    /// connect with <c>Encrypt=False</c>.
    /// </summary>
    internal sealed class TdsConnection
    {
        private readonly TcpClient client;
        private readonly string serverName;
        private int packetSize = 4096;

        public TdsConnection(TcpClient client, string serverName)
        {
            this.client = client;
            this.serverName = serverName;
        }

        public async Task HandleAsync(CancellationToken cancellationToken)
        {
            using (this.client)
            await using (var stream = this.client.GetStream())
            {
                var reader = new TdsPacketReader(stream);

                if (!await HandlePreLoginAsync(reader, stream, cancellationToken))
                {
                    return;
                }

                var engine = await HandleLoginAsync(reader, stream, cancellationToken);
                if (engine is null)
                {
                    return;
                }

                await RequestLoopAsync(engine, reader, stream, cancellationToken);
            }
        }

        private async Task<bool> HandlePreLoginAsync(TdsPacketReader reader, Stream stream, CancellationToken ct)
        {
            var message = await reader.ReadMessageAsync(ct);
            if (message is null || message.Type != TdsPacketType.PreLogin)
            {
                return false;
            }

            var writer = new TdsResponseWriter(stream, this.packetSize);
            writer.WriteBytes(PreLoginResponse.Build());
            await writer.CompleteAsync(ct);
            return true;
        }

        private async Task<TidalSql?> HandleLoginAsync(TdsPacketReader reader, Stream stream, CancellationToken ct)
        {
            var message = await reader.ReadMessageAsync(ct);
            if (message is null || message.Type != TdsPacketType.Login7)
            {
                return null;
            }

            Login7 login;
            try
            {
                login = Login7.Parse(message.Payload);
            }
            catch (ArgumentException)
            {
                return null;
            }

            this.packetSize = Math.Clamp(login.PacketSize, 512, 32767);

            var engine = new TidalSql();
            if (!engine.Login(login.UserName, login.Password))
            {
                var writer = new TdsResponseWriter(stream, this.packetSize);
                TdsTokens.WriteMessage(
                    writer,
                    isError: true,
                    number: 18456,
                    state: 1,
                    severity: 14,
                    message: $"Login failed for user '{login.UserName}'.",
                    serverName: this.serverName);
                TdsTokens.WriteDone(writer, (ushort)(TdsDoneStatus.Final | TdsDoneStatus.Error), 0, 0);
                await writer.CompleteAsync(ct);
                return null;
            }

            var database = string.IsNullOrWhiteSpace(login.Database) ? "master" : login.Database;
            var useError = TrySelectDatabase(engine, database);

            var ack = new TdsResponseWriter(stream, this.packetSize);
            if (useError is not null)
            {
                TdsTokens.WriteMessage(ack, isError: false, number: 5701, state: 1, severity: 0, message: useError, serverName: this.serverName);
                database = "master";
            }

            TdsTokens.WriteDatabaseEnvChange(ack, database, "master");
            TdsTokens.WritePacketSizeEnvChange(ack, this.packetSize);
            TdsTokens.WriteLoginAck(ack, "TidalSql");
            TdsTokens.WriteDone(ack, TdsDoneStatus.Final, 0, 0);
            await ack.CompleteAsync(ct);
            return engine;
        }

        private static string? TrySelectDatabase(TidalSql engine, string database)
        {
            if (string.Equals(database, "master", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            try
            {
                var result = engine.ExecuteQuery($"USE [{database.Replace("]", "]]")}]");
                return result.Success ? null : result.Message;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private async Task RequestLoopAsync(TidalSql engine, TdsPacketReader reader, Stream stream, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var message = await reader.ReadMessageAsync(ct);
                if (message is null)
                {
                    return; // client disconnected
                }

                switch (message.Type)
                {
                    case TdsPacketType.SqlBatch:
                        await ExecuteBatchAsync(engine, ExtractSqlText(message.Payload), stream, ct);
                        break;

                    case TdsPacketType.Attention:
                        await WriteDoneOnlyAsync(stream, (ushort)(TdsDoneStatus.Final | TdsDoneStatus.Attention), ct);
                        break;

                    case TdsPacketType.TransactionManager:
                        await WriteDoneOnlyAsync(stream, TdsDoneStatus.Final, ct);
                        break;

                    case TdsPacketType.Rpc:
                        await WriteErrorAsync(stream, "Remote procedure calls are not supported yet; use text SQL batches.", ct);
                        break;

                    default:
                        await WriteErrorAsync(stream, $"Unsupported TDS message type 0x{message.Type:X2}.", ct);
                        break;
                }
            }
        }

        private async Task ExecuteBatchAsync(TidalSql engine, string sql, Stream stream, CancellationToken ct)
        {
            var writer = new TdsResponseWriter(stream, this.packetSize);
            QueryResult result;
            try
            {
                result = engine.ExecuteQuery(sql, ct);
            }
            catch (Exception ex)
            {
                TdsTokens.WriteMessage(writer, isError: true, number: 50000, state: 1, severity: 16, message: ex.Message, serverName: this.serverName);
                TdsTokens.WriteDone(writer, (ushort)(TdsDoneStatus.Final | TdsDoneStatus.Error), 0, 0);
                await writer.CompleteAsync(ct);
                return;
            }

            if (!result.Success)
            {
                var text = string.IsNullOrEmpty(result.Message) ? "Query failed." : result.Message;
                TdsTokens.WriteMessage(writer, isError: true, number: 50000, state: 1, severity: 16, message: text, serverName: this.serverName);
                TdsTokens.WriteDone(writer, (ushort)(TdsDoneStatus.Final | TdsDoneStatus.Error), 0, 0);
                await writer.CompleteAsync(ct);
                return;
            }

            if (result.Columns.Count > 0)
            {
                var plans = TdsResultWriter.BuildPlans(result);
                TdsResultWriter.WriteColMetaData(writer, plans);
                foreach (var row in result.Rows)
                {
                    TdsResultWriter.WriteRow(writer, plans, row);
                }

                TdsTokens.WriteDone(writer, (ushort)(TdsDoneStatus.Final | TdsDoneStatus.Count), 0, (ulong)result.Rows.Count);
            }
            else
            {
                var affected = result.RowCount < 0 ? 0 : result.RowCount;
                TdsTokens.WriteDone(writer, (ushort)(TdsDoneStatus.Final | TdsDoneStatus.Count), 0, (ulong)affected);
            }

            await writer.CompleteAsync(ct);
        }

        private async Task WriteErrorAsync(Stream stream, string message, CancellationToken ct)
        {
            var writer = new TdsResponseWriter(stream, this.packetSize);
            TdsTokens.WriteMessage(writer, isError: true, number: 50000, state: 1, severity: 16, message: message, serverName: this.serverName);
            TdsTokens.WriteDone(writer, (ushort)(TdsDoneStatus.Final | TdsDoneStatus.Error), 0, 0);
            await writer.CompleteAsync(ct);
        }

        private async Task WriteDoneOnlyAsync(Stream stream, ushort status, CancellationToken ct)
        {
            var writer = new TdsResponseWriter(stream, this.packetSize);
            TdsTokens.WriteDone(writer, status, 0, 0);
            await writer.CompleteAsync(ct);
        }

        /// <summary>
        /// Extracts the UCS-2 SQL text from a SQL Batch payload. TDS 7.2+ batches begin with an
        /// ALL_HEADERS block whose first 4 bytes are its own total length; the SQL text follows it.
        /// </summary>
        private static string ExtractSqlText(byte[] payload)
        {
            var offset = 0;
            if (payload.Length >= 4)
            {
                var headerTotal = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
                if (headerTotal >= 4 && headerTotal <= (uint)payload.Length)
                {
                    offset = (int)headerTotal;
                }
            }

            return Encoding.Unicode.GetString(payload, offset, payload.Length - offset);
        }
    }
}
