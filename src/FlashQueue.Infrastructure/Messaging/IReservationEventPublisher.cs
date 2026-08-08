namespace FlashQueue.Infrastructure.Messaging;

/// <summary>
/// Publica eventos de dominio a RabbitMQ. Puerto pequeño y propio, en vez de exponer
/// <c>IPublishEndpoint</c>/<c>IBus</c> de MassTransit directamente, para que
/// <c>PostgresReservationProcessor</c> no dependa de MassTransit y para poder sustituirlo por un
/// doble de prueba sin arrancar un bus real.
/// </summary>
public interface IReservationEventPublisher
{
    Task PublishAsync<T>(T message, CancellationToken cancellationToken) where T : class;
}
