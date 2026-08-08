namespace FlashQueue.Api.Reservations;

public sealed record CreateReservationRequestBody(Guid UserId, int Quantity);
