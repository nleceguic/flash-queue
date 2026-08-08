namespace FlashQueue.Contracts.Events;

public sealed record ReservationConfirmed(
    Guid ReservationId,
    Guid EventId,
    Guid UserId,
    DateTimeOffset Timestamp);
