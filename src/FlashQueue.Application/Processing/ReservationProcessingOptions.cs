namespace FlashQueue.Application.Processing;

public sealed class ReservationProcessingOptions
{
    public const string SectionName = "ReservationProcessing";
    public const int DefaultMaxConcurrency = 10;
    public static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Número máximo de reservas procesándose a la vez (limita la presión sobre la base de datos).</summary>
    public int MaxConcurrency { get; set; } = DefaultMaxConcurrency;

    /// <summary>Tiempo máximo de espera, al recibir shutdown, a que terminen los procesamientos ya en vuelo.</summary>
    public TimeSpan DrainTimeout { get; set; } = DefaultDrainTimeout;
}
