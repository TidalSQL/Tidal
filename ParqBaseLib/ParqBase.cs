namespace ParqBaseLib
{
    using Microsoft.Extensions.DependencyInjection;
    using ParquetSharp;
    using System.IO;

    public class ParqBase
    {
        private ParqBaseStatementVisitor parqBaseStatementVisitor;
        private ServiceCollection serviceCollection = new();
        private ServiceProvider serviceProvider;

        public ParqBase()
        {
            // this.serviceCollection.AddSingleton<ITableColumnCache, TableColumnCache>();

            this.serviceCollection.AddSingleton<ITableColumnCache>(provider =>
            {
                return new TableColumnCache();
            });

            this.serviceProvider = this.serviceCollection.BuildServiceProvider();
            this.parqBaseStatementVisitor = new ParqBaseStatementVisitor(this.serviceProvider);
        }

        public void ExecuteStatement(string statement)
        {
            this.parqBaseStatementVisitor.StartVisitor(statement);
        }
    }
}
