using System.Diagnostics;
using FlashQueue.Application.Observability;
using FlashQueue.Infrastructure.Observability;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace FlashQueue.Tests.Unit.Observability;

/// <summary>Prueba <see cref="ObservabilityServiceCollectionExtensions.AddObservability"/> sin backend OTLP real, con un <c>InMemoryExporter</c> propio.</summary>
public class ObservabilityServiceCollectionExtensionsTests
{
    [Fact]
    public void AddObservability_SubscribesTracerProviderAndMeterProvider_ToTheSharedInstrumentation()
    {
        var services = new ServiceCollection();
        // IP literal en vez de "localhost": falla la conexión al instante en vez de tardar varios
        // segundos en resolver el hostname (visto en Windows) cuando el exportador OTLP hace ForceFlush.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Observability:OtlpEndpoint"] = "http://127.0.0.1:1",
            })
            .Build();

        services.AddObservability(configuration, serviceName: "test-service");

        var exportedActivities = new List<Activity>();
        var exportedMetrics = new List<Metric>();
        services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddInMemoryExporter(exportedActivities))
            .WithMetrics(metrics => metrics.AddInMemoryExporter(exportedMetrics));

        using var provider = services.BuildServiceProvider();

        // Resolverlos fuerza su construcción perezosa (normalmente ocurre al arrancar el host).
        var tracerProvider = provider.GetRequiredService<TracerProvider>();
        var meterProvider = provider.GetRequiredService<MeterProvider>();

        var operationName = $"test.observability.{Guid.NewGuid()}";
        using (var activity = FlashQueueDiagnostics.ActivitySource.StartActivity(operationName))
        {
            activity.Should().NotBeNull(
                "AddObservability debe registrar un listener para FlashQueueDiagnostics.ActivitySource " +
                "(AddSource(FlashQueueDiagnostics.Name)); sin listener, StartActivity devuelve null.");
        }

        var correlationTag = new KeyValuePair<string, object?>("correlation.id", operationName);
        FlashQueueDiagnostics.ReservationsProcessed.Add(1, correlationTag);
        FlashQueueDiagnostics.ProcessingDuration.Record(123.4, correlationTag);

        tracerProvider.ForceFlush();
        meterProvider.ForceFlush();

        exportedActivities.Should().Contain(a => a.OperationName == operationName);

        exportedMetrics.Should().Contain(m => m.Name == "flashqueue.reservations.processed");
        exportedMetrics.Should().Contain(m => m.Name == "flashqueue.reservations.processing_duration");
    }

    [Fact]
    public void AddObservability_RegistersTracerProviderAndMeterProvider_ResolvableFromDi()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Observability:OtlpEndpoint"] = "http://127.0.0.1:1",
            })
            .Build();

        services.AddObservability(configuration, serviceName: "test-service");

        using var provider = services.BuildServiceProvider();

        var act = () =>
        {
            provider.GetRequiredService<TracerProvider>();
            provider.GetRequiredService<MeterProvider>();
        };

        act.Should().NotThrow("AddObservability debe registrar ambos providers y aceptar la configuración por defecto sin lanzar");
    }
}
