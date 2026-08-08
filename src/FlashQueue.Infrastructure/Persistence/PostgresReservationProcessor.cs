using System.Diagnostics;
using FlashQueue.Application.Observability;
using FlashQueue.Application.Processing;
using FlashQueue.Application.Stats;
using FlashQueue.Contracts.Events;
using FlashQueue.Domain.Entities;
using FlashQueue.Infrastructure.Messaging;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace FlashQueue.Infrastructure.Persistence;

/// <summary>Implementación real de <see cref="IReservationProcessor"/> sobre Postgres.</summary>
public sealed class PostgresReservationProcessor(
    ReservationRepository repository,
    IReservationEventPublisher eventPublisher,
    IReservationStatsNotifier statsNotifier,
    ILogger<PostgresReservationProcessor> logger)
    : IReservationProcessor
{
    public async Task ProcessAsync(ReservationRequest request, CancellationToken cancellationToken)
    {
        var reservation = await repository.ReserveAsync(request, cancellationToken);
        var resolvedAt = reservation.ResolvedAt
            ?? throw new InvalidOperationException("Una reserva resuelta por ReserveAsync siempre debe tener ResolvedAt.");

        var statusTag = new KeyValuePair<string, object?>("reservation.status", reservation.Status.ToString());
        Activity.Current?.SetTag(statusTag.Key, statusTag.Value);

        FlashQueueDiagnostics.ReservationsProcessed.Add(1, statusTag);
        FlashQueueDiagnostics.ProcessingDuration.Record((resolvedAt - request.RequestedAt).TotalMilliseconds, statusTag);

        // Best-effort: un fallo notificando al panel en vivo no debe tumbar la reserva.
        try
        {
            await statsNotifier.NotifyReservationResolvedAsync(reservation.EventId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo notificar al panel en vivo sobre la reserva {ReservationId}.", reservation.Id);
        }

        // La reserva ya está persistida: un fallo publicando no la deshace ni se reintenta.
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
