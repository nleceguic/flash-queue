using FlashQueue.Application.Processing;
using FlashQueue.Application.Stats;
using FlashQueue.Infrastructure.Chaos;
using FlashQueue.Infrastructure.Messaging;
using FlashQueue.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FlashQueue.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public const string ConnectionStringName = "FlashQueueDb";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Falta la cadena de conexión '{ConnectionStringName}'.");

        services.AddNpgsqlDataSource(connectionString);
        services.TryAddSingleton(TimeProvider.System);

        services.AddChaos(configuration);

        // Por defecto no-op; FlashQueue.Workers lo sustituye por SignalRReservationStatsNotifier.
        services.TryAddSingleton<IReservationStatsNotifier, NullReservationStatsNotifier>();

        services.Configure<ReservationRepositoryOptions>(
            configuration.GetSection(ReservationRepositoryOptions.SectionName));
        services.AddSingleton(sp => new ReservationRepository(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOptions<ReservationRepositoryOptions>>().Value,
            sp.GetRequiredService<IChaosInjector>()));

        services.AddSingleton<SchemaMigrator>();
        services.AddSingleton<IReservationProcessor, PostgresReservationProcessor>();

        // FlashQueue.Workers solo publica, nunca consume: no se pasa configureConsumers ni serviceName.
        services.AddRabbitMqMessaging(configuration);

        // Circuit breaker + timeout alrededor de la publicación (ADR 0004); los Consumers.* llaman
        // a AddRabbitMqMessaging directamente y no lo registran.
        services.Configure<RabbitMqPublishResilienceOptions>(
            configuration.GetSection(RabbitMqPublishResilienceOptions.SectionName));
        services.AddSingleton<RabbitMqPublishResiliencePipelineProvider>();
        services.AddSingleton<IReservationEventPublisher, ReservationEventPublisher>();

        return services;
    }
}
