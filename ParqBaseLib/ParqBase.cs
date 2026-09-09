namespace ParqBaseLib
{
    using Microsoft.Extensions.DependencyInjection;

    public class ParqBase
    {
        private ParqBaseStatementVisitor parqBaseStatementVisitor;

        public ParqBase(IServiceProvider serviceProvider)
        {
            this.parqBaseStatementVisitor = new ParqBaseStatementVisitor(serviceProvider);
        }

        public ParqBase()
        {
            var serviceCollection = new ServiceCollection();
            serviceCollection.AddSingleton<ITableColumnCache, TableColumnCache>();
            var serviceProvider = serviceCollection.BuildServiceProvider();
            this.parqBaseStatementVisitor = new ParqBaseStatementVisitor(serviceProvider);
        }

        public void ExecuteStatement(string statement)
        {
            this.parqBaseStatementVisitor.StartVisitor(statement);
        }

        public QueryResult ExecuteQuery(string statement)
        {
            return this.parqBaseStatementVisitor.StartVisitor(statement);
        }
    }
}
