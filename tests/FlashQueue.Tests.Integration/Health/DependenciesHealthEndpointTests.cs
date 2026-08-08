using System.Diagnostics;
using System.Net.Http.Json;
using Dapper;
using FlashQueue.Api.Reservations;
using FlashQueue.Infrastructure.Chaos;
using FlashQueue.Infrastructure.Persistence;
using FlashQueue.Tests.Integration.Support;
using FlashQueue.Workers;
using FlashQueue.Workers.Health;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace FlashQueue.Tests.Integration.Health;

/// <summary>Fuerza los tres estados del circuit breaker de <c>GET /health/dependencies</c> con <see cref="ToggleableChaosInjector"/>.</summary>
public sealed class DependenciesHealthEndpointTests : IAsyncLifetime
{
    // Valor por defecto real, sin sobrescribir.
    private const int ConsecutiveFailuresBeforeBreaking = 5;

    // En producción son 30s; se acorta solo para no hacer el test lento sin ganar nada.
    private static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(2);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("flashqueue")
        .WithUsername("flashqueue")
        .WithPassword("flashqueue")
        .Build();

    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:3.13-alpine")
        .WithUsername("flashqueue")
        .WithPassword("flashqueue")
        .Build();

    private readonly ToggleableChaosInjector _chaosInjector = new();

    private WebApplicationFactory<Program> _factory = null!;
    private NpgsqlDataSource _dataSource = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());

        _dataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());
        await new SchemaMigrator(_dataSource).EnsureSchemaAsync(CancellationToken.None);

        InfrastructureEnvironmentVariables.SetFor(_postgres, _rabbitMq);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMqPublishResilience:ConsecutiveFailuresBeforeBreaking"] = ConsecutiveFailuresBeforeBreaking.ToString(),
                ["RabbitMqPublishResilience:BreakDuration"] = BreakDuration.ToString(),
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IChaosInjector>();
                services.AddSingleton<IChaosInjector>(_chaosInjector);
            });
        });
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        InfrastructureEnvironmentVariables.Clear();
        await _dataSource.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbitMq.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task DependenciesHealth_WithClosedCircuit_RespondsClosedWithMinimalLatency()
    {
        using var client = _factory.CreateClient();

        // Calienta el host antes de medir para no contaminar la latencia con el arranque en frío.
        await client.GetAsync("/health");

        var stopwatch = Stopwatch.StartNew();
        var health = await client.GetFromJsonAsync<DependenciesHealthResponse>("/health/dependencies");
        stopwatch.Stop();

        var rabbitMq = health!.Dependencies.Should().ContainSingle(d => d.Name == "rabbitmq-publish").Which;
        rabbitMq.CircuitState.Should().Be("Closed");
        rabbitMq.Healthy.Should().BeTrue();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500),
            "el endpoint solo lee un estado en memoria (CircuitBreakerStateProvider), nunca debe llamar de verdad a RabbitMQ");
    }

    [Fact]
    public async Task DependenciesHealth_ReflectsFullCircuitLifecycle_ClosedThenOpenThenRecovered()
    {
        var eventId = Guid.NewGuid();
        await using (var setupConnection = await _dataSource.OpenConnectionAsync())
        {
            await setupConnection.ExecuteAsync(
                "INSERT INTO events (id, name, total_stock, reserved_stock) VALUES (@Id, @Name, @TotalStock, 0)",
                new { Id = eventId, Name = "Circuit breaker health lifecycle test", TotalStock = 100 });
        }

        using var client = _factory.CreateClient();

        (await GetRabbitMqHealthAsync(client)).CircuitState.Should().Be("Closed",
            "todavía no se ha publicado nada, el circuito arranca cerrado por defecto");

        // Fuerza el circuito a abrirse; un par de más por margen, no hace falta acertar el índice exacto.
        _chaosInjector.FailRabbitMqPublish = true;

        for (var i = 0; i < ConsecutiveFailuresBeforeBreaking + 2; i++)
        {
            var response = await client.PostAsJsonAsync(
                $"/events/{eventId}/reservations", new CreateReservationRequestBody(Guid.NewGuid(), 1));
            response.EnsureSuccessStatusCode();
        }

        await Polling.UntilAsync(
            async () => (await GetRabbitMqHealthAsync(client)).CircuitState == "Open",
            TimeSpan.FromSeconds(15));

        var openState = await GetRabbitMqHealthAsync(client);
        openState.CircuitState.Should().Be("Open");
        openState.Healthy.Should().BeFalse("con el circuito abierto, la dependencia debe reportarse como no saludable");

        _chaosInjector.FailRabbitMqPublish = false;
        await Task.Delay(BreakDuration + TimeSpan.FromMilliseconds(500));

        // Polly no reevalúa el circuito con un timer de fondo: hace falta una llamada más tras
        // vencer BreakDuration para que pase a HalfOpen y, si tiene éxito, a Closed.
        var recoveryResponse = await client.PostAsJsonAsync(
            $"/events/{eventId}/reservations", new CreateReservationRequestBody(Guid.NewGuid(), 1));
        recoveryResponse.EnsureSuccessStatusCode();

        await Polling.UntilAsync(
            async () => (await GetRabbitMqHealthAsync(client)).CircuitState is "HalfOpen" or "Closed",
            TimeSpan.FromSeconds(15));

        var recoveredState = await GetRabbitMqHealthAsync(client);
        recoveredState.CircuitState.Should().BeOneOf("HalfOpen", "Closed",
            "tras una publicación que ya no falla, el circuito debe abandonar Open — HalfOpen es la " +
            "transición inmediata a Closed y puede no ser observable si la ventana de polling la salta");
    }

    private static async Task<DependencyHealth> GetRabbitMqHealthAsync(HttpClient client)
    {
        var response = await client.GetFromJsonAsync<DependenciesHealthResponse>("/health/dependencies");
        return response!.Dependencies.Single(d => d.Name == "rabbitmq-publish");
    }
}
