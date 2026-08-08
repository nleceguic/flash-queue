using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using FlashQueue.Api.Reservations;
using FlashQueue.Application.Ingestion;
using FlashQueue.Tests.Integration.Support;
using FlashQueue.Workers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace FlashQueue.Tests.Integration.Reservations;

/// <summary>Retira el worker real del host para observar el backpressure con un lector artificial controlado.</summary>
public sealed class ReservationsBackpressureTests : IAsyncLifetime
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

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());

        InfrastructureEnvironmentVariables.SetFor(_postgres, _rabbitMq);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => HostedServiceRemoval.Remove<ReservationProcessingWorker>(services)));
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        InfrastructureEnvironmentVariables.Clear();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbitMq.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task PostReservations_WhenChannelIsFull_WaitsInsteadOfFailing()
    {
        const int capacity = 5;
        const int requestCount = 40;
        var drainDelay = TimeSpan.FromMilliseconds(75);

        await using var smallCapacityFactory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ReservationIngest:Capacity"] = capacity.ToString() })));

        using var client = smallCapacityFactory.CreateClient();
        var ingestChannel = smallCapacityFactory.Services.GetRequiredService<ReservationIngestChannel>();

        var drainedCount = 0;
        using var drainCts = new CancellationTokenSource();
        var slowDrainTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in ingestChannel.Reader.ReadAllAsync(drainCts.Token))
                {
                    Interlocked.Increment(ref drainedCount);
                    await Task.Delay(drainDelay, drainCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        var eventId = Guid.NewGuid();
        var stopwatch = Stopwatch.StartNew();

        var responses = await Task.WhenAll(Enumerable.Range(0, requestCount).Select(_ =>
            client.PostAsJsonAsync($"/events/{eventId}/reservations", new CreateReservationRequestBody(Guid.NewGuid(), 1))));

        stopwatch.Stop();

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Accepted);
        stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(1000),
            "sin backpressure real, 40 POSTs a un endpoint que solo encola tardarían milisegundos");

        await Polling.UntilAsync(() => Volatile.Read(ref drainedCount) >= requestCount, TimeSpan.FromSeconds(5));
        drainCts.Cancel();
        await slowDrainTask;

        drainedCount.Should().Be(requestCount);
    }
}
