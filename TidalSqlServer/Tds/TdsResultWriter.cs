namespace TidalSqlServer.Tds
{
    using System;
    using System.Buffers.Binary;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Numerics;
    using System.Text;
    using TidalSqlLib;

    internal enum ColumnKind
    {
        Int,
        BigInt,
        Bit,
        Float,
        Decimal,
        DateTime,
        Guid,
        NVarChar,
        NVarCharMax,
    }

    /// <summary>The resolved TDS shape of one result column.</summary>
    internal sealed class ColumnPlan
    {
        public required string Name { get; init; }

        public required ColumnKind Kind { get; init; }

        public int NVarCharBytes { get; init; } // declared byte length for non-MAX NVARCHAR

        public byte Precision { get; init; }

        public byte Scale { get; init; }
    }

    /// <summary>
    /// Translates a <see cref="QueryResult"/> (column names + loosely-typed row values) into TDS
    /// COLMETADATA and ROW tokens. Because the engine does not surface column types, each column's TDS
    /// type is inferred from its first non-null value (defaulting to NVARCHAR), with string lengths and
    /// decimal precision/scale derived from the data.
    /// </summary>
    internal static class TdsResultWriter
    {
        // SQL_Latin1_General_CP1_CI_AS — a conventional default collation for char/nchar columns.
        private static readonly byte[] DefaultCollation = { 0x09, 0x04, 0xD0, 0x00, 0x34 };

        private static readonly DateTime SqlEpoch = new(1900, 1, 1);

        public static List<ColumnPlan> BuildPlans(QueryResult result)
        {
            var plans = new List<ColumnPlan>(result.Columns.Count);
            foreach (var name in result.Columns)
            {
                plans.Add(BuildPlan(name, result));
            }

            return plans;
        }

        public static void WriteColMetaData(TdsResponseWriter w, List<ColumnPlan> plans)
        {
            w.WriteByte(TdsToken.ColMetaData);
            w.WriteUInt16LE((ushort)plans.Count);

            foreach (var plan in plans)
            {
                w.WriteUInt32LE(0);        // UserType
                w.WriteUInt16LE(0x0001);   // Flags: fNullable

                switch (plan.Kind)
                {
                    case ColumnKind.Int:
                        w.WriteByte(TdsDataType.IntN);
                        w.WriteByte(4);
                        break;
                    case ColumnKind.BigInt:
                        w.WriteByte(TdsDataType.IntN);
                        w.WriteByte(8);
                        break;
                    case ColumnKind.Bit:
                        w.WriteByte(TdsDataType.BitN);
                        w.WriteByte(1);
                        break;
                    case ColumnKind.Float:
                        w.WriteByte(TdsDataType.FloatN);
                        w.WriteByte(8);
                        break;
                    case ColumnKind.Decimal:
                        w.WriteByte(TdsDataType.NumericN);
                        w.WriteByte(17);            // max byte length (sign + 16)
                        w.WriteByte(plan.Precision);
                        w.WriteByte(plan.Scale);
                        break;
                    case ColumnKind.DateTime:
                        w.WriteByte(TdsDataType.DateTimeN);
                        w.WriteByte(8);
                        break;
                    case ColumnKind.Guid:
                        w.WriteByte(TdsDataType.UniqueIdentifier);
                        w.WriteByte(16);
                        break;
                    case ColumnKind.NVarChar:
                        w.WriteByte(TdsDataType.NVarChar);
                        w.WriteUInt16LE((ushort)plan.NVarCharBytes);
                        w.WriteBytes(DefaultCollation);
                        break;
                    case ColumnKind.NVarCharMax:
                        w.WriteByte(TdsDataType.NVarChar);
                        w.WriteUInt16LE(0xFFFF);
                        w.WriteBytes(DefaultCollation);
                        break;
                }

                w.WriteBVarchar(plan.Name);
            }
        }

        public static void WriteRow(TdsResponseWriter w, List<ColumnPlan> plans, Dictionary<string, object?> row)
        {
            w.WriteByte(TdsToken.Row);
            foreach (var plan in plans)
            {
                row.TryGetValue(plan.Name, out var value);
                WriteValue(w, plan, value);
            }
        }

        private static void WriteValue(TdsResponseWriter w, ColumnPlan plan, object? value)
        {
            var isNull = value is null or DBNull;

            switch (plan.Kind)
            {
                case ColumnKind.Int:
                    if (isNull) { w.WriteByte(0); break; }
                    w.WriteByte(4);
                    w.WriteInt32LE(Convert.ToInt32(value, CultureInfo.InvariantCulture));
                    break;

                case ColumnKind.BigInt:
                    if (isNull) { w.WriteByte(0); break; }
                    w.WriteByte(8);
                    WriteInt64LE(w, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                    break;

                case ColumnKind.Bit:
                    if (isNull) { w.WriteByte(0); break; }
                    w.WriteByte(1);
                    w.WriteByte((byte)(Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? 1 : 0));
                    break;

                case ColumnKind.Float:
                    if (isNull) { w.WriteByte(0); break; }
                    w.WriteByte(8);
                    WriteDoubleLE(w, Convert.ToDouble(value, CultureInfo.InvariantCulture));
                    break;

                case ColumnKind.Decimal:
                    if (isNull) { w.WriteByte(0); break; }
                    WriteDecimal(w, Convert.ToDecimal(value, CultureInfo.InvariantCulture), plan.Scale);
                    break;

                case ColumnKind.DateTime:
                    if (isNull) { w.WriteByte(0); break; }
                    WriteDateTime(w, Convert.ToDateTime(value, CultureInfo.InvariantCulture));
                    break;

                case ColumnKind.Guid:
                    if (isNull) { w.WriteByte(0); break; }
                    w.WriteByte(16);
                    w.WriteBytes(ToGuid(value!).ToByteArray());
                    break;

                case ColumnKind.NVarChar:
                    if (isNull) { w.WriteUInt16LE(0xFFFF); break; }
                    var bytes = Encoding.Unicode.GetBytes(value!.ToString() ?? string.Empty);
                    w.WriteUInt16LE((ushort)bytes.Length);
                    w.WriteBytes(bytes);
                    break;

                case ColumnKind.NVarCharMax:
                    WritePlpString(w, isNull ? null : value!.ToString());
                    break;
            }
        }

        private static ColumnPlan BuildPlan(string name, QueryResult result)
        {
            object? sample = null;
            foreach (var row in result.Rows)
            {
                if (row.TryGetValue(name, out var v) && v is not (null or DBNull))
                {
                    sample = v;
                    break;
                }
            }

            var kind = sample switch
            {
                bool => ColumnKind.Bit,
                byte or sbyte or short or ushort or int or uint => ColumnKind.Int,
                long or ulong => ColumnKind.BigInt,
                float or double => ColumnKind.Float,
                decimal => ColumnKind.Decimal,
                DateTime => ColumnKind.DateTime,
                Guid => ColumnKind.Guid,
                _ => ColumnKind.NVarChar,
            };

            if (kind == ColumnKind.Decimal)
            {
                byte scale = 0;
                foreach (var row in result.Rows)
                {
                    if (row.TryGetValue(name, out var v) && v is decimal d)
                    {
                        scale = Math.Max(scale, (byte)((decimal.GetBits(d)[3] >> 16) & 0xFF));
                    }
                }

                return new ColumnPlan { Name = name, Kind = kind, Precision = 38, Scale = Math.Min(scale, (byte)38) };
            }

            if (kind == ColumnKind.NVarChar)
            {
                var maxChars = 1;
                foreach (var row in result.Rows)
                {
                    if (row.TryGetValue(name, out var v) && v is not (null or DBNull))
                    {
                        maxChars = Math.Max(maxChars, (v.ToString() ?? string.Empty).Length);
                    }
                }

                if (maxChars > 4000)
                {
                    return new ColumnPlan { Name = name, Kind = ColumnKind.NVarCharMax };
                }

                return new ColumnPlan { Name = name, Kind = ColumnKind.NVarChar, NVarCharBytes = maxChars * 2 };
            }

            return new ColumnPlan { Name = name, Kind = kind };
        }

        private static void WriteInt64LE(TdsResponseWriter w, long value)
        {
            Span<byte> tmp = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(tmp, value);
            w.WriteBytes(tmp);
        }

        private static void WriteDoubleLE(TdsResponseWriter w, double value)
        {
            Span<byte> tmp = stackalloc byte[8];
            BinaryPrimitives.WriteDoubleLittleEndian(tmp, value);
            w.WriteBytes(tmp);
        }

        private static void WriteDateTime(TdsResponseWriter w, DateTime value)
        {
            var days = (int)(value.Date - SqlEpoch).TotalDays;
            var seconds = (value - value.Date).TotalSeconds;
            var time = (uint)Math.Round(seconds * 300.0, MidpointRounding.AwayFromZero);

            w.WriteByte(8);
            w.WriteInt32LE(days);
            w.WriteUInt32LE(time);
        }

        private static void WriteDecimal(TdsResponseWriter w, decimal value, byte scale)
        {
            var negative = value < 0;
            var bits = decimal.GetBits(Math.Abs(value));
            var unscaled = (new BigInteger((uint)bits[2]) << 64)
                         | (new BigInteger((uint)bits[1]) << 32)
                         | new BigInteger((uint)bits[0]);
            var currentScale = (bits[3] >> 16) & 0xFF;
            if (scale > currentScale)
            {
                unscaled *= BigInteger.Pow(10, scale - currentScale);
            }

            var magnitude = unscaled.ToByteArray(isUnsigned: true, isBigEndian: false);
            var payload = new byte[16];
            Array.Copy(magnitude, payload, Math.Min(magnitude.Length, 16));

            w.WriteByte(17);                        // sign + 16 magnitude bytes
            w.WriteByte((byte)(negative ? 0 : 1));  // 1 = positive, 0 = negative
            w.WriteBytes(payload);
        }

        private static void WritePlpString(TdsResponseWriter w, string? value)
        {
            if (value is null)
            {
                w.WriteUInt64LE(0xFFFFFFFFFFFFFFFFUL); // PLP_NULL
                return;
            }

            var bytes = Encoding.Unicode.GetBytes(value);
            w.WriteUInt64LE((ulong)bytes.Length); // known total length
            if (bytes.Length > 0)
            {
                w.WriteUInt32LE((uint)bytes.Length); // single chunk
                w.WriteBytes(bytes);
            }

            w.WriteUInt32LE(0); // PLP terminator (zero-length chunk)
        }

        private static Guid ToGuid(object value) =>
            value is Guid g ? g : Guid.Parse(value.ToString() ?? Guid.Empty.ToString());
    }
}
