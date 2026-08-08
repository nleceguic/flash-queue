namespace FlashQueue.Infrastructure.Messaging;

public sealed class RabbitMqPublishResilienceOptions
{
    public const string SectionName = "RabbitMqPublishResilience";
    public static readonly TimeSpan DefaultPublishTimeout = TimeSpan.FromSeconds(2);
    public const int DefaultConsecutiveFailuresBeforeBreaking = 5;
    public static readonly TimeSpan DefaultSamplingDuration = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultBreakDuration = TimeSpan.FromSeconds(30);

    /// <summary>Tiempo máximo por intento individual de publicación antes de contarlo como fallo.</summary>
    public TimeSpan PublishTimeout { get; set; } = DefaultPublishTimeout;

    /// <summary>Número de fallos consecutivos que abren el circuito.</summary>
    public int ConsecutiveFailuresBeforeBreaking { get; set; } = DefaultConsecutiveFailuresBeforeBreaking;

    /// <summary>Ventana sobre la que se evalúan los fallos (con FailureRatio=1.0, un éxito la reinicia).</summary>
    public TimeSpan SamplingDuration { get; set; } = DefaultSamplingDuration;

    /// <summary>Tiempo que el circuito permanece abierto (fail-fast) antes de pasar a semiabierto.</summary>
    public TimeSpan BreakDuration { get; set; } = DefaultBreakDuration;
}
