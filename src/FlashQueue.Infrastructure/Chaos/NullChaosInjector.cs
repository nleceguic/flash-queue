namespace FlashQueue.Infrastructure.Chaos;

/// <summary>No-op registrado cuando CHAOS_MODE no está activo: cero coste añadido en el hot path.</summary>
public sealed class NullChaosInjector : IChaosInjector
{
    public Task BeforePostgresCallAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task BeforeRabbitMqPublishAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
