using Microsoft.AspNetCore.SignalR;

namespace FlashQueue.Workers.Stats;

/// <summary>Avisa por grupo (un grupo por evento) cuando una reserva se resuelve; el cliente refresca desde <c>GET /events/{id}/status</c>.</summary>
public sealed class ReservationStatsHub : Hub
{
    public Task SubscribeToEvent(string eventId) => Groups.AddToGroupAsync(Context.ConnectionId, eventId);
}
