namespace FlashQueue.Infrastructure.Chaos;

public sealed class ChaosOptions
{
    public const string SectionName = "Chaos";
    public static readonly TimeSpan DefaultMinLatency = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan DefaultMaxLatency = TimeSpan.FromMilliseconds(2000);
    public const double DefaultPostgresFailureProbability = 0.05;
    public const double DefaultRabbitMqFailureProbability = 0.10;

    /// <summary>Límite inferior de la latencia artificial inyectada antes de cada llamada.</summary>
    public TimeSpan MinLatency { get; set; } = DefaultMinLatency;

    /// <summary>Límite superior de la latencia artificial inyectada antes de cada llamada.</summary>
    public TimeSpan MaxLatency { get; set; } = DefaultMaxLatency;

    /// <summary>Probabilidad (0.0–1.0) de fallar artificialmente una llamada a Postgres.</summary>
    public double PostgresFailureProbability { get; set; } = DefaultPostgresFailureProbability;

    /// <summary>Probabilidad (0.0–1.0) de fallar artificialmente una publicación a RabbitMQ.</summary>
    public double RabbitMqFailureProbability { get; set; } = DefaultRabbitMqFailureProbability;
}
