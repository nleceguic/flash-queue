namespace FlashQueue.Infrastructure.Chaos;

/// <summary>
/// No-op: se registra cuando CHAOS_MODE no está activo. Cada método es una devolución inmediata
/// de <see cref="Task.CompletedTask"/> sin ninguna comprobación — cero coste añadido en el hot
/// path de <c>ReservationRepository</c>/<c>ReservationEventPublisher</c> más allá de la llamada
/// virtual en sí, que el JIT puede inlinear.
/// </summary>
public sealed class NullChaosInjector : IChaosInjector
{
    public Task BeforePostgresCallAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task BeforeRabbitMqPublishAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
