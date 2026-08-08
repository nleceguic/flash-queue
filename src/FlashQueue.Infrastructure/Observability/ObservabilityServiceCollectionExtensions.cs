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
    /// <summary>Traza y métricas de ASP.NET Core, Npgsql, MassTransit y <see cref="FlashQueueDiagnostics"/>, exportadas vía OTLP a un Collector (ADR 0006).</summary>
    /// <param name="serviceName">Recurso <c>service.name</c> de OpenTelemetry.</param>
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
