using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using FlashQueue.Api.Reservations;
using FlashQueue.Tests.Integration.Support;
using FlashQueue.Workers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace FlashQueue.Tests.Integration.Reservations;

/// <summary>Retira el worker real para que no drene el channel saturado antes de medir la respuesta de /health.</summary>
public sealed class ReservationsResponsivenessTests : IAsyncLifetime
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
    public async Task GetHealth_StaysResponsive_WhileIngestChannelIsSaturated()
    {
        await using var saturatedFactory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ReservationIngest:Capacity"] = "2" })));

        using var client = saturatedFactory.CreateClient();
        using var writesCts = new CancellationTokenSource();
        var eventId = Guid.NewGuid();

        // Calienta el host antes de medir para no contaminar la medición con el arranque en frío.
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
