namespace ParqBaseConsole
{
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using ParqBaseLib;

    internal class Program
    {
        static async Task Main(string[] args)
        {
            var host = Host.CreateDefaultBuilder(args)
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
