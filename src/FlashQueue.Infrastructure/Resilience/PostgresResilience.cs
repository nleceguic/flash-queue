using FlashQueue.Infrastructure.Persistence;
using Npgsql;
using Polly;
using Polly.Retry;

namespace FlashQueue.Infrastructure.Resilience;

/// <summary>
/// Política de reintento ante fallos transitorios de Postgres (conexión perdida, timeout de red),
/// usada por <see cref="ReservationRepository"/>. Ver
/// docs/adr/0004-polly-retry-postgres-circuit-breaker-rabbitmq.md.
/// </summary>
public static class PostgresResilience
{
    /// <summary>
    /// Un fallo se considera transitorio si Npgsql lo marca como tal
    /// (<see cref="NpgsqlException.IsTransient"/>): errores de clase de conexión (08xxx), timeouts,
    /// deadlocks o fallos de serialización. Nunca es <see langword="true"/> para violaciones de
    /// constraint (23xxx) ni errores de validación de datos (22xxx) — Npgsql ya los distingue por
    /// nosotros a través del <c>SqlState</c> del error.
    /// </summary>
    public static bool IsTransientFailure(Exception? exception) =>
        exception is NpgsqlException { IsTransient: true };

    public static ResiliencePipeline<T> BuildTransientFaultPipeline<T>(ReservationRepositoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new ResiliencePipelineBuilder<T>()
            .AddRetry(new RetryStrategyOptions<T>
            {
                ShouldHandle = args => new ValueTask<bool>(IsTransientFailure(args.Outcome.Exception)),
                MaxRetryAttempts = options.TransientFaultMaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = options.TransientFaultBaseDelay,
            })
            .Build();
    }
}
