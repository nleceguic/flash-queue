namespace FlashQueue.Infrastructure.Observability;

public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";
    public const string DefaultOtlpEndpoint = "http://localhost:4317";

    /// <summary>Endpoint OTLP/gRPC para trazas y métricas; en producción se sobrescribe vía <c>Observability__OtlpEndpoint</c>.</summary>
    public string OtlpEndpoint { get; set; } = DefaultOtlpEndpoint;
}
