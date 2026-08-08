using FlashQueue.Contracts.Events;
using FlashQueue.Infrastructure.Chaos;
using FlashQueue.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace FlashQueue.Tests.Unit.Messaging;

/// <summary>
/// Verifica, con un <see cref="MassTransit.IBus"/> fake que falla N veces (sin RabbitMQ real),
/// que <see cref="ReservationEventPublisher"/> abre el circuito tras los fallos consecutivos
/// configurados, deja de invocar realmente el endpoint mientras está abierto (fail-fast) y
/// aplica el timeout por intento. Ver docs/adr/0004-polly-retry-postgres-circuit-breaker-rabbitmq.md.
/// </summary>
public class ReservationEventPublisherTests
{
    private static readonly ReservationConfirmed Message = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);

    [Fact]
    public async Task PublishAsync_AfterConsecutiveFailures_OpensCircuitAndFailsFastWithoutCallingEndpoint()
    {
        const int consecutiveFailuresBeforeBreaking = 5;
        var provider = CreateProvider(new RabbitMqPublishResilienceOptions
        {
            ConsecutiveFailuresBeforeBreaking = consecutiveFailuresBeforeBreaking,
            SamplingDuration = TimeSpan.FromSeconds(2),
            BreakDuration = TimeSpan.FromSeconds(2),
            PublishTimeout = TimeSpan.FromSeconds(2),
        });
        var endpoint = new FakeBus(_ => throw new InvalidOperationException("broker inalcanzable (simulado)"));
        var publisher = new ReservationEventPublisher(endpoint, provider, new NullChaosInjector());

        for (var i = 0; i < consecutiveFailuresBeforeBreaking; i++)
        {
            var act = () => publisher.PublishAsync(Message, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>(
                "los primeros fallos deben propagar el error real, el circuito aún está cerrado");
        }

        endpoint.CallCount.Should().Be(consecutiveFailuresBeforeBreaking);
        provider.CircuitBreakerStateProvider.CircuitState.Should().Be(CircuitState.Open);

        // Con el circuito abierto: falla al instante con BrokenCircuitException y NUNCA llega a
        // invocar el endpoint real. Esta es la comprobación de fail-fast, no solo "también falla".
        var actAfterOpen = () => publisher.PublishAsync(Message, CancellationToken.None);
        await actAfterOpen.Should().ThrowAsync<BrokenCircuitException>();

        endpoint.CallCount.Should().Be(
            consecutiveFailuresBeforeBreaking, "con el circuito abierto no debe volver a invocarse el endpoint real");
    }

    [Fact]
    public async Task PublishAsync_WithIsolatedSuccessBetweenFailures_NeverOpensTheCircuit()
    {
        var provider = CreateProvider(new RabbitMqPublishResilienceOptions
        {
            ConsecutiveFailuresBeforeBreaking = 5,
            SamplingDuration = TimeSpan.FromSeconds(2),
            BreakDuration = TimeSpan.FromSeconds(2),
            PublishTimeout = TimeSpan.FromSeconds(2),
        });

        var callCount = 0;
        var endpoint = new FakeBus(_ =>
        {
            callCount++;
            // Falla salvo en la 5ª llamada: nunca hay 5 fallos SEGUIDOS.
            return callCount == 5 ? Task.CompletedTask : throw new InvalidOperationException("fallo simulado");
        });
        var publisher = new ReservationEventPublisher(endpoint, provider, new NullChaosInjector());

        for (var i = 0; i < 8; i++)
        {
            try
            {
                await publisher.PublishAsync(Message, CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // esperado en 7 de las 8 llamadas
            }
        }

        endpoint.CallCount.Should().Be(8, "el circuito nunca debió abrirse, así que todas las llamadas llegan al endpoint");
        provider.CircuitBreakerStateProvider.CircuitState.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public async Task PublishAsync_WhenEndpointExceedsPublishTimeout_FailsFastWithTimeoutRejectedException()
    {
        var provider = CreateProvider(new RabbitMqPublishResilienceOptions
        {
            ConsecutiveFailuresBeforeBreaking = 5,
            SamplingDuration = TimeSpan.FromSeconds(5),
            BreakDuration = TimeSpan.FromSeconds(5),
            PublishTimeout = TimeSpan.FromMilliseconds(200),
        });
        var endpoint = new FakeBus(async ct => await Task.Delay(TimeSpan.FromSeconds(10), ct));
        var publisher = new ReservationEventPublisher(endpoint, provider, new NullChaosInjector());

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var act = () => publisher.PublishAsync(Message, CancellationToken.None);
        await act.Should().ThrowAsync<TimeoutRejectedException>();
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(5), "el timeout de 200ms por intento debe cortar la espera de 10s del endpoint simulado");
    }

    private static RabbitMqPublishResiliencePipelineProvider CreateProvider(RabbitMqPublishResilienceOptions options) =>
        new(Options.Create(options));
}
