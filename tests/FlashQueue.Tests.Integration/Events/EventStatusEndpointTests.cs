using System.Net.Http.Json;
using Dapper;
using FlashQueue.Infrastructure;
using FlashQueue.Infrastructure.Persistence;
using FlashQueue.Workers.Events;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace FlashQueue.Tests.Integration.Events;

/// <summary>Prueba <c>GET /events/{eventId}/status</c> contra un Postgres real.</summary>
public sealed class EventStatusEndpointTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("flashqueue")
        .WithUsername("flashqueue")
        .WithPassword("flashqueue")
        .Build();

    // AddInfrastructure también configura MassTransit; hace falta un broker real aunque este endpoint no lo use.
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:3.13-alpine")
        .WithUsername("flashqueue")
        .WithPassword("flashqueue")
        .Build();

    private WebApplication _app = null!;
    private NpgsqlDataSource _dataSource = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());

        _dataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());
        await new SchemaMigrator(_dataSource).EnsureSchemaAsync(CancellationToken.None);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:FlashQueueDb"] = _postgres.GetConnectionString(),
            ["RabbitMq:Host"] = _rabbitMq.Hostname,
            ["RabbitMq:Port"] = _rabbitMq.GetMappedPublicPort(5672).ToString(),
            ["RabbitMq:VirtualHost"] = "/",
            ["RabbitMq:Username"] = "flashqueue",
            ["RabbitMq:Password"] = "flashqueue",
        });

        builder.Services.AddInfrastructure(builder.Configuration);

        _app = builder.Build();
        _app.MapEventStatusEndpoint();

        await _app.StartAsync();

        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _dataSource.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbitMq.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task GetEventStatus_ForExistingEvent_ReturnsStockAndReservationCounts()
    {
        var eventId = Guid.NewGuid();

        await using (var connection = await _dataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "INSERT INTO events (id, name, total_stock, reserved_stock) VALUES (@Id, @Name, @TotalStock, @ReservedStock)",
                new { Id = eventId, Name = "Evento de prueba", TotalStock = 500, ReservedStock = 120 });

            await connection.ExecuteAsync(
                """
                INSERT INTO reservations (id, event_id, user_id, quantity, status, requested_at, resolved_at, rejection_reason)
                VALUES
                    (@Id1, @EventId, @UserId, 1, 'Confirmed', now(), now(), NULL),
                    (@Id2, @EventId, @UserId, 1, 'Rejected', now(), now(), 'Sin stock')
                """,
                new { Id1 = Guid.NewGuid(), Id2 = Guid.NewGuid(), EventId = eventId, UserId = Guid.NewGuid() });
        }

        var response = await _client.GetFromJsonAsync<EventStatusResponse>($"/events/{eventId}/status");

        response.Should().NotBeNull();
        response!.EventId.Should().Be(eventId);
        response.Name.Should().Be("Evento de prueba");
        response.TotalStock.Should().Be(500);
        response.ReservedStock.Should().Be(120);
        response.AvailableStock.Should().Be(380);
        response.ConfirmedReservations.Should().Be(1);
        response.RejectedReservations.Should().Be(1);
    }

    [Fact]
    public async Task GetEventStatus_ForUnknownEvent_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"/events/{Guid.NewGuid()}/status");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetEventStatus_NeverReportsReservedStockAboveTotalStock()
    {
        // No es la prueba rigurosa de no-overselling (esa es ReservationRepositoryOversellingTests);
        // solo comprueba que el endpoint refleja la restricción ya impuesta por el esquema.
        var eventId = Guid.NewGuid();

        await using (var connection = await _dataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "INSERT INTO events (id, name, total_stock, reserved_stock) VALUES (@Id, @Name, @TotalStock, @ReservedStock)",
                new { Id = eventId, Name = "Evento agotado", TotalStock = 500, ReservedStock = 500 });
        }

        var response = await _client.GetFromJsonAsync<EventStatusResponse>($"/events/{eventId}/status");

        response!.ReservedStock.Should().BeLessThanOrEqualTo(response.TotalStock);
        response.AvailableStock.Should().Be(0);
    }
}
