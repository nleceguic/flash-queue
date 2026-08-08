using MassTransit;

namespace FlashQueue.Tests.Unit.Messaging;

/// <summary>Doble de <see cref="IBus"/>: solo implementa el overload de <c>Publish</c> que usa <c>ReservationEventPublisher</c>, el resto lanza.</summary>
internal sealed class FakeBus(Func<CancellationToken, Task> behavior) : IBus
{
    public int CallCount { get; private set; }

    public Task Publish<T>(T message, CancellationToken cancellationToken) where T : class
    {
        CallCount++;
        return behavior(cancellationToken);
    }

    public Uri Address => throw new NotSupportedException();

    public IBusTopology Topology => throw new NotSupportedException();

    public Task Publish<T>(T message, IPipe<PublishContext<T>> publishPipe, CancellationToken cancellationToken)
        where T : class => throw new NotSupportedException();

    public Task Publish<T>(T message, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken)
        where T : class => throw new NotSupportedException();

    public Task Publish(object message, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task Publish(object message, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task Publish(object message, Type messageType, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task Publish(object message, Type messageType, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task Publish<T>(object values, CancellationToken cancellationToken) where T : class =>
        throw new NotSupportedException();

    public Task Publish<T>(object values, IPipe<PublishContext<T>> publishPipe, CancellationToken cancellationToken)
        where T : class => throw new NotSupportedException();

    public Task Publish<T>(object values, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken)
        where T : class => throw new NotSupportedException();

    public ConnectHandle ConnectPublishObserver(IPublishObserver observer) => throw new NotSupportedException();

    public Task<ISendEndpoint> GetPublishSendEndpoint<T>() where T : class => throw new NotSupportedException();

    public Task<ISendEndpoint> GetSendEndpoint(Uri address) => throw new NotSupportedException();

    public ConnectHandle ConnectSendObserver(ISendObserver observer) => throw new NotSupportedException();

    public ConnectHandle ConnectConsumeMessageObserver<T>(IConsumeMessageObserver<T> observer) where T : class =>
        throw new NotSupportedException();

    public ConnectHandle ConnectConsumeObserver(IConsumeObserver observer) => throw new NotSupportedException();

    public ConnectHandle ConnectConsumePipe<T>(IPipe<ConsumeContext<T>> pipe) where T : class =>
        throw new NotSupportedException();

    public ConnectHandle ConnectConsumePipe<T>(IPipe<ConsumeContext<T>> pipe, ConnectPipeOptions options) where T : class =>
        throw new NotSupportedException();

    public ConnectHandle ConnectRequestPipe<T>(Guid requestId, IPipe<ConsumeContext<T>> pipe) where T : class =>
        throw new NotSupportedException();

    public ConnectHandle ConnectReceiveEndpointObserver(IReceiveEndpointObserver observer) =>
        throw new NotSupportedException();

    public ConnectHandle ConnectReceiveObserver(IReceiveObserver observer) => throw new NotSupportedException();

    public ConnectHandle ConnectEndpointConfigurationObserver(IEndpointConfigurationObserver observer) =>
        throw new NotSupportedException();

    public HostReceiveEndpointHandle ConnectReceiveEndpoint(
        IEndpointDefinition definition, IEndpointNameFormatter? endpointNameFormatter = null,
        Action<IReceiveEndpointConfigurator>? configureEndpoint = null) =>
        throw new NotSupportedException();

    public HostReceiveEndpointHandle ConnectReceiveEndpoint(
        string queueName, Action<IReceiveEndpointConfigurator>? configureEndpoint = null) =>
        throw new NotSupportedException();

    public void Probe(ProbeContext context) => throw new NotSupportedException();
}
