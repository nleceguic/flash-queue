using FlashQueue.Infrastructure.Chaos;
using MassTransit;

namespace FlashQueue.Infrastructure.Messaging;

/// <summary>
/// Depende de <see cref="IBus"/>, no de <see cref="IPublishEndpoint"/>: MassTransit registra
/// <c>IPublishEndpoint</c> como scoped (pensado para publicar desde dentro de un consumidor, con
/// el contexto del mensaje en curso) mientras que <c>IBus</c> es singleton, que es lo correcto
/// aquí — <see cref="PostgresReservationProcessor"/> publica fuera de cualquier consume context,
/// disparado por <c>ReservationProcessingWorker</c>, no por un mensaje recibido.
/// </summary>
public sealed class ReservationEventPublisher(
    IBus bus, RabbitMqPublishResiliencePipelineProvider resilienceProvider, IChaosInjector chaosInjector)
    : IReservationEventPublisher
{
    public Task PublishAsync<T>(T message, CancellationToken cancellationToken) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);

        // La inyección de caos vive DENTRO del delegado que ejecuta el pipeline: un fallo
        // artificial cuenta como un fallo real de cara al circuit breaker (y el timeout también
        // se aplica a la latencia artificial, igual que a una publicación real lenta).
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
