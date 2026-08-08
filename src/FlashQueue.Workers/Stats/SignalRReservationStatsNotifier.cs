using FlashQueue.Application.Stats;
using Microsoft.AspNetCore.SignalR;

namespace FlashQueue.Workers.Stats;

/// <summary>Reenvía el aviso de reserva resuelta al grupo de SignalR de ese evento (ver <see cref="ReservationStatsHub"/>).</summary>
public sealed class SignalRReservationStatsNotifier(IHubContext<ReservationStatsHub> hubContext) : IReservationStatsNotifier
{
    public Task NotifyReservationResolvedAsync(Guid eventId, CancellationToken cancellationToken) =>
        hubContext.Clients.Group(eventId.ToString()).SendAsync("reservationResolved", eventId.ToString(), cancellationToken);
}
