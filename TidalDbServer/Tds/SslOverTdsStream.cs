namespace TidalDbServer.Tds
{
    using System;
    using System.Buffers.Binary;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Adapts the raw network stream so a standard <see cref="System.Net.Security.SslStream"/> can run
    /// the TLS handshake that TDS encapsulates inside PRELOGIN (0x12) packets. While
    /// <see cref="FinishHandshake"/> has not been called, every TLS record the SslStream writes is
    /// framed as one PRELOGIN packet, and every read strips a PRELOGIN 8-byte header to recover the
    /// client's TLS bytes. After <c>AuthenticateAsServer</c> completes the caller invokes
    /// <see cref="FinishHandshake"/>, which switches the stream to pass-through: the SslStream then
    /// exchanges raw TLS application records directly over the socket, which is how SqlClient tunnels
    /// the encrypted TDS traffic (LOGIN7 onward) for the rest of the session.
    /// </summary>
    internal sealed class SslOverTdsStream : Stream
    {
        private const int HeaderLength = 8;
        private readonly Stream inner;
        private byte[] readBuffer = Array.Empty<byte>();
        private int readPos;
        private bool encapsulate = true;

        public SslOverTdsStream(Stream inner) => this.inner = inner;

        /// <summary>Stops PRELOGIN encapsulation; subsequent reads/writes pass straight to the socket.</summary>
        public void FinishHandshake() => this.encapsulate = false;

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => this.inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => this.inner.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!this.encapsulate)
            {
                return await this.inner.ReadAsync(buffer, cancellationToken);
            }

            while (this.readPos >= this.readBuffer.Length)
            {
                if (!await FillFromNextPacketAsync(cancellationToken))
                {
                    return 0;
                }
            }

            var available = this.readBuffer.Length - this.readPos;
            var toCopy = Math.Min(available, buffer.Length);
            this.readBuffer.AsSpan(this.readPos, toCopy).CopyTo(buffer.Span);
            this.readPos += toCopy;
            return toCopy;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!this.encapsulate)
            {
                await this.inner.WriteAsync(buffer, cancellationToken);
                return;
            }

            // Frame the TLS bytes into PRELOGIN packets no larger than the 16-bit length field allows.
            var remaining = buffer;
            const int maxBody = 0xFFFF - HeaderLength;
            while (remaining.Length > 0)
            {
                var chunk = Math.Min(maxBody, remaining.Length);
                var header = new byte[HeaderLength];
                header[0] = TdsPacketType.PreLogin;
                header[1] = TdsStatus.EndOfMessage;
                BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), (ushort)(chunk + HeaderLength));
                await this.inner.WriteAsync(header, cancellationToken);
                await this.inner.WriteAsync(remaining.Slice(0, chunk), cancellationToken);
                remaining = remaining.Slice(chunk);
            }

            await this.inner.FlushAsync(cancellationToken);
        }

        private async Task<bool> FillFromNextPacketAsync(CancellationToken cancellationToken)
        {
            var header = new byte[HeaderLength];
            if (!await ReadExactlyAsync(header, HeaderLength, cancellationToken))
            {
                return false;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
            int bodyLength = length - HeaderLength;
            if (bodyLength <= 0)
            {
                this.readBuffer = Array.Empty<byte>();
                this.readPos = 0;
                return true;
            }

            var body = new byte[bodyLength];
            if (!await ReadExactlyAsync(body, bodyLength, cancellationToken))
            {
                return false;
            }

            this.readBuffer = body;
            this.readPos = 0;
            return true;
        }

        private async Task<bool> ReadExactlyAsync(byte[] buffer, int count, CancellationToken cancellationToken)
        {
            var read = 0;
            while (read < count)
            {
                var n = await this.inner.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken);
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
