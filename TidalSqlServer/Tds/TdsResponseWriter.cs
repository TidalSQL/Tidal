namespace TidalSqlServer.Tds
{
    using System;
    using System.Buffers.Binary;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Writes a server-&gt;client TDS response as a stream of tokens, chunking the bytes into packets of
    /// the negotiated size. Token writers append bytes freely; the writer emits a non-final packet
    /// whenever the current packet fills, and <see cref="CompleteAsync"/> flushes the trailing bytes
    /// with the End-Of-Message status bit set. This keeps memory bounded for large result sets.
    /// </summary>
    internal sealed class TdsResponseWriter
    {
        private readonly Stream stream;
        private readonly byte packetType;
        private readonly byte[] buffer;
        private int position;   // next free byte in buffer; bytes [0,8) are reserved for the header
        private byte packetId;

        public TdsResponseWriter(Stream stream, int packetSize, byte packetType = TdsPacketType.TabularResult)
        {
            this.stream = stream;
            this.packetType = packetType;
            this.buffer = new byte[Math.Clamp(packetSize, 512, 32767)];
            this.position = 8;
        }

        public void WriteByte(byte value)
        {
            if (this.position == this.buffer.Length)
            {
                FlushPacket(last: false);
            }

            this.buffer[this.position++] = value;
        }

        public void WriteBytes(ReadOnlySpan<byte> data)
        {
            foreach (var b in data)
            {
                WriteByte(b);
            }
        }

        public void WriteUInt16LE(ushort value)
        {
            Span<byte> tmp = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(tmp, value);
            WriteBytes(tmp);
        }

        public void WriteInt32LE(int value)
        {
            Span<byte> tmp = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(tmp, value);
            WriteBytes(tmp);
        }

        public void WriteUInt32LE(uint value)
        {
            Span<byte> tmp = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(tmp, value);
            WriteBytes(tmp);
        }

        public void WriteUInt64LE(ulong value)
        {
            Span<byte> tmp = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(tmp, value);
            WriteBytes(tmp);
        }

        /// <summary>Writes a B_VARCHAR: a 1-byte character count followed by the UCS-2 (UTF-16LE) text.</summary>
        public void WriteBVarchar(string value)
        {
            WriteByte((byte)value.Length);
            WriteBytes(System.Text.Encoding.Unicode.GetBytes(value));
        }

        /// <summary>Writes a US_VARCHAR: a 2-byte character count followed by the UCS-2 text.</summary>
        public void WriteUsVarchar(string value)
        {
            WriteUInt16LE((ushort)value.Length);
            WriteBytes(System.Text.Encoding.Unicode.GetBytes(value));
        }

        /// <summary>Flushes any buffered bytes as the final packet (End-Of-Message) of this message.</summary>
        public async Task CompleteAsync(CancellationToken cancellationToken)
        {
            WriteHeader(this.position, last: true);
            await this.stream.WriteAsync(this.buffer.AsMemory(0, this.position), cancellationToken);
            await this.stream.FlushAsync(cancellationToken);
            this.position = 8;
            this.packetId = 0;
        }

        private void FlushPacket(bool last)
        {
            WriteHeader(this.buffer.Length, last);

            // Synchronous flush of a full packet keeps the token writers non-async; the buffer is
            // immediately reused for the next packet. Blocking here is acceptable for a full packet.
            this.stream.Write(this.buffer, 0, this.buffer.Length);
            this.position = 8;
        }

        private void WriteHeader(int totalLength, bool last)
        {
            this.buffer[0] = this.packetType;
            this.buffer[1] = last ? TdsStatus.EndOfMessage : TdsStatus.Normal;
            BinaryPrimitives.WriteUInt16BigEndian(this.buffer.AsSpan(2, 2), (ushort)totalLength);
            this.buffer[4] = 0; // SPID hi
            this.buffer[5] = 0; // SPID lo
            this.buffer[6] = this.packetId++;
            this.buffer[7] = 0; // window
        }
    }
}
