using FlashQueue.Infrastructure.Chaos;
using MassTransit;

namespace FlashQueue.Infrastructure.Messaging;

/// <summary>
/// Depende de <see cref="IBus"/> (singleton), no de <see cref="IPublishEndpoint"/> (scoped, pensado
/// para publicar desde un consumer): aquí se publica fuera de cualquier consume context.
/// </summary>
public sealed class ReservationEventPublisher(
    IBus bus, RabbitMqPublishResiliencePipelineProvider resilienceProvider, IChaosInjector chaosInjector)
    : IReservationEventPublisher
{
    public Task PublishAsync<T>(T message, CancellationToken cancellationToken) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);

        // El caos vive dentro del pipeline: un fallo artificial cuenta como fallo real para el circuit breaker.
        return resilienceProvider.Pipeline.ExecuteAsync(
            static async (state, ct) =>
            {
                await state.ChaosInjector.BeforeRabbitMqPublishAsync(ct);
                await state.Bus.Publish(state.Message, ct);
            },
            (Bus: bus, Message: message, ChaosInjector: chaosInjector),
            cancellationToken).AsTask();
    }
}
