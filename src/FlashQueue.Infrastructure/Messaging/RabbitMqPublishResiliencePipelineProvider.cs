using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;

namespace FlashQueue.Infrastructure.Messaging;

/// <summary>
/// Construye, una única vez, el pipeline de resiliencia (circuit breaker + timeout) que protege
/// la publicación a RabbitMQ, y expone su <see cref="CircuitBreakerStateProvider"/> para que
/// <c>/health/dependencies</c> pueda leer el estado del circuito sin acoplarse a Polly. Ver
/// docs/adr/0004-polly-retry-postgres-circuit-breaker-rabbitmq.md.
/// </summary>
public sealed class RabbitMqPublishResiliencePipelineProvider
{
    public RabbitMqPublishResiliencePipelineProvider(IOptions<RabbitMqPublishResilienceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;

        CircuitBreakerStateProvider = new CircuitBreakerStateProvider();

        // Orden deliberado: el circuit breaker va FUERA del timeout. Así, con el circuito
        // abierto, una llamada falla al instante (BrokenCircuitException) sin siquiera intentar
        // publicar ni esperar los 2s del timeout interno.
        Pipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                // FailureRatio = 1.0 + MinimumThroughput = N emula "N fallos consecutivos": una
                // sola publicación exitosa dentro de SamplingDuration hace que el ratio baje de
                // 1.0 y el circuito no se abra (ver RabbitMqPublishResilienceOptions.SamplingDuration).
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
