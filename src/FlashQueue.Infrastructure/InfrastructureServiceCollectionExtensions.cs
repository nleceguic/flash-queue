using FlashQueue.Application.Processing;
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

        services.Configure<ReservationRepositoryOptions>(
            configuration.GetSection(ReservationRepositoryOptions.SectionName));
        services.AddSingleton(sp => new ReservationRepository(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOptions<ReservationRepositoryOptions>>().Value));

        services.AddSingleton<SchemaMigrator>();
        services.AddSingleton<IReservationProcessor, PostgresReservationProcessor>();

        return services;
    }
}
