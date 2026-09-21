namespace TidalSqlServer.Tds
{
    using System.Buffers.Binary;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>A fully-reassembled TDS request message: its packet type and concatenated payload.</summary>
    internal sealed record TdsMessage(byte Type, byte[] Payload);

    /// <summary>
    /// Reads TDS messages off a stream. Each message is one or more 8-byte-headed packets; packets are
    /// concatenated until one carries the End-Of-Message status bit. The 8-byte header is:
    /// Type(1) Status(1) Length(2, big-endian, includes header) SPID(2) PacketID(1) Window(1).
    /// </summary>
    internal sealed class TdsPacketReader
    {
        private readonly Stream stream;

        public TdsPacketReader(Stream stream) => this.stream = stream;

        /// <summary>Reads the next complete message, or null if the client closed the connection.</summary>
        public async Task<TdsMessage?> ReadMessageAsync(CancellationToken cancellationToken)
        {
            var header = new byte[8];
            using var payload = new MemoryStream();
            byte messageType = 0;

            while (true)
            {
                if (!await ReadExactlyAsync(header, 0, 8, cancellationToken))
                {
                    return null; // clean EOF between messages
                }

                messageType = header[0];
                var status = header[1];
                int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
                int bodyLength = length - 8;
                if (bodyLength < 0)
                {
                    throw new IOException($"Malformed TDS packet length {length}.");
                }

                if (bodyLength > 0)
                {
                    var body = new byte[bodyLength];
                    if (!await ReadExactlyAsync(body, 0, bodyLength, cancellationToken))
                    {
                        return null; // truncated mid-message => treat as disconnect
                    }

                    payload.Write(body, 0, bodyLength);
                }

                if ((status & TdsStatus.EndOfMessage) != 0)
                {
                    return new TdsMessage(messageType, payload.ToArray());
                }
            }
        }

        private async Task<bool> ReadExactlyAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var read = 0;
            while (read < count)
            {
                var n = await this.stream.ReadAsync(buffer.AsMemory(offset + read, count - read), ct);
                if (n == 0)
                {
                    return false;
                }

                read += n;
            }

            return true;
        }
    }
}
