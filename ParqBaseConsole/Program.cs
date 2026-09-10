namespace ParqBaseConsole
{
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using ParqBaseLib;

    internal class Program
    {
        static async Task Main(string[] args)
        {
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureLogging(logging =>
                {
                    // Keep the REPL clean: only surface warnings/errors, and silence the
                    // generic host's "Application started / Content root path" lifetime messages.
                    logging.SetMinimumLevel(LogLevel.Warning);
                    logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
                })
                .ConfigureServices(services =>
                {
                    services.AddParqBase();
                    services.AddHostedService<ParqBaseHostedService>();
                })
                .Build();

            await host.RunAsync();
        }
    }
}
