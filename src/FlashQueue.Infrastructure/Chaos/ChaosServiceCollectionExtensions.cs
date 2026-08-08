using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlashQueue.Infrastructure.Chaos;

public static class ChaosServiceCollectionExtensions
{
    public const string EnabledVariableName = "CHAOS_MODE";

    /// <summary>
    /// Decide, una sola vez, qué <see cref="IChaosInjector"/> registrar. "Ausente" y
    /// "explícitamente false" toman la misma rama (<see cref="NullChaosInjector"/>): esa es la
    /// garantía de "cero overhead cuando CHAOS_MODE no está presente" — no hay ninguna
    /// comprobación de este flag en el camino de las llamadas a Postgres/RabbitMQ, solo aquí, en
    /// el arranque.
    /// </summary>
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
