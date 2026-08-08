using FlashQueue.Application.Processing;
using FlashQueue.Contracts.Events;
using FlashQueue.Domain.Entities;
using FlashQueue.Infrastructure.Messaging;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Polly.Timeout;

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

        // La reserva ya quedó persistida en Postgres en este punto (ver ADR 0004): si RabbitMQ
        // está caído o el circuito está abierto, se registra el fallo de publicación y se
        // continúa — nunca se deshace ni se reintenta la reserva por un problema del broker.
        try
        {
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
        catch (Exception ex) when (ex is BrokenCircuitException or TimeoutRejectedException)
        {
            logger.LogWarning(
                ex,
                "No se pudo publicar el evento de la reserva {ReservationId} (circuito RabbitMQ {CircuitState}): " +
                "la reserva ya está persistida, el evento se pierde para esta ejecución.",
                reservation.Id, ex is BrokenCircuitException ? "abierto" : "timeout");
        }
    }
}
