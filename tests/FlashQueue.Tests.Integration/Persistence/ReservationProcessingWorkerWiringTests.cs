using Dapper;
using FlashQueue.Application.Ingestion;
using FlashQueue.Application.Processing;
using FlashQueue.Domain.Entities;
using FlashQueue.Infrastructure;
using FlashQueue.Infrastructure.Persistence;
using FlashQueue.Tests.Integration.Support;
using FlashQueue.Workers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace FlashQueue.Tests.Integration.Persistence;

/// <summary>
/// Prueba el cableado real (requisito 3 del encargo): que <see cref="ReservationProcessingWorker"/>
/// consume <see cref="ReservationIngestChannel"/> y cada item termina persistido a través del
/// <see cref="ReservationRepository"/> real, no del placeholder de logging. Complementa —no
/// sustituye— a <see cref="ReservationRepositoryOversellingTests"/>, que es la prueba rigurosa
/// de no-overselling a nivel de repositorio. Necesita también un RabbitMQ real porque
/// AddInfrastructure ya registra la publicación de eventos (ver
/// <see cref="ReservationEventFanoutTests"/> para la prueba del fanout en sí).
/// </summary>
public sealed class ReservationProcessingWorkerWiringTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("flashqueue")
        .WithUsername("flashqueue")
        .WithPassword("flashqueue")
        .Build();

    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:3.13-alpine")
        .WithUsername("flashqueue")
        .WithPassword("flashqueue")
        .Build();

    private IHost _host = null!;
    private NpgsqlDataSource _dataSource = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());

        _dataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());
        await new SchemaMigrator(_dataSource).EnsureSchemaAsync(CancellationToken.None);

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:FlashQueueDb"] = _postgres.GetConnectionString(),
            ["RabbitMq:Host"] = _rabbitMq.Hostname,
            ["RabbitMq:Port"] = _rabbitMq.GetMappedPublicPort(5672).ToString(),
            ["RabbitMq:VirtualHost"] = "/",
            ["RabbitMq:Username"] = "flashqueue",
            ["RabbitMq:Password"] = "flashqueue",
        });

        builder.Services.AddSingleton(new ReservationIngestChannel(new ReservationIngestOptions()));
        builder.Services.Configure<ReservationProcessingOptions>(
            builder.Configuration.GetSection(ReservationProcessingOptions.SectionName));
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddHostedService<ReservationProcessingWorker>();

        _host = builder.Build();
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        await _dataSource.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbitMq.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task ReservationProcessingWorker_ConsumesChannel_AndPersistsThroughRealRepository()
    {
        const int totalStock = 10;
        const int requestCount = 15;
        var eventId = Guid.NewGuid();

        await using (var setupConnection = await _dataSource.OpenConnectionAsync())
        {
            await setupConnection.ExecuteAsync(
                "INSERT INTO events (id, name, total_stock, reserved_stock) VALUES (@Id, @Name, @TotalStock, 0)",
                new { Id = eventId, Name = "Wiring test", TotalStock = totalStock });
        }

        var channel = _host.Services.GetRequiredService<ReservationIngestChannel>();
        for (var i = 0; i < requestCount; i++)
        {
            await channel.Writer.WriteAsync(new ReservationIngestItem(
                new ReservationRequest(Guid.NewGuid(), eventId, Guid.NewGuid(), 1, DateTimeOffset.UtcNow), default));
        }

        await using var verifyConnection = await _dataSource.OpenConnectionAsync();

        await Polling.UntilAsync(async () =>
        {
            var count = await verifyConnection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM reservations WHERE event_id = @EventId", new { EventId = eventId });
            return count >= requestCount;
        }, TimeSpan.FromSeconds(30));

        var confirmed = await verifyConnection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM reservations WHERE event_id = @EventId AND status = 'Confirmed'",
            new { EventId = eventId });
        var rejected = await verifyConnection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM reservations WHERE event_id = @EventId AND status = 'Rejected'",
            new { EventId = eventId });

        confirmed.Should().Be(totalStock, "el worker debe llamar al repositorio real, no a un placeholder que no persiste nada");
        rejected.Should().Be(requestCount - totalStock);
    }
}
