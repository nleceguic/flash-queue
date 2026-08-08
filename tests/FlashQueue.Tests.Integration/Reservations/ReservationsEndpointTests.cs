using System.Net;
using System.Net.Http.Json;
using Dapper;
using FlashQueue.Api.Reservations;
using FlashQueue.Infrastructure.Persistence;
using FlashQueue.Tests.Integration.Support;
using FlashQueue.Workers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace FlashQueue.Tests.Integration.Reservations;

/// <summary>Extremo a extremo contra el proceso unificado (ADR 0013): cada reserva aceptada debe quedar persistida exactamente una vez.</summary>
public sealed class ReservationsEndpointTests : IAsyncLifetime
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

    private WebApplicationFactory<Program> _factory = null!;
    private NpgsqlDataSource _dataSource = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());

        _dataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());
        await new SchemaMigrator(_dataSource).EnsureSchemaAsync(CancellationToken.None);

        InfrastructureEnvironmentVariables.SetFor(_postgres, _rabbitMq);
        _factory = new WebApplicationFactory<Program>();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        InfrastructureEnvironmentVariables.Clear();
        await _dataSource.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbitMq.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task PostReservations_With2000ConcurrentRequests_PersistsEveryRequestExactlyOnce()
    {
        const int requestCount = 2000;
        var eventId = Guid.NewGuid();

        await using (var setupConnection = await _dataSource.OpenConnectionAsync())
        {
            await setupConnection.ExecuteAsync(
                "INSERT INTO events (id, name, total_stock, reserved_stock) VALUES (@Id, @Name, @TotalStock, 0)",
                new { Id = eventId, Name = "Responsiveness test", TotalStock = requestCount });
        }

        using var client = _factory.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, requestCount).Select(async _ =>
        {
            var response = await client.PostAsJsonAsync(
                $"/events/{eventId}/reservations", new CreateReservationRequestBody(Guid.NewGuid(), 1));
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            return await response.Content.ReadFromJsonAsync<ReservationAcceptedResponse>();
        }));

        await using var verifyConnection = await _dataSource.OpenConnectionAsync();

        await Polling.UntilAsync(async () =>
        {
            var count = await verifyConnection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM reservations WHERE event_id = @EventId", new { EventId = eventId });
            return count >= requestCount;
        }, TimeSpan.FromSeconds(30));

        var returnedIds = responses.Select(r => r!.ReservationId).ToList();
        returnedIds.Should().OnlyHaveUniqueItems();
        returnedIds.Should().HaveCount(requestCount);

        var persistedIds = (await verifyConnection.QueryAsync<Guid>(
            "SELECT id FROM reservations WHERE event_id = @EventId", new { EventId = eventId })).ToList();

        persistedIds.Should().BeEquivalentTo(returnedIds,
            "cada reserva aceptada por HTTP debe llegar exactamente una vez al worker real, que comparte el mismo channel (ADR 0013)");
    }
}
