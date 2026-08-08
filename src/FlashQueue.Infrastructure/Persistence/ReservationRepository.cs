using Dapper;
using FlashQueue.Domain.Entities;
using FlashQueue.Domain.Exceptions;
using FlashQueue.Infrastructure.Chaos;
using FlashQueue.Infrastructure.Resilience;
using Npgsql;
using Polly;

namespace FlashQueue.Infrastructure.Persistence;

/// <summary>
/// Reserva stock de forma segura bajo concurrencia: bloquea la fila del evento con
/// <c>SELECT ... FOR UPDATE SKIP LOCKED</c>, decide Confirmed/Rejected según el stock
/// disponible en ese instante, y persiste el cambio de stock + la reserva en la misma
/// transacción. Ver docs/adr/0002-locking-skip-locked-con-reintentos.md para el porqué
/// de SKIP LOCKED (con reintentos) frente a un FOR UPDATE que bloquea, y
/// docs/adr/0004-polly-retry-postgres-circuit-breaker-rabbitmq.md para el reintento adicional
/// ante fallos transitorios de conexión (mecanismo distinto e independiente del anterior).
/// </summary>
public sealed class ReservationRepository
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _timeProvider;
    private readonly ReservationRepositoryOptions _options;
    private readonly IChaosInjector _chaosInjector;
    private readonly ResiliencePipeline<Reservation> _transientFaultPipeline;

    public ReservationRepository(
        NpgsqlDataSource dataSource, TimeProvider timeProvider, ReservationRepositoryOptions options, IChaosInjector chaosInjector)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(chaosInjector);

        _dataSource = dataSource;
        _timeProvider = timeProvider;
        _options = options;
        _chaosInjector = chaosInjector;
        _transientFaultPipeline = PostgresResilience.BuildTransientFaultPipeline<Reservation>(options);
    }

    public Task<Reservation> ReserveAsync(ReservationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Todo el método es seguro de reintentar como unidad: nada se compromete hasta el
        // CommitAsync final, así que un fallo transitorio a mitad de camino no deja escritura
        // parcial alguna que limpiar.
        return _transientFaultPipeline.ExecuteAsync(
            static (state, ct) => new ValueTask<Reservation>(state.Self.ReserveCoreAsync(state.Request, ct)),
            (Self: this, Request: request),
            cancellationToken).AsTask();
    }

    private async Task<Reservation> ReserveCoreAsync(ReservationRequest request, CancellationToken cancellationToken)
    {
        // Con el modo caos desactivado esto resuelve a NullChaosInjector: una llamada virtual a un
        // método que solo devuelve Task.CompletedTask, sin ningún condicional aquí. Dentro del
        // pipeline de reintento (ReserveAsync): cada reintento vuelve a pasar por aquí, así que un
        // fallo inyectado se comporta igual que un fallo transitorio real de la conexión.
        await _chaosInjector.BeforePostgresCallAsync(cancellationToken);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM events WHERE id = @EventId)",
            new { request.EventId },
            transaction,
            cancellationToken: cancellationToken));

        if (!exists)
        {
            throw new EventNotFoundException(request.EventId);
        }

        var stock = await AcquireEventStockAsync(connection, transaction, request.EventId, cancellationToken);

        var reservation = new Reservation(request.Id, request.EventId, request.UserId, request.Quantity, request.RequestedAt);
        var now = _timeProvider.GetUtcNow();

        if (request.Quantity <= stock.TotalStock - stock.ReservedStock)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE events SET reserved_stock = reserved_stock + @Quantity WHERE id = @EventId",
                new { request.Quantity, request.EventId },
                transaction,
                cancellationToken: cancellationToken));

            reservation.Confirm(now);
        }
        else
        {
            reservation.Reject("Stock insuficiente en el momento de procesar la reserva.", now);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO reservations (id, event_id, user_id, quantity, status, requested_at, resolved_at, rejection_reason)
            VALUES (@Id, @EventId, @UserId, @Quantity, @Status, @RequestedAt, @ResolvedAt, @RejectionReason)
            """,
            new
            {
                reservation.Id,
                reservation.EventId,
                reservation.UserId,
                reservation.Quantity,
                Status = reservation.Status.ToString(),
                reservation.RequestedAt,
                reservation.ResolvedAt,
                reservation.RejectionReason,
            },
            transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);

        return reservation;
    }

    /// <summary>
    /// Reintenta <c>SELECT ... FOR UPDATE SKIP LOCKED</c> mientras la fila esté ocupada por otra
    /// transacción (0 filas devueltas no distingue "ocupada" de "inexistente"; la existencia ya
    /// se comprobó antes de llamar aquí). Cada intento fallido no bloquea: Postgres nunca hace
    /// esperar al llamante, así que el reintento con una espera corta es responsabilidad nuestra.
    /// </summary>
    private async Task<EventStockRow> AcquireEventStockAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid eventId, CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + _options.LockAcquisitionTimeout;

        while (true)
        {
            var row = await connection.QuerySingleOrDefaultAsync<EventStockRow>(new CommandDefinition(
                """
                SELECT total_stock AS TotalStock, reserved_stock AS ReservedStock
                FROM events
                WHERE id = @EventId
                FOR UPDATE SKIP LOCKED
                """,
                new { EventId = eventId },
                transaction,
                cancellationToken: cancellationToken));

            if (row is not null)
            {
                return row;
            }

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                throw new TimeoutException(
                    $"No se pudo adquirir el bloqueo de fila del evento {eventId} dentro de {_options.LockAcquisitionTimeout}.");
            }

            var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 3));
            await Task.Delay(_options.LockRetryDelay + jitter, cancellationToken);
        }
    }

    private sealed class EventStockRow
    {
        public int TotalStock { get; init; }
        public int ReservedStock { get; init; }
    }
}
