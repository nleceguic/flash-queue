using FlashQueue.Domain.Exceptions;

namespace FlashQueue.Domain.Entities;

public sealed class Reservation
{
    public Guid Id { get; }
    public Guid EventId { get; }
    public Guid UserId { get; }
    public int Quantity { get; }
    public ReservationStatus Status { get; private set; }
    public DateTimeOffset RequestedAt { get; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public string? RejectionReason { get; private set; }

    public Reservation(Guid id, Guid eventId, Guid userId, int quantity, DateTimeOffset requestedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("El identificador de la reserva no puede estar vacío.", nameof(id));
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
        Status = ReservationStatus.Pending;
    }

    public void Confirm(DateTimeOffset confirmedAt)
    {
        EnsurePending(ReservationStatus.Confirmed);

        Status = ReservationStatus.Confirmed;
        ResolvedAt = confirmedAt;
    }

    public void Reject(string reason, DateTimeOffset rejectedAt)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("El motivo de rechazo no puede estar vacío.", nameof(reason));
        }

        EnsurePending(ReservationStatus.Rejected);

        Status = ReservationStatus.Rejected;
        RejectionReason = reason;
        ResolvedAt = rejectedAt;
    }

    private void EnsurePending(ReservationStatus attemptedStatus)
    {
        if (Status != ReservationStatus.Pending)
        {
            throw new InvalidReservationStateTransitionException(Status, attemptedStatus);
        }
    }
}
