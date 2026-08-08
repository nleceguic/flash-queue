using FlashQueue.Domain.Entities;

namespace FlashQueue.Domain.Exceptions;

public sealed class InvalidReservationStateTransitionException : DomainException
{
    public ReservationStatus CurrentStatus { get; }
    public ReservationStatus AttemptedStatus { get; }

    public InvalidReservationStateTransitionException(ReservationStatus currentStatus, ReservationStatus attemptedStatus)
        : base($"No se puede transicionar una reserva de {currentStatus} a {attemptedStatus}.")
    {
        CurrentStatus = currentStatus;
        AttemptedStatus = attemptedStatus;
    }
}
