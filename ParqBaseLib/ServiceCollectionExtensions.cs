namespace ParqBaseLib
{
    using Microsoft.Extensions.DependencyInjection;

    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddParqBase(this IServiceCollection services)
        {
            // Transient so each caller / web request gets an isolated session, making concurrent
            // use safe (no shared session state or process-global current directory).
            services.AddTransient<ParqBase>();
            return services;
        }
    }
}
