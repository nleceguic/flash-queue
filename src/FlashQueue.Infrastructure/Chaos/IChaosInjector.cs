namespace FlashQueue.Infrastructure.Chaos;

/// <summary>
/// Punto de inyección de caos delante de cada dependencia externa. La implementación real
/// (<see cref="RandomChaosInjector"/>) solo se registra cuando <c>CHAOS_MODE=true</c>; en caso
/// contrario se registra <see cref="NullChaosInjector"/>, así que el código que llama a estos
/// métodos (<c>ReservationRepository</c>, <c>ReservationEventPublisher</c>) nunca comprueba un
/// flag — la decisión ya está tomada, una sola vez, al arrancar. Ver
/// docs/adr/0005-modo-caos.md.
/// </summary>
public interface IChaosInjector
{
    Task BeforePostgresCallAsync(CancellationToken cancellationToken);

    Task BeforeRabbitMqPublishAsync(CancellationToken cancellationToken);
}
