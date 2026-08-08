using FlashQueue.Application.Processing;
using FlashQueue.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace FlashQueue.Infrastructure.Persistence;

/// <summary>
/// Implementación real de <see cref="IReservationProcessor"/> sobre Postgres. Sustituye a
/// <c>LoggingReservationProcessor</c> (placeholder de FlashQueue.Workers) ahora que existe
/// el motor de reserva descrito en CLAUDE.md, sección 2, punto 3.
/// </summary>
public sealed class PostgresReservationProcessor(
    ReservationRepository repository, ILogger<PostgresReservationProcessor> logger) : IReservationProcessor
{
    public async Task ProcessAsync(ReservationRequest request, CancellationToken cancellationToken)
    {
        var reservation = await repository.ReserveAsync(request, cancellationToken);

        if (reservation.Status == ReservationStatus.Confirmed)
        {
            logger.LogInformation(
                "Reserva {ReservationId} del evento {EventId} confirmada ({Quantity} unidades).",
                reservation.Id, reservation.EventId, reservation.Quantity);
        }
        else
        {
            logger.LogInformation(
                "Reserva {ReservationId} del evento {EventId} rechazada: {Reason}",
                reservation.Id, reservation.EventId, reservation.RejectionReason);
        }
    }
}
