using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using FlashQueue.Api.Reservations;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace FlashQueue.Tests.Integration.Reservations;

public sealed class ReservationsResponsivenessTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ReservationsResponsivenessTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task GetHealth_StaysResponsive_WhileIngestChannelIsSaturated()
    {
        await using var saturatedFactory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ReservationIngest:Capacity"] = "2" })));

        using var client = saturatedFactory.CreateClient();
        using var writesCts = new CancellationTokenSource();
        var eventId = Guid.NewGuid();

        // Calienta el host (JIT, construcción del contenedor de DI, primera conexión) antes de
        // medir, para que el arranque en frío de WebApplicationFactory no contamine la medición.
        using (var warmup = await client.GetAsync("/health"))
        {
            warmup.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var pendingPosts = Enumerable.Range(0, 20)
            .Select(_ => client.PostAsJsonAsync(
                $"/events/{eventId}/reservations", new CreateReservationRequestBody(Guid.NewGuid(), 1), writesCts.Token))
            .ToList();

        await Task.Delay(200);

        var stopwatch = Stopwatch.StartNew();
        using var healthResponse = await client.GetAsync("/health");
        stopwatch.Stop();

        healthResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1),
            "las escrituras pendientes por backpressure no deben bloquear el pipeline de peticiones");

        writesCts.Cancel();
        foreach (var pendingPost in pendingPosts)
        {
            try
            {
                await pendingPost;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
