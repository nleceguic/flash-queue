using FlashQueue.Application.Processing;
using FlashQueue.Domain.Entities;

namespace FlashQueue.Workers.Processing;

/// <summary>
/// Implementación provisional de <see cref="IReservationProcessor"/> mientras no existe el
/// motor de reserva sobre Postgres (ver CLAUDE.md, sección 2, punto 3). Se sustituirá por la
/// implementación con SELECT ... FOR UPDATE SKIP LOCKED.
/// </summary>
public sealed class LoggingReservationProcessor(ILogger<LoggingReservationProcessor> logger) : IReservationProcessor
{
    public Task ProcessAsync(ReservationRequest request, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Procesando reserva {ReservationId} del evento {EventId} (cantidad: {Quantity})",
            request.Id, request.EventId, request.Quantity);

        return Task.CompletedTask;
    }
}
