namespace ParqBaseLib
{
    /// <summary>
    /// Holds per-session state (the currently selected database) instead of relying on
    /// the process-global current working directory. This makes execution safe to isolate
    /// per caller / per request.
    /// </summary>
    internal sealed class SessionContext
    {
        public string? CurrentDatabasePath { get; set; }
    }
}
