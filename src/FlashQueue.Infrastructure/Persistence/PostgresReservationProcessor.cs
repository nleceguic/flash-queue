using FlashQueue.Application.Processing;
using FlashQueue.Contracts.Events;
using FlashQueue.Domain.Entities;
using FlashQueue.Infrastructure.Messaging;
using Microsoft.Extensions.Logging;

namespace FlashQueue.Infrastructure.Persistence;

/// <summary>
/// Implementación real de <see cref="IReservationProcessor"/> sobre Postgres. Sustituye a
/// <c>LoggingReservationProcessor</c> (placeholder de FlashQueue.Workers) ahora que existe
/// el motor de reserva descrito en CLAUDE.md, sección 2, punto 3.
/// </summary>
public sealed class PostgresReservationProcessor(
    ReservationRepository repository, IReservationEventPublisher eventPublisher, ILogger<PostgresReservationProcessor> logger)
    : IReservationProcessor
{
    public async Task ProcessAsync(ReservationRequest request, CancellationToken cancellationToken)
    {
        var reservation = await repository.ReserveAsync(request, cancellationToken);
        var resolvedAt = reservation.ResolvedAt
            ?? throw new InvalidOperationException("Una reserva resuelta por ReserveAsync siempre debe tener ResolvedAt.");

        if (reservation.Status == ReservationStatus.Confirmed)
        {
            logger.LogInformation(
                "Reserva {ReservationId} del evento {EventId} confirmada ({Quantity} unidades).",
                reservation.Id, reservation.EventId, reservation.Quantity);

            await eventPublisher.PublishAsync(
                new ReservationConfirmed(reservation.Id, reservation.EventId, reservation.UserId, resolvedAt),
                cancellationToken);
        }
        else
        {
            logger.LogInformation(
                "Reserva {ReservationId} del evento {EventId} rechazada: {Reason}",
                reservation.Id, reservation.EventId, reservation.RejectionReason);

            await eventPublisher.PublishAsync(
                new ReservationRejected(
                    reservation.Id, reservation.EventId, reservation.UserId, resolvedAt, reservation.RejectionReason!),
                cancellationToken);
        }
    }
}
