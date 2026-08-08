using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;

namespace FlashQueue.Infrastructure.Messaging;

/// <summary>
/// Pipeline de resiliencia (circuit breaker + timeout) para la publicación a RabbitMQ, con su
/// <see cref="CircuitBreakerStateProvider"/> expuesto para que <c>/health/dependencies</c> lea
/// el estado del circuito sin acoplarse a Polly.
/// </summary>
public sealed class RabbitMqPublishResiliencePipelineProvider
{
    public RabbitMqPublishResiliencePipelineProvider(IOptions<RabbitMqPublishResilienceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;

        CircuitBreakerStateProvider = new CircuitBreakerStateProvider();

        // El circuit breaker va fuera del timeout: con el circuito abierto, falla al instante
        // en vez de esperar el timeout interno.
        Pipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                // FailureRatio 1.0 + MinimumThroughput = N emula "N fallos consecutivos".
                FailureRatio = 1.0,
                MinimumThroughput = value.ConsecutiveFailuresBeforeBreaking,
                SamplingDuration = value.SamplingDuration,
                BreakDuration = value.BreakDuration,
                StateProvider = CircuitBreakerStateProvider,
            })
            .AddTimeout(value.PublishTimeout)
            .Build();
    }

    public ResiliencePipeline Pipeline { get; }

    public CircuitBreakerStateProvider CircuitBreakerStateProvider { get; }
}
