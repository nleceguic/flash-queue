using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlashQueue.Infrastructure.Chaos;

public static class ChaosServiceCollectionExtensions
{
    public const string EnabledVariableName = "CHAOS_MODE";

    /// <summary>Decide, una sola vez al arrancar, qué <see cref="IChaosInjector"/> registrar. "Ausente" y "false" toman la misma rama.</summary>
    public static bool IsEnabled(IConfiguration configuration) =>
        bool.TryParse(configuration[EnabledVariableName], out var enabled) && enabled;

    public static IServiceCollection AddChaos(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ChaosOptions>(configuration.GetSection(ChaosOptions.SectionName));

        if (IsEnabled(configuration))
        {
            services.AddSingleton<IChaosInjector, RandomChaosInjector>();
        }
        else
        {
            services.AddSingleton<IChaosInjector, NullChaosInjector>();
        }

        return services;
    }
}
