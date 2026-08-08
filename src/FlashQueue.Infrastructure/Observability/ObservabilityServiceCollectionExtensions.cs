using FlashQueue.Application.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace FlashQueue.Infrastructure.Observability;

public static class ObservabilityServiceCollectionExtensions
{
    /// <summary>
    /// Traza distribuida (ASP.NET Core + Npgsql + MassTransit + <see cref="FlashQueueDiagnostics.ActivitySource"/>)
    /// y métricas (runtime + ASP.NET Core + <see cref="FlashQueueDiagnostics.Meter"/>), exportadas
    /// vía OTLP a un único endpoint configurable (<see cref="ObservabilityOptions.OtlpEndpoint"/>).
    /// Ver docs/adr/0006-opentelemetry-collector-como-fan-out.md: el endpoint apunta a un
    /// OpenTelemetry Collector, no directamente a Tempo/Prometheus — Tempo solo entiende OTLP de
    /// trazas y Prometheus necesita remote-write para métricas, así que alguien tiene que separar
    /// ambas señales, y ese "alguien" no debería ser esta aplicación.
    /// </summary>
    /// <param name="serviceName">
    /// Recurso <c>service.name</c> de OpenTelemetry: identifica qué proceso generó cada traza o
    /// métrica en Tempo/Grafana (p. ej. "flashqueue-api", "flashqueue-workers"). A diferencia del
    /// nombre del <see cref="FlashQueueDiagnostics.ActivitySource"/>/<see cref="FlashQueueDiagnostics.Meter"/>
    /// (compartido, porque describe el mismo flujo de negocio visto desde dos procesos), esto sí
    /// varía por proceso.
    /// </param>
    public static IServiceCollection AddObservability(
        this IServiceCollection services, IConfiguration configuration, string serviceName)
    {
        var options = new ObservabilityOptions();
        configuration.GetSection(ObservabilityOptions.SectionName).Bind(options);
        var otlpEndpoint = new Uri(options.OtlpEndpoint);

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName, serviceVersion: FlashQueueDiagnostics.Version))
            .WithTracing(tracing => tracing
                .AddSource(FlashQueueDiagnostics.Name)
                .AddSource("MassTransit") // MassTransit v8 emite Activities nativamente, sin paquete adicional.
                .AddAspNetCoreInstrumentation()
                .AddNpgsql()
                .AddOtlpExporter(otlp => otlp.Endpoint = otlpEndpoint))
            .WithMetrics(metrics => metrics
                .AddMeter(FlashQueueDiagnostics.Name)
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddNpgsqlInstrumentation(_ => { })
                .AddOtlpExporter(otlp => otlp.Endpoint = otlpEndpoint));

        return services;
    }
}
