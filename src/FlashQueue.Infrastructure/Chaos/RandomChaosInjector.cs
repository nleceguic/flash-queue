using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FlashQueue.Infrastructure.Chaos;

/// <summary>
/// Implementación real del modo caos (activa solo con <c>CHAOS_MODE=true</c>): inyecta latencia y,
/// con la probabilidad configurada, un fallo antes de cada llamada a Postgres o RabbitMQ.
/// </summary>
public sealed class RandomChaosInjector(ILogger<RandomChaosInjector> logger, IOptions<ChaosOptions> options) : IChaosInjector
{
    private readonly ChaosOptions _options = options.Value;

    public async Task BeforePostgresCallAsync(CancellationToken cancellationToken)
    {
        await InjectLatencyAsync("Postgres", cancellationToken);
        // Con causa SocketException, Npgsql la clasifica como transitoria y sí se reintenta.
        InjectFailureIfTriggered("Postgres", _options.PostgresFailureProbability, static () =>
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
