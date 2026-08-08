namespace FlashQueue.Infrastructure.Observability;

public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";
    public const string DefaultOtlpEndpoint = "http://localhost:4317";

    /// <summary>
    /// Endpoint OTLP/gRPC al que se exportan tanto trazas como métricas. Apunta por defecto al
    /// OpenTelemetry Collector local de <c>docker-compose.observability.yml</c>; en producción
    /// (servidor Ubuntu self-hosted) se sobrescribe vía configuración/variables de entorno
    /// (p. ej. <c>Observability__OtlpEndpoint</c>) para apuntar al collector remoto.
    /// </summary>
    public string OtlpEndpoint { get; set; } = DefaultOtlpEndpoint;
}
