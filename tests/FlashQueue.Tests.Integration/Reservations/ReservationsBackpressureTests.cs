using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using FlashQueue.Api.Reservations;
using FlashQueue.Application.Ingestion;
using FlashQueue.Tests.Integration.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlashQueue.Tests.Integration.Reservations;

public sealed class ReservationsBackpressureTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ReservationsBackpressureTests(WebApplicationFactory<Program> factory) => _factory = factory;

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
