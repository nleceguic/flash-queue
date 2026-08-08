namespace FlashQueue.Domain.Entities;

public sealed class ReservationRequest
{
    public Guid Id { get; }
    public Guid EventId { get; }
    public Guid UserId { get; }
    public int Quantity { get; }
    public DateTimeOffset RequestedAt { get; }

    public ReservationRequest(Guid id, Guid eventId, Guid userId, int quantity, DateTimeOffset requestedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("El identificador de la solicitud no puede estar vacío.", nameof(id));
        }

        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("El identificador del evento no puede estar vacío.", nameof(eventId));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("El identificador del usuario no puede estar vacío.", nameof(userId));
        }

        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "La cantidad solicitada debe ser mayor que cero.");
        }

        Id = id;
        EventId = eventId;
        UserId = userId;
        Quantity = quantity;
        RequestedAt = requestedAt;
    }
}
