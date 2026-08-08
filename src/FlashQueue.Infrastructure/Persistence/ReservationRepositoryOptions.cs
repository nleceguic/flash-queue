namespace FlashQueue.Infrastructure.Persistence;

public sealed class ReservationRepositoryOptions
{
    public const string SectionName = "ReservationRepository";
    public static readonly TimeSpan DefaultLockAcquisitionTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultLockRetryDelay = TimeSpan.FromMilliseconds(2);
    public const int DefaultTransientFaultMaxRetryAttempts = 3;
    public static readonly TimeSpan DefaultTransientFaultBaseDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>Tiempo máximo total esperando a adquirir el lock de fila del evento antes de abandonar.</summary>
    public TimeSpan LockAcquisitionTimeout { get; set; } = DefaultLockAcquisitionTimeout;

    /// <summary>Espera base entre reintentos de <c>SELECT ... FOR UPDATE SKIP LOCKED</c> cuando la fila está ocupada.</summary>
    public TimeSpan LockRetryDelay { get; set; } = DefaultLockRetryDelay;

    /// <summary>Reintentos ante un fallo transitorio de Postgres (ver <c>PostgresResilience</c>); no afecta al bucle de SKIP LOCKED.</summary>
    public int TransientFaultMaxRetryAttempts { get; set; } = DefaultTransientFaultMaxRetryAttempts;

    /// <summary>Retardo base (antes de aplicar backoff exponencial + jitter) del primer reintento transitorio.</summary>
    public TimeSpan TransientFaultBaseDelay { get; set; } = DefaultTransientFaultBaseDelay;
}
