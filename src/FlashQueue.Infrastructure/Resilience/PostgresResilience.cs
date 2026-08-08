using FlashQueue.Infrastructure.Persistence;
using Npgsql;
using Polly;
using Polly.Retry;

namespace FlashQueue.Infrastructure.Resilience;

/// <summary>Política de reintento ante fallos transitorios de Postgres, usada por <see cref="ReservationRepository"/>.</summary>
public static class PostgresResilience
{
    /// <summary>Transitorio según Npgsql (conexión, timeout, deadlock) — nunca para constraints o validación.</summary>
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
