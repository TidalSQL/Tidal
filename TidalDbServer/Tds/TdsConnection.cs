namespace TidalDbServer.Tds
{
    using System;
    using System.Buffers.Binary;
    using System.IO;
    using System.Net.Security;
    using System.Net.Sockets;
    using System.Security.Authentication;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using TidalDbLib;

    /// <summary>
    /// Drives one client connection through the TDS handshake and request loop: PRELOGIN, an optional
    /// TLS handshake, LOGIN7 (authenticated against the TidalDb security catalog), then a loop that
    /// executes SQL batches and streams their results back as TDS token streams. Each connection owns
    /// its own <see cref="TidalDb"/> session, so logins and the current database are isolated per
    /// connection. When the client requests encryption (SSMS and modern SqlClient default to this), the
    /// whole session is protected with TLS using a self-signed certificate; clients that request no
    /// encryption continue to be served in plaintext.
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
            await using (var networkStream = this.client.GetStream())
            {
                var reader = new TdsPacketReader(networkStream);

                var active = await HandlePreLoginAsync(reader, networkStream, cancellationToken);
                if (active is null)
                {
                    return;
                }

                // After an optional TLS upgrade the active stream may differ from the raw socket, so
                // read every subsequent TDS message through a reader bound to the active stream.
                var activeReader = ReferenceEquals(active, networkStream)
                    ? reader
                    : new TdsPacketReader(active);

                try
                {
                    var engine = await HandleLoginAsync(activeReader, active, cancellationToken);
                    if (engine is null)
                    {
                        return;
                    }

                    await RequestLoopAsync(engine, activeReader, active, cancellationToken);
                }
                finally
                {
                    if (!ReferenceEquals(active, networkStream))
                    {
                        await active.DisposeAsync();
                    }
                }
            }
        }

        /// <summary>
        /// Handles PRELOGIN and encryption negotiation. Returns the stream to use for the rest of the
        /// session — the raw socket when unencrypted, or an <see cref="SslStream"/> when the client
        /// requested encryption — or <c>null</c> if the handshake failed and the connection must close.
        /// </summary>
        private async Task<Stream?> HandlePreLoginAsync(TdsPacketReader reader, Stream networkStream, CancellationToken ct)
        {
            var message = await reader.ReadMessageAsync(ct);
            if (message is null || message.Type != TdsPacketType.PreLogin)
            {
                TdsDiagnostics.Log($"PRELOGIN expected but got {(message is null ? "disconnect" : "0x" + message.Type.ToString("X2"))}.");
                return null;
            }

            var clientEncryption = ReadClientEncryption(message.Payload);
            TdsDiagnostics.Log($"PRELOGIN received. Client encryption option = {EncryptionName(clientEncryption)}.");

            // Encrypt the whole session when the client asks for (or requires) encryption; otherwise
            // advertise "not supported" and continue in plaintext, which keeps Encrypt=Optional/False
            // clients working exactly as before.
            var encrypt = clientEncryption == TdsEncryption.On || clientEncryption == TdsEncryption.Required;
            var serverEncryption = encrypt ? TdsEncryption.On : TdsEncryption.NotSupported;

            var writer = new TdsResponseWriter(networkStream, this.packetSize);
            writer.WriteBytes(PreLoginResponse.Build(serverEncryption));
            await writer.CompleteAsync(ct);

            if (!encrypt)
            {
                return networkStream;
            }

            var sslOverTds = new SslOverTdsStream(networkStream);
            var ssl = new SslStream(sslOverTds, leaveInnerStreamOpen: false);
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = TdsServerCertificate.Instance,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12,
                });
            }
            catch (Exception ex)
            {
                TdsDiagnostics.Log($"TLS handshake failed: {ex.Message}");
                await ssl.DisposeAsync();
                return null;
            }

            sslOverTds.FinishHandshake();
            TdsDiagnostics.Log($"TLS handshake complete ({ssl.SslProtocol}).");
            return ssl;
        }

        private async Task<TidalDb?> HandleLoginAsync(TdsPacketReader reader, Stream stream, CancellationToken ct)
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

            TdsDiagnostics.Log($"LOGIN7: user='{login.UserName}', db='{login.Database}', app='{login.AppName}', host='{login.HostName}', packet={login.PacketSize}.");

            var engine = new TidalDb();
            if (!engine.Login(login.UserName, login.Password))
            {
                TdsDiagnostics.Log($"LOGIN7 failed for user '{login.UserName}'.");
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
            TdsDiagnostics.Log($"LOGIN7 authenticated; database='{database}'{(useError is null ? string.Empty : $" (USE failed: {useError})")}.");

            var ack = new TdsResponseWriter(stream, this.packetSize);
            if (useError is not null)
            {
                TdsTokens.WriteMessage(ack, isError: false, number: 5701, state: 1, severity: 0, message: useError, serverName: this.serverName);
                database = "master";
            }

            TdsTokens.WriteDatabaseEnvChange(ack, database, "master");
            TdsTokens.WritePacketSizeEnvChange(ack, this.packetSize);
            TdsTokens.WriteLoginAck(ack, "TidalDb");
            TdsTokens.WriteDone(ack, TdsDoneStatus.Final, 0, 0);
            await ack.CompleteAsync(ct);
            TdsDiagnostics.Log("LOGINACK sent.");
            return engine;
        }

        private static string? TrySelectDatabase(TidalDb engine, string database)
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

        private async Task RequestLoopAsync(TidalDb engine, TdsPacketReader reader, Stream stream, CancellationToken ct)
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
                        var sql = ExtractSqlText(message.Payload);
                        TdsDiagnostics.Log($"SQLBATCH: {sql}");
                        await ExecuteBatchAsync(engine, sql, stream, ct);
                        break;

                    case TdsPacketType.Attention:
                        TdsDiagnostics.Log("ATTENTION");
                        await WriteDoneOnlyAsync(stream, (ushort)(TdsDoneStatus.Final | TdsDoneStatus.Attention), ct);
                        break;

                    case TdsPacketType.TransactionManager:
                        TdsDiagnostics.Log("TRANSACTION_MANAGER");
                        await WriteDoneOnlyAsync(stream, TdsDoneStatus.Final, ct);
                        break;

                    case TdsPacketType.Rpc:
                        if (TryExtractRpcSql(message.Payload, out var rpcSql, out var procName) &&
                            rpcSql.Length > 0)
                        {
                            TdsDiagnostics.Log($"RPC {procName}: {rpcSql}");
                            await ExecuteBatchAsync(engine, rpcSql, stream, ct);
                        }
                        else
                        {
                            TdsDiagnostics.Log($"RPC {procName} (no inline SQL; acknowledged)");
                            await WriteDoneOnlyAsync(stream, TdsDoneStatus.Final, ct);
                        }

                        break;

                    default:
                        TdsDiagnostics.Log($"UNKNOWN 0x{message.Type:X2}: {TdsDiagnostics.Describe(message.Payload)}");
                        await WriteErrorAsync(stream, $"Unsupported TDS message type 0x{message.Type:X2}.", ct);
                        break;
                }
            }
        }

        private async Task ExecuteBatchAsync(TidalDb engine, string sql, Stream stream, CancellationToken ct)
        {
            var writer = new TdsResponseWriter(stream, this.packetSize);

            // SSMS/SMO issue fixed metadata batches (SERVERPROPERTY, CONNECTIONPROPERTY, session SETs)
            // the engine cannot parse; answer those with synthesized result sets so the client connects.
            if (SsmsCompatibility.TryHandle(sql, out var compatSets))
            {
                TdsDiagnostics.Log($"  -> SSMS compat handled ({compatSets.Count} result set(s))");
                WriteResultSets(writer, compatSets);
                await writer.CompleteAsync(ct);
                return;
            }

            QueryResult result;
            try
            {
                result = engine.ExecuteQuery(sql, ct);
            }
            catch (Exception ex)
            {
                TdsDiagnostics.Log($"  -> EXCEPTION: {ex.Message}");
                TdsTokens.WriteMessage(writer, isError: true, number: 50000, state: 1, severity: 16, message: ex.Message, serverName: this.serverName);
                TdsTokens.WriteDone(writer, (ushort)(TdsDoneStatus.Final | TdsDoneStatus.Error), 0, 0);
                await writer.CompleteAsync(ct);
                return;
            }

            if (!result.Success)
            {
                var text = string.IsNullOrEmpty(result.Message) ? "Query failed." : result.Message;
                TdsDiagnostics.Log($"  -> FAILED: {text}");
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

        /// <summary>
        /// Streams one or more result sets as a single batch response. Each result set with columns
        /// emits COLMETADATA + ROWs followed by a DONE carrying the MORE flag, except the final token
        /// which is DONE_FINAL so the client stops reading. A batch with no result sets emits one
        /// DONE_FINAL acknowledging a successful statement that returned no rows.
        /// </summary>
        private static void WriteResultSets(TdsResponseWriter writer, List<QueryResult> resultSets)
        {
            if (resultSets.Count == 0)
            {
                TdsTokens.WriteDone(writer, (ushort)(TdsDoneStatus.Final | TdsDoneStatus.Count), 0, 0);
                return;
            }

            for (var i = 0; i < resultSets.Count; i++)
            {
                var result = resultSets[i];
                var isLast = i == resultSets.Count - 1;
                var status = isLast
                    ? (ushort)(TdsDoneStatus.Final | TdsDoneStatus.Count)
                    : (ushort)(TdsDoneStatus.More | TdsDoneStatus.Count);

                if (result.Columns.Count > 0)
                {
                    var plans = TdsResultWriter.BuildPlans(result);
                    TdsResultWriter.WriteColMetaData(writer, plans);
                    foreach (var row in result.Rows)
                    {
                        TdsResultWriter.WriteRow(writer, plans, row);
                    }

                    TdsTokens.WriteDone(writer, status, 0, (ulong)result.Rows.Count);
                }
                else
                {
                    var affected = result.RowCount < 0 ? 0 : result.RowCount;
                    TdsTokens.WriteDone(writer, status, 0, (ulong)affected);
                }
            }
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
        /// Reads the ENCRYPTION option byte from a client PRELOGIN payload, defaulting to NOT_SUP when
        /// the option is absent so an omitted option is treated as "no encryption".
        /// </summary>
        private static byte ReadClientEncryption(byte[] payload)
        {
            var pos = 0;
            while (pos + 5 <= payload.Length)
            {
                var token = payload[pos];
                if (token == TdsPreLoginOption.Terminator)
                {
                    break;
                }

                var offset = (payload[pos + 1] << 8) | payload[pos + 2];
                var length = (payload[pos + 3] << 8) | payload[pos + 4];
                if (token == TdsPreLoginOption.Encryption && length >= 1 && offset < payload.Length)
                {
                    return payload[offset];
                }

                pos += 5;
            }

            return TdsEncryption.NotSupported;
        }

        private static string EncryptionName(byte value) => value switch
        {
            TdsEncryption.Off => "OFF (0x00)",
            TdsEncryption.On => "ON (0x01)",
            TdsEncryption.NotSupported => "NOT_SUP (0x02)",
            TdsEncryption.Required => "REQ (0x03)",
            _ => $"0x{value:X2}",
        };

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

        /// <summary>
        /// Extracts the inline SQL statement from an RPC request (notably sp_executesql, which SMO/SSMS
        /// use extensively). The statement is the RPC's first character-typed parameter. Returns false
        /// for parameterless system RPCs (e.g. sp_reset_connection) so the caller can acknowledge them.
        /// </summary>
        private static bool TryExtractRpcSql(byte[] payload, out string sql, out string procName)
        {
            sql = string.Empty;
            procName = string.Empty;
            var pos = 0;

            // Skip the ALL_HEADERS stream (same framing as a SQL batch).
            if (payload.Length >= 4)
            {
                var headerTotal = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
                if (headerTotal >= 4 && headerTotal <= (uint)payload.Length)
                {
                    pos = (int)headerTotal;
                }
            }

            if (pos + 2 > payload.Length)
            {
                return false;
            }

            // NameLenProcID: either a proc name (USHORT char count + UCS2) or 0xFFFF then a proc id.
            var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(pos, 2));
            pos += 2;
            if (nameLen == 0xFFFF)
            {
                if (pos + 2 > payload.Length)
                {
                    return false;
                }

                var procId = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(pos, 2));
                pos += 2;
                procName = procId switch
                {
                    10 => "sp_executesql",
                    12 => "sp_execute",
                    2 => "sp_prepare",
                    13 => "sp_prepexec",
                    _ => $"#proc{procId}",
                };
            }
            else
            {
                if (pos + (nameLen * 2) > payload.Length)
                {
                    return false;
                }

                procName = Encoding.Unicode.GetString(payload, pos, nameLen * 2);
                pos += nameLen * 2;
            }

            // OptionFlags (USHORT).
            if (pos + 2 > payload.Length)
            {
                return false;
            }

            pos += 2;

            // Read the first parameter; for sp_executesql this is the statement text.
            if (pos >= payload.Length)
            {
                return false;
            }

            var paramNameLen = payload[pos];
            pos += 1;
            pos += paramNameLen * 2;
            if (pos + 2 > payload.Length)
            {
                return false;
            }

            pos += 1; // StatusFlags
            var type = payload[pos];
            pos += 1;

            // Character types: NVARCHAR (0xE7), NCHAR (0xEF), VARCHAR (0xA7), CHAR (0xAF).
            var isNChar = type == 0xE7 || type == 0xEF;
            if (!(isNChar || type == 0xA7 || type == 0xAF))
            {
                return false;
            }

            if (pos + 2 > payload.Length)
            {
                return false;
            }

            var maxLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(pos, 2));
            pos += 2;
            pos += 5; // COLLATION
            if (pos > payload.Length)
            {
                return false;
            }

            if (maxLen == 0xFFFF)
            {
                sql = ReadPlpString(payload, ref pos, isNChar);
            }
            else
            {
                if (pos + 2 > payload.Length)
                {
                    return false;
                }

                var actual = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(pos, 2));
                pos += 2;
                if (actual == 0xFFFF || pos + actual > payload.Length)
                {
                    return false; // NULL or truncated
                }

                sql = isNChar
                    ? Encoding.Unicode.GetString(payload, pos, actual)
                    : Encoding.ASCII.GetString(payload, pos, actual);
            }

            return sql.Length > 0;
        }

        /// <summary>Reads a PLP (partially length-prefixed) character value, concatenating its chunks.</summary>
        private static string ReadPlpString(byte[] payload, ref int pos, bool isNChar)
        {
            if (pos + 8 > payload.Length)
            {
                return string.Empty;
            }

            var total = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(pos, 8));
            pos += 8;
            if (total == 0xFFFFFFFFFFFFFFFF)
            {
                return string.Empty; // PLP_NULL
            }

            using var buffer = new System.IO.MemoryStream();
            while (pos + 4 <= payload.Length)
            {
                var chunk = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(pos, 4));
                pos += 4;
                if (chunk == 0)
                {
                    break; // PLP_TERMINATOR
                }

                if (pos + (int)chunk > payload.Length)
                {
                    chunk = (uint)(payload.Length - pos);
                }

                buffer.Write(payload, pos, (int)chunk);
                pos += (int)chunk;
            }

            var bytes = buffer.ToArray();
            return isNChar ? Encoding.Unicode.GetString(bytes) : Encoding.ASCII.GetString(bytes);
        }
    }
}
