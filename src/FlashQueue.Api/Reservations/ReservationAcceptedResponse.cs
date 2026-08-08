namespace FlashQueue.Api.Reservations;

public sealed record ReservationAcceptedResponse(
    Guid ReservationId,
    Guid EventId,
    int Quantity,
    DateTimeOffset QueuedAt);
