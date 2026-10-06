namespace TidalDbServer.Tds
{
    using System;
    using System.IO;
    using System.Text;

    /// <summary>
    /// Opt-in diagnostic logger for the TDS endpoint. When the environment variable
    /// <c>TIDALDB_TDS_LOG</c> is set to a writable file path, every connection event and request
    /// (PRELOGIN, LOGIN7, SQL batches, RPCs) is appended to that file. This is used to discover the
    /// exact sequence of statements a client such as SSMS issues on connect so the server can be made
    /// compatible. It is a no-op (zero overhead beyond an env lookup) when the variable is unset.
    /// </summary>
    internal static class TdsDiagnostics
    {
        private static readonly object Gate = new object();
        private static readonly string? LogPath =
            Environment.GetEnvironmentVariable("TIDALDB_TDS_LOG");

        public static bool Enabled => !string.IsNullOrEmpty(LogPath);

        public static void Log(string message)
        {
            if (string.IsNullOrEmpty(LogPath))
            {
                return;
            }

            try
            {
                lock (Gate)
                {
                    File.AppendAllText(
                        LogPath,
                        $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}",
                        Encoding.UTF8);
                }
            }
            catch
            {
                // Diagnostics must never affect serving a client.
            }
        }

        /// <summary>Renders a short, printable summary of a binary payload for the log.</summary>
        public static string Describe(ReadOnlySpan<byte> payload, int maxBytes = 64)
        {
            var take = Math.Min(maxBytes, payload.Length);
            var sb = new StringBuilder();
            sb.Append(payload.Length).Append(" bytes: ");
            for (var i = 0; i < take; i++)
            {
                sb.Append(payload[i].ToString("X2")).Append(' ');
            }

            if (payload.Length > take)
            {
                sb.Append("...");
            }

            return sb.ToString();
        }
    }
}
