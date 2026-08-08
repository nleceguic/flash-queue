using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FlashQueue.Infrastructure.Chaos;

/// <summary>
/// Implementación real del modo caos: se registra solo cuando <c>CHAOS_MODE=true</c> (ver
/// <see cref="ChaosServiceCollectionExtensions"/>). Antes de cada llamada a Postgres o RabbitMQ,
/// inyecta una latencia aleatoria y, con la probabilidad configurada, un fallo — para forzar en
/// caliente el retry de Postgres y el circuit breaker de RabbitMQ (ver
/// docs/adr/0004-polly-retry-postgres-circuit-breaker-rabbitmq.md).
/// </summary>
public sealed class RandomChaosInjector(ILogger<RandomChaosInjector> logger, IOptions<ChaosOptions> options) : IChaosInjector
{
    private readonly ChaosOptions _options = options.Value;

    public async Task BeforePostgresCallAsync(CancellationToken cancellationToken)
    {
        await InjectLatencyAsync("Postgres", cancellationToken);
        InjectFailureIfTriggered("Postgres", _options.PostgresFailureProbability, static () =>
            // NpgsqlException con una excepción de socket como causa se clasifica como transitoria
            // (NpgsqlException.IsTransient), así que PostgresResilience.IsTransientFailure la
            // reintenta de verdad — igual que un fallo de conexión real.
            new NpgsqlException(
                "[CHAOS] Fallo de conexión a Postgres inyectado artificialmente.",
                new SocketException((int)SocketError.ConnectionReset)));
    }

    public async Task BeforeRabbitMqPublishAsync(CancellationToken cancellationToken)
    {
        await InjectLatencyAsync("RabbitMQ", cancellationToken);
        InjectFailureIfTriggered("RabbitMQ", _options.RabbitMqFailureProbability, static () =>
            new ChaosInjectedException("[CHAOS] Fallo de publicación a RabbitMQ inyectado artificialmente."));
    }

    private async Task InjectLatencyAsync(string dependency, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(
            Random.Shared.Next((int)_options.MinLatency.TotalMilliseconds, (int)_options.MaxLatency.TotalMilliseconds + 1));

        logger.LogWarning(
            "[CHAOS] Inyectando {DelayMs}ms de latencia artificial antes de la llamada a {Dependency}.",
            delay.TotalMilliseconds, dependency);

        await Task.Delay(delay, cancellationToken);
    }

    private void InjectFailureIfTriggered(string dependency, double failureProbability, Func<Exception> exceptionFactory)
    {
        if (Random.Shared.NextDouble() >= failureProbability)
        {
            return;
        }

        var exception = exceptionFactory();
        logger.LogWarning(exception, "[CHAOS] Inyectando fallo artificial en la llamada a {Dependency}.", dependency);

        throw exception;
    }
}
