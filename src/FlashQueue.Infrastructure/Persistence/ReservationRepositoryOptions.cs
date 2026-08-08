namespace FlashQueue.Infrastructure.Persistence;

public sealed class ReservationRepositoryOptions
{
    public const string SectionName = "ReservationRepository";
    public static readonly TimeSpan DefaultLockAcquisitionTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultLockRetryDelay = TimeSpan.FromMilliseconds(2);

    /// <summary>Tiempo máximo total esperando a adquirir el lock de fila del evento antes de abandonar.</summary>
    public TimeSpan LockAcquisitionTimeout { get; set; } = DefaultLockAcquisitionTimeout;

    /// <summary>Espera base entre reintentos de <c>SELECT ... FOR UPDATE SKIP LOCKED</c> cuando la fila está ocupada.</summary>
    public TimeSpan LockRetryDelay { get; set; } = DefaultLockRetryDelay;
}
