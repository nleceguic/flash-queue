namespace FlashQueue.Infrastructure.Messaging;

/// <summary>Publica eventos de dominio a RabbitMQ protegido por circuit breaker + timeout (ver <see cref="RabbitMqPublishResiliencePipelineProvider"/>).</summary>
public interface IReservationEventPublisher
{
    Task PublishAsync<T>(T message, CancellationToken cancellationToken) where T : class;
}
