namespace TidalSqlServer.Tds
{
    using System;
    using System.IO;
    using System.Text;

    /// <summary>Emits the fixed control tokens of a TDS response (login ack, env changes, messages, done).</summary>
    internal static class TdsTokens
    {
        /// <summary>LOGINACK: acknowledges a successful login and reports the negotiated TDS version.</summary>
        public static void WriteLoginAck(TdsResponseWriter w, string programName)
        {
            var body = Build(bw =>
            {
                bw.Write((byte)1);           // Interface: 1 = SQL
                // TDS 7.4 version (0x74000004), encoded big-endian as it appears in LOGINACK.
                bw.Write(new byte[] { 0x74, 0x00, 0x00, 0x04 });
                WriteBVarchar(bw, programName);
                bw.Write((byte)16);           // ProgVersion: major
                bw.Write((byte)0);            // minor
                bw.Write((byte)0);            // build high
                bw.Write((byte)0);            // build low
            });

            w.WriteByte(TdsToken.LoginAck);
            w.WriteUInt16LE((ushort)body.Length);
            w.WriteBytes(body);
        }

        /// <summary>ENVCHANGE for the current database (type 1).</summary>
        public static void WriteDatabaseEnvChange(TdsResponseWriter w, string newDb, string oldDb)
        {
            var body = Build(bw =>
            {
                bw.Write(TdsEnvChangeType.Database);
                WriteBVarchar(bw, newDb);
                WriteBVarchar(bw, oldDb);
            });

            w.WriteByte(TdsToken.EnvChange);
            w.WriteUInt16LE((ushort)body.Length);
            w.WriteBytes(body);
        }

        /// <summary>ENVCHANGE for the negotiated packet size (type 4).</summary>
        public static void WritePacketSizeEnvChange(TdsResponseWriter w, int packetSize)
        {
            var value = packetSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var body = Build(bw =>
            {
                bw.Write(TdsEnvChangeType.PacketSize);
                WriteBVarchar(bw, value);
                WriteBVarchar(bw, value);
            });

            w.WriteByte(TdsToken.EnvChange);
            w.WriteUInt16LE((ushort)body.Length);
            w.WriteBytes(body);
        }

        /// <summary>ERROR (0xAA) or INFO (0xAB) message token.</summary>
        public static void WriteMessage(
            TdsResponseWriter w,
            bool isError,
            int number,
            byte state,
            byte severity,
            string message,
            string serverName)
        {
            var body = Build(bw =>
            {
                bw.Write(number);             // Number (int32 LE)
                bw.Write(state);              // State
                bw.Write(severity);           // Class / severity
                WriteUsVarchar(bw, message);  // MsgText
                WriteBVarchar(bw, serverName); // ServerName
                WriteBVarchar(bw, string.Empty); // ProcName
                bw.Write(0);                  // LineNumber (int32 LE)
            });

            w.WriteByte(isError ? TdsToken.Error : TdsToken.Info);
            w.WriteUInt16LE((ushort)body.Length);
            w.WriteBytes(body);
        }

        /// <summary>DONE token. <paramref name="status"/> is a <see cref="TdsDoneStatus"/> bitmask.</summary>
        public static void WriteDone(TdsResponseWriter w, ushort status, ushort currentCommand, ulong rowCount)
        {
            w.WriteByte(TdsToken.Done);
            w.WriteUInt16LE(status);
            w.WriteUInt16LE(currentCommand);
            w.WriteUInt64LE(rowCount);
        }

        private static byte[] Build(Action<BinaryWriter> write)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.Unicode, leaveOpen: true);
            write(bw);
            bw.Flush();
            return ms.ToArray();
        }

        private static void WriteBVarchar(BinaryWriter bw, string value)
        {
            bw.Write((byte)value.Length);
            bw.Write(Encoding.Unicode.GetBytes(value));
        }

        private static void WriteUsVarchar(BinaryWriter bw, string value)
        {
            bw.Write((ushort)value.Length);
            bw.Write(Encoding.Unicode.GetBytes(value));
        }
    }
}
