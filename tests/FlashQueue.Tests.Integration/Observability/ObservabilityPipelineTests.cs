using System.Diagnostics;
using System.Net.Http.Json;
using Dapper;
using FlashQueue.Api.Reservations;
using FlashQueue.Infrastructure.Persistence;
using FlashQueue.Tests.Integration.Support;
using FlashQueue.Workers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace FlashQueue.Tests.Integration.Observability;

/// <summary>Confirma que la traza sigue conectada más allá de "reservation.process" y que las métricas de throughput/latencia se actualizan.</summary>
public sealed class ObservabilityPipelineTests : IAsyncLifetime
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

    private readonly List<Activity> _exportedActivities = [];
    private readonly List<Metric> _exportedMetrics = [];

    private WebApplicationFactory<Program> _factory = null!;
    private NpgsqlDataSource _dataSource = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());

        _dataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());
        await new SchemaMigrator(_dataSource).EnsureSchemaAsync(CancellationToken.None);

        InfrastructureEnvironmentVariables.SetFor(_postgres, _rabbitMq);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddOpenTelemetry()
                .WithTracing(tracing => tracing.AddInMemoryExporter(_exportedActivities))
                .WithMetrics(metrics => metrics.AddInMemoryExporter(_exportedMetrics))));
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        InfrastructureEnvironmentVariables.Clear();
        await _dataSource.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbitMq.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task ProcessingAReservation_ProducesAConnectedTrace_AndUpdatesCustomMetrics()
    {
        var eventId = Guid.NewGuid();

        await using (var setupConnection = await _dataSource.OpenConnectionAsync())
        {
            await setupConnection.ExecuteAsync(
                "INSERT INTO events (id, name, total_stock, reserved_stock) VALUES (@Id, @Name, @TotalStock, 0)",
                new { Id = eventId, Name = "Observability pipeline test", TotalStock = 10 });
        }

        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/events/{eventId}/reservations", new CreateReservationRequestBody(Guid.NewGuid(), 1));
        response.EnsureSuccessStatusCode();
        var accepted = await response.Content.ReadFromJsonAsync<ReservationAcceptedResponse>();
        accepted.Should().NotBeNull();

        await using var verifyConnection = await _dataSource.OpenConnectionAsync();
        await Polling.UntilAsync(async () =>
        {
            var count = await verifyConnection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM reservations WHERE id = @Id", new { Id = accepted!.ReservationId });
            return count >= 1;
        }, TimeSpan.FromSeconds(30));

        var tracerProvider = _factory.Services.GetRequiredService<TracerProvider>();
        var meterProvider = _factory.Services.GetRequiredService<MeterProvider>();
        tracerProvider.ForceFlush();
        meterProvider.ForceFlush();

        var enqueueActivity = _exportedActivities.Should().ContainSingle(a =>
            a.OperationName == "reservation.enqueue"
            && Equals(a.GetTagItem("reservation.id"), accepted!.ReservationId)).Which;

        var processActivity = _exportedActivities.Should().ContainSingle(a =>
            a.OperationName == "reservation.process"
            && Equals(a.GetTagItem("reservation.id"), accepted!.ReservationId)).Which;

        processActivity.TraceId.Should().Be(enqueueActivity.TraceId,
            "reservation.process debe reconectar la traza de reservation.enqueue a través del channel (ADR 0006/0013), no abrir una nueva");

        // Npgsql y MassTransit publican mientras reservation.process sigue siendo Activity.Current.
        _exportedActivities.Should().Contain(
            a => a.TraceId == processActivity.TraceId && a.Source.Name == "Npgsql",
            "la llamada real a Postgres debe quedar dentro de la misma traza que reservation.process");

        _exportedActivities.Should().Contain(
            a => a.TraceId == processActivity.TraceId && a.Source.Name == "MassTransit",
            "la publicación real a RabbitMQ debe quedar dentro de la misma traza que reservation.process");

        _exportedMetrics.Should().Contain(
            m => m.Name == "flashqueue.reservations.processed" && HasTag(m, "reservation.status", "Confirmed"),
            "PostgresReservationProcessor debe incrementar el contador de throughput al confirmar la reserva");
        _exportedMetrics.Should().Contain(
            m => m.Name == "flashqueue.reservations.processing_duration" && HasTag(m, "reservation.status", "Confirmed"),
            "PostgresReservationProcessor debe registrar la latencia de extremo a extremo de la reserva");
    }

    private static bool HasTag(Metric metric, string key, string value)
    {
        foreach (ref readonly var point in metric.GetMetricPoints())
        {
            foreach (var tag in point.Tags)
            {
                if (tag.Key == key && Equals(tag.Value?.ToString(), value))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
