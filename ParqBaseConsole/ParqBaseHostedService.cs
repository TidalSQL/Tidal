namespace ParqBaseConsole
{
    using Microsoft.Extensions.Hosting;
    using ParqBaseLib;
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    internal class ParqBaseHostedService : BackgroundService
    {
        private readonly ParqBase db;
        private readonly IHostApplicationLifetime lifetime;

        public ParqBaseHostedService(ParqBase db, IHostApplicationLifetime lifetime)
        {
            this.db = db;
            this.lifetime = lifetime;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("Initializing ParqBase...");
            Console.WriteLine("ParqBase is running");
            Console.WriteLine("Type SQL statements, 'exit' to quit.");

            return Task.Run(() =>
            {
                try
                {
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        Console.WriteLine();
                        Console.Write("parqbase>> ");
                        var input = Console.ReadLine();

                        // null means end-of-input (e.g. Ctrl+Z / piped input closed).
                        if (input == null || stoppingToken.IsCancellationRequested)
                        {
                            break;
                        }

                        input = input.Trim();
                        if (input.Length == 0)
                        {
                            continue;
                        }

                        if (string.Equals(input, "exit", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(input, "quit", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }

                        var result = this.db.ExecuteQuery(input);

                        if (result.Columns.Count > 0)
                        {
                            Console.WriteLine(result.ToDisplayString());
                        }
                        else
                        {
                            Console.WriteLine(result.Message);
                        }
                    }
                }
                finally
                {
                    // Tell the generic host to shut down so the process actually exits
                    // instead of lingering until a Ctrl+C signal.
                    this.lifetime.StopApplication();
                }
            }, stoppingToken);
        }
    }
}
