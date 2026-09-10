namespace ParqBaseLib
{
    using System;

    public class ParqBase
    {
        private readonly SessionContext session = new();
        private readonly object gate = new();

        public ParqBase()
        {
        }

        // Retained for dependency-injection resolution; ParqBase keeps its own session state.
        public ParqBase(IServiceProvider serviceProvider)
        {
        }

        /// <summary>
        /// Absolute path of the database currently selected in this session, or null if none.
        /// </summary>
        public string? CurrentDatabasePath => this.session.CurrentDatabasePath;

        public void ExecuteStatement(string statement)
        {
            this.ExecuteQuery(statement);
        }

        public QueryResult ExecuteQuery(string statement)
        {
            // A ParqBase instance represents a single logical session; serialize calls so a
            // shared instance stays consistent. Concurrency comes from using separate instances.
            lock (this.gate)
            {
                var visitor = new ParqBaseStatementVisitor(this.session);
                return visitor.Run(statement);
            }
        }
    }
}
