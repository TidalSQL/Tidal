namespace TidalSqlServer.Tds
{
    using System;
    using System.Buffers.Binary;
    using System.Text;

    /// <summary>The fields this server extracts from a client LOGIN7 record.</summary>
    internal sealed class Login7
    {
        public byte[] TdsVersion { get; init; } = new byte[4];

        public int PacketSize { get; init; } = 4096;

        public string UserName { get; init; } = string.Empty;

        public string Password { get; init; } = string.Empty;

        public string Database { get; init; } = string.Empty;

        public string HostName { get; init; } = string.Empty;

        public string AppName { get; init; } = string.Empty;

        /// <summary>
        /// Parses a LOGIN7 payload. All string offsets in the record are byte offsets from the start of
        /// the payload; lengths are in UCS-2 characters. The password is de-obfuscated by XOR-ing each
        /// byte with 0xA5 and swapping its nibbles (the inverse of the client's obfuscation).
        /// </summary>
        public static Login7 Parse(byte[] p)
        {
            if (p.Length < 94)
            {
                throw new ArgumentException("LOGIN7 record too short.");
            }

            var tdsVersion = new byte[4];
            Array.Copy(p, 4, tdsVersion, 0, 4);
            int packetSize = BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(8, 4));

            string ReadString(int tableOffset, bool isPassword = false)
            {
                int ib = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(tableOffset, 2));
                int cch = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(tableOffset + 2, 2));
                if (cch == 0)
                {
                    return string.Empty;
                }

                int byteLen = cch * 2;
                if (ib < 0 || ib + byteLen > p.Length)
                {
                    return string.Empty;
                }

                if (!isPassword)
                {
                    return Encoding.Unicode.GetString(p, ib, byteLen);
                }

                var decoded = new byte[byteLen];
                for (var i = 0; i < byteLen; i++)
                {
                    var b = (byte)(p[ib + i] ^ 0xA5);
                    decoded[i] = (byte)(((b & 0x0F) << 4) | ((b & 0xF0) >> 4));
                }

                return Encoding.Unicode.GetString(decoded);
            }

            // OffsetLength table begins at byte 36: HostName, UserName, Password, AppName, ServerName,
            // (Extension/Unused), CltIntName, Language, Database, ...
            return new Login7
            {
                TdsVersion = tdsVersion,
                PacketSize = packetSize <= 0 ? 4096 : packetSize,
                HostName = ReadString(36),
                UserName = ReadString(40),
                Password = ReadString(44, isPassword: true),
                AppName = ReadString(48),
                Database = ReadString(68),
            };
        }
    }

    /// <summary>Builds the server PRELOGIN response payload advertising "encryption not supported".</summary>
    internal static class PreLoginResponse
    {
        public static byte[] Build()
        {
            // Two options (VERSION, ENCRYPTION) then a terminator. Each table entry is
            // token(1) + offset(2, big-endian) + length(2, big-endian); offsets are from payload start.
            const int tableSize = 5 + 5 + 1; // VERSION + ENCRYPTION + TERMINATOR
            var versionData = new byte[] { 16, 0, 0, 0, 0, 0 }; // UL_VERSION(4) + US_SUBBUILD(2)
            var encryptionData = new byte[] { TdsEncryption.NotSupported };

            int versionOffset = tableSize;
            int encryptionOffset = versionOffset + versionData.Length;
            var payload = new byte[encryptionOffset + encryptionData.Length];

            var pos = 0;
            payload[pos++] = TdsPreLoginOption.Version;
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(pos, 2), (ushort)versionOffset);
            pos += 2;
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(pos, 2), (ushort)versionData.Length);
            pos += 2;

            payload[pos++] = TdsPreLoginOption.Encryption;
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(pos, 2), (ushort)encryptionOffset);
            pos += 2;
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(pos, 2), (ushort)encryptionData.Length);
            pos += 2;

            payload[pos++] = TdsPreLoginOption.Terminator;

            Array.Copy(versionData, 0, payload, versionOffset, versionData.Length);
            Array.Copy(encryptionData, 0, payload, encryptionOffset, encryptionData.Length);
            return payload;
        }
    }
}
