namespace FlashQueue.Application.Stats;

/// <summary>Avisa de que una reserva se resolvió, sin llevar el recuento — Postgres sigue siendo la fuente de verdad.</summary>
public interface IReservationStatsNotifier
{
    Task NotifyReservationResolvedAsync(Guid eventId, CancellationToken cancellationToken);
}
