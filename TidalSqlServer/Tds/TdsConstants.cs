namespace TidalSqlServer.Tds
{
    /// <summary>TDS packet (message) types — the first byte of every 8-byte packet header.</summary>
    internal static class TdsPacketType
    {
        public const byte SqlBatch = 0x01;
        public const byte Rpc = 0x03;
        public const byte TabularResult = 0x04; // server -> client responses
        public const byte Attention = 0x06;
        public const byte BulkLoad = 0x07;
        public const byte TransactionManager = 0x0E;
        public const byte Login7 = 0x10;
        public const byte Sspi = 0x11;
        public const byte PreLogin = 0x12;
    }

    /// <summary>Status bits in the second byte of the packet header.</summary>
    internal static class TdsStatus
    {
        public const byte Normal = 0x00;
        public const byte EndOfMessage = 0x01;
        public const byte IgnoreEvent = 0x02;
        public const byte ResetConnection = 0x08;
    }

    /// <summary>Response token type bytes (server -> client token streams).</summary>
    internal static class TdsToken
    {
        public const byte ColMetaData = 0x81;
        public const byte Error = 0xAA;
        public const byte Info = 0xAB;
        public const byte LoginAck = 0xAD;
        public const byte Row = 0xD1;
        public const byte NbcRow = 0xD2;
        public const byte EnvChange = 0xE3;
        public const byte Done = 0xFD;
        public const byte DoneProc = 0xFE;
        public const byte DoneInProc = 0xFF;
    }

    /// <summary>DONE token status flags (a USHORT bitmask).</summary>
    internal static class TdsDoneStatus
    {
        public const ushort Final = 0x0000;
        public const ushort More = 0x0001;
        public const ushort Error = 0x0002;
        public const ushort Count = 0x0010;   // DoneRowCount is valid
        public const ushort Attention = 0x0020;
    }

    /// <summary>ENVCHANGE sub-types.</summary>
    internal static class TdsEnvChangeType
    {
        public const byte Database = 1;
        public const byte Language = 2;
        public const byte PacketSize = 4;
        public const byte SqlCollation = 7;
    }

    /// <summary>PRELOGIN option tokens.</summary>
    internal static class TdsPreLoginOption
    {
        public const byte Version = 0x00;
        public const byte Encryption = 0x01;
        public const byte InstOpt = 0x02;
        public const byte ThreadId = 0x03;
        public const byte Mars = 0x04;
        public const byte TraceId = 0x05;
        public const byte FedAuthRequired = 0x06;
        public const byte Terminator = 0xFF;
    }

    /// <summary>PRELOGIN encryption negotiation values.</summary>
    internal static class TdsEncryption
    {
        public const byte Off = 0x00;      // encryption available, used for login only
        public const byte On = 0x01;       // encryption on
        public const byte NotSupported = 0x02;
        public const byte Required = 0x03;
    }

    /// <summary>TDS data type tokens used by this server's COLMETADATA / ROW encoders.</summary>
    internal static class TdsDataType
    {
        public const byte IntN = 0x26;          // nullable int (len 1/2/4/8)
        public const byte BitN = 0x68;          // nullable bit (len 1)
        public const byte FloatN = 0x6D;        // nullable float (len 4/8)
        public const byte NumericN = 0x6C;      // nullable numeric/decimal
        public const byte DateTimeN = 0x6F;     // nullable datetime (len 8)
        public const byte UniqueIdentifier = 0x24;
        public const byte NVarChar = 0xE7;      // USHORTLEN char/binary
    }
}
