using System.Net.Http.Json;
using Dapper;
using FlashQueue.Api.Reservations;
using FlashQueue.Infrastructure.Persistence;
using FlashQueue.Tests.Integration.Support;
using FlashQueue.Workers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace FlashQueue.Tests.Integration.Stats;

/// <summary>Prueba con un cliente SignalR real que el hub avisa solo a los suscriptores del evento correcto.</summary>
public sealed class ReservationStatsHubTests : IAsyncLifetime
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
    public async Task ResolvingAReservation_NotifiesOnlySubscribersOfThatEvent()
    {
        var subscribedEventId = Guid.NewGuid();
        var otherEventId = Guid.NewGuid();

        await using (var setupConnection = await _dataSource.OpenConnectionAsync())
        {
            await setupConnection.ExecuteAsync(
                "INSERT INTO events (id, name, total_stock, reserved_stock) VALUES (@Id, @Name, @TotalStock, 0)",
                new[]
                {
                    new { Id = subscribedEventId, Name = "Suscrito", TotalStock = 10 },
                    new { Id = otherEventId, Name = "No suscrito", TotalStock = 10 },
                });
        }

        // Habla con el TestServer en memoria en vez de abrir una conexión de red real.
        await using var hubConnection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/reservation-stats"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            })
            .Build();

        var notifiedEventIds = new List<string>();
        hubConnection.On<string>("reservationResolved", id => notifiedEventIds.Add(id));

        await hubConnection.StartAsync();
        await hubConnection.InvokeAsync("SubscribeToEvent", subscribedEventId.ToString());

        using var client = _factory.CreateClient();

        var otherResponse = await client.PostAsJsonAsync(
            $"/events/{otherEventId}/reservations", new CreateReservationRequestBody(Guid.NewGuid(), 1));
        otherResponse.EnsureSuccessStatusCode();

        var subscribedResponse = await client.PostAsJsonAsync(
            $"/events/{subscribedEventId}/reservations", new CreateReservationRequestBody(Guid.NewGuid(), 1));
        subscribedResponse.EnsureSuccessStatusCode();

        await Polling.UntilAsync(() => notifiedEventIds.Count > 0, TimeSpan.FromSeconds(15));

        await Task.Delay(TimeSpan.FromMilliseconds(500)); // margen por si llega un aviso tardío

        notifiedEventIds.Should().ContainSingle().Which.Should().Be(subscribedEventId.ToString());
    }
}
