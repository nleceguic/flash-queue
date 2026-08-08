using FlashQueue.Infrastructure.Chaos;

namespace FlashQueue.Tests.Integration.Support;

/// <summary>Doble de <see cref="IChaosInjector"/> con fallo de RabbitMQ activable/desactivable a mano, para el circuit breaker (ADR 0004).</summary>
internal sealed class ToggleableChaosInjector : IChaosInjector
{
    public volatile bool FailRabbitMqPublish;

    public Task BeforePostgresCallAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task BeforeRabbitMqPublishAsync(CancellationToken cancellationToken)
    {
        if (FailRabbitMqPublish)
        {
            throw new ChaosInjectedException("[TEST] Fallo de publicación a RabbitMQ forzado por ToggleableChaosInjector.");
        }

        return Task.CompletedTask;
    }
}
