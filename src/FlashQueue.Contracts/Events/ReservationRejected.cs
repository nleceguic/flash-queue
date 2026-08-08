namespace FlashQueue.Contracts.Events;

public sealed record ReservationRejected(
    Guid ReservationId,
    Guid EventId,
    Guid UserId,
    DateTimeOffset Timestamp,
    string Reason);
