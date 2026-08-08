using Dapper;
using FlashQueue.Application.Processing;
using FlashQueue.Domain.Entities;
using FlashQueue.Infrastructure;
using FlashQueue.Infrastructure.Persistence;
using FlashQueue.Tests.Integration.Support;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace FlashQueue.Tests.Integration.Messaging;

/// <summary>
/// Prueba de extremo a extremo del requisito de mensajería (ver docs/adr/0003-masstransit-rabbitmq-para-eventos-de-dominio.md):
/// contra un Postgres y un RabbitMQ reales (Testcontainers, no mocks), confirma una reserva a
/// través del pipeline real (<see cref="ReservationRepository"/> + <see cref="PostgresReservationProcessor"/>)
/// y comprueba que <c>ReservationConfirmed</c> llega, de forma independiente, a los tres
/// consumidores reales de FlashQueue.Consumers.Payments/Notifications/Analytics — cada uno
/// levantado como su propio <see cref="IHost"/>, exactamente como se despliegan en producción.
/// </summary>
public sealed class ReservationEventFanoutTests : IAsyncLifetime
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

    private readonly CapturingLoggerProvider _paymentsLogs = new();
    private readonly CapturingLoggerProvider _notificationsLogs = new();
    private readonly CapturingLoggerProvider _analyticsLogs = new();

    private NpgsqlDataSource _dataSource = null!;
    private IHost _producerHost = null!;
    private IHost _paymentsHost = null!;
    private IHost _notificationsHost = null!;
    private IHost _analyticsHost = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());

        _dataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());
        await new SchemaMigrator(_dataSource).EnsureSchemaAsync(CancellationToken.None);

        var rabbitMqConfig = new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = _rabbitMq.Hostname,
            ["RabbitMq:Port"] = _rabbitMq.GetMappedPublicPort(5672).ToString(),
            ["RabbitMq:VirtualHost"] = "/",
            ["RabbitMq:Username"] = "flashqueue",
            ["RabbitMq:Password"] = "flashqueue",
        };

        _producerHost = BuildProducerHost(rabbitMqConfig);

        _paymentsHost = BuildConsumerHost(rabbitMqConfig, "payments", _paymentsLogs, x =>
        {
            x.AddConsumer<FlashQueue.Consumers.Payments.Consumers.ReservationConfirmedConsumer>();
            x.AddConsumer<FlashQueue.Consumers.Payments.Consumers.ReservationRejectedConsumer>();
        });

        _notificationsHost = BuildConsumerHost(rabbitMqConfig, "notifications", _notificationsLogs, x =>
        {
            x.AddConsumer<FlashQueue.Consumers.Notifications.Consumers.ReservationConfirmedConsumer>();
            x.AddConsumer<FlashQueue.Consumers.Notifications.Consumers.ReservationRejectedConsumer>();
        });

        _analyticsHost = BuildConsumerHost(rabbitMqConfig, "analytics", _analyticsLogs, x =>
        {
            x.AddConsumer<FlashQueue.Consumers.Analytics.Consumers.ReservationConfirmedConsumer>();
            x.AddConsumer<FlashQueue.Consumers.Analytics.Consumers.ReservationRejectedConsumer>();
        });

        await Task.WhenAll(
            _producerHost.StartAsync(),
            _paymentsHost.StartAsync(),
            _notificationsHost.StartAsync(),
            _analyticsHost.StartAsync());
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(
            _producerHost.StopAsync(),
            _paymentsHost.StopAsync(),
            _notificationsHost.StopAsync(),
            _analyticsHost.StopAsync());

        _producerHost.Dispose();
        _paymentsHost.Dispose();
        _notificationsHost.Dispose();
        _analyticsHost.Dispose();

        await _dataSource.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbitMq.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task ConfirmingAReservation_PublishesReservationConfirmed_ToAllThreeConsumers()
    {
        const int totalStock = 5;
        var eventId = Guid.NewGuid();

        await using (var setupConnection = await _dataSource.OpenConnectionAsync())
        {
            await setupConnection.ExecuteAsync(
                "INSERT INTO events (id, name, total_stock, reserved_stock) VALUES (@Id, @Name, @TotalStock, 0)",
                new { Id = eventId, Name = "Fanout test", TotalStock = totalStock });
        }

        var processor = _producerHost.Services.GetRequiredService<IReservationProcessor>();
        var request = new ReservationRequest(Guid.NewGuid(), eventId, Guid.NewGuid(), 1, DateTimeOffset.UtcNow);

        await processor.ProcessAsync(request, CancellationToken.None);

        var reservationId = request.Id.ToString();

        // Se espera al mensaje de "completado" de cada consumidor (no al primero que aparezca):
        // cada uno simula latencia con Task.Delay antes de terminar, así que un mensaje que solo
        // contenga el id de la reserva podría ser el de "iniciando", no el de que ya terminó.
        await Polling.UntilAsync(
            () => _paymentsLogs.Messages.Any(m => m.Contains(reservationId) && m.Contains("Cobro simulado completado")),
            TimeSpan.FromSeconds(30));
        await Polling.UntilAsync(
            () => _notificationsLogs.Messages.Any(m => m.Contains(reservationId) && m.Contains("Email de confirmación enviado")),
            TimeSpan.FromSeconds(30));
        await Polling.UntilAsync(
            () => _analyticsLogs.Messages.Any(m => m.Contains(reservationId) && m.Contains("Evento de analítica registrado")),
            TimeSpan.FromSeconds(30));

        _paymentsLogs.Messages.Should().Contain(
            m => m.Contains("[Payments]") && m.Contains(reservationId) && m.Contains("Cobro simulado completado"));
        _notificationsLogs.Messages.Should().Contain(
            m => m.Contains("[Notifications]") && m.Contains(reservationId) && m.Contains("Email de confirmación enviado"));
        _analyticsLogs.Messages.Should().Contain(
            m => m.Contains("[Analytics]") && m.Contains(reservationId) && m.Contains("Evento de analítica registrado"));
    }

    private IHost BuildProducerHost(IReadOnlyDictionary<string, string?> rabbitMqConfig)
    {
        var builder = Host.CreateApplicationBuilder();
        var config = new Dictionary<string, string?>(rabbitMqConfig)
        {
            ["ConnectionStrings:FlashQueueDb"] = _postgres.GetConnectionString(),
        };
        builder.Configuration.AddInMemoryCollection(config);

        builder.Services.AddInfrastructure(builder.Configuration);

        return builder.Build();
    }

    private static IHost BuildConsumerHost(
        IReadOnlyDictionary<string, string?> rabbitMqConfig,
        string serviceName,
        CapturingLoggerProvider loggerProvider,
        Action<IBusRegistrationConfigurator> configureConsumers)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(rabbitMqConfig);
        builder.Logging.AddProvider(loggerProvider);

        builder.Services.AddRabbitMqMessaging(builder.Configuration, serviceName, configureConsumers);

        return builder.Build();
    }
}
