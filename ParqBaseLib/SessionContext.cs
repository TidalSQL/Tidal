namespace ParqBaseLib
{
    using System.Collections.Generic;

    /// <summary>
    /// Holds per-session state (the currently selected database and the impersonation stack)
    /// instead of relying on the process-global current working directory. This makes execution
    /// safe to isolate per caller / per request.
    /// </summary>
    internal sealed class SessionContext
    {
        private readonly Stack<string> impersonation = new();

        public string? CurrentDatabasePath { get; set; }

        /// <summary>
        /// The server login the session authenticated as (via the console login gate), or null when
        /// the library is used programmatically without authenticating.
        /// </summary>
        public string? CurrentLogin { get; set; }

        /// <summary>True when the authenticated login is a member of the sysadmin server role.</summary>
        public bool IsSysadminLogin { get; set; }

        /// <summary>
        /// The database user the session is currently executing as (via EXECUTE AS USER), or
        /// null when unauthenticated. A null principal is treated as a full-control administrator
        /// so that unauthenticated sessions retain full access.
        /// </summary>
        public string? CurrentUser => this.impersonation.Count > 0 ? this.impersonation.Peek() : null;

        public void PushUser(string user) => this.impersonation.Push(user);

        public bool PopUser()
        {
            if (this.impersonation.Count == 0)
            {
                return false;
            }

            this.impersonation.Pop();
            return true;
        }
    }
}
