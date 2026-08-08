namespace FlashQueue.Application.Stats;

/// <summary>No-op registrado por defecto, para no depender de que exista un panel en vivo escuchando.</summary>
public sealed class NullReservationStatsNotifier : IReservationStatsNotifier
{
    public Task NotifyReservationResolvedAsync(Guid eventId, CancellationToken cancellationToken) => Task.CompletedTask;
}
