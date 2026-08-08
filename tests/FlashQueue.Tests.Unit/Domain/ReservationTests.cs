using FlashQueue.Domain.Entities;
using FlashQueue.Domain.Exceptions;
using FluentAssertions;

namespace FlashQueue.Tests.Unit.Domain;

public class ReservationTests
{
    private static Reservation CreatePendingReservation() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2, DateTimeOffset.UtcNow);

    [Fact]
    public void NewReservation_StartsAsPending()
    {
        var reservation = CreatePendingReservation();

        reservation.Status.Should().Be(ReservationStatus.Pending);
        reservation.ResolvedAt.Should().BeNull();
    }

    [Fact]
    public void Confirm_FromPending_TransitionsToConfirmed()
    {
        var reservation = CreatePendingReservation();
        var confirmedAt = DateTimeOffset.UtcNow;

        reservation.Confirm(confirmedAt);

        reservation.Status.Should().Be(ReservationStatus.Confirmed);
        reservation.ResolvedAt.Should().Be(confirmedAt);
    }

    [Fact]
    public void Reject_FromPending_TransitionsToRejectedWithReason()
    {
        var reservation = CreatePendingReservation();

        reservation.Reject("Sin stock disponible", DateTimeOffset.UtcNow);

        reservation.Status.Should().Be(ReservationStatus.Rejected);
        reservation.RejectionReason.Should().Be("Sin stock disponible");
    }

    [Fact]
    public void Confirm_WhenAlreadyConfirmed_ThrowsAndReservationNeverReturnsToPending()
    {
        var reservation = CreatePendingReservation();
        reservation.Confirm(DateTimeOffset.UtcNow);

        var act = () => reservation.Confirm(DateTimeOffset.UtcNow);

        act.Should().Throw<InvalidReservationStateTransitionException>();
        reservation.Status.Should().Be(ReservationStatus.Confirmed, "una reserva confirmada es un estado terminal y nunca debe volver a Pending");
    }

    [Fact]
    public void Reject_WhenAlreadyConfirmed_ThrowsInvalidReservationStateTransitionException()
    {
        var reservation = CreatePendingReservation();
        reservation.Confirm(DateTimeOffset.UtcNow);

        var act = () => reservation.Reject("Motivo tardío", DateTimeOffset.UtcNow);

        act.Should().Throw<InvalidReservationStateTransitionException>();
        reservation.Status.Should().Be(ReservationStatus.Confirmed);
    }

    [Fact]
    public void Confirm_WhenAlreadyRejected_ThrowsInvalidReservationStateTransitionException()
    {
        var reservation = CreatePendingReservation();
        reservation.Reject("Sin stock disponible", DateTimeOffset.UtcNow);

        var act = () => reservation.Confirm(DateTimeOffset.UtcNow);

        act.Should().Throw<InvalidReservationStateTransitionException>();
        reservation.Status.Should().Be(ReservationStatus.Rejected, "una reserva rechazada es un estado terminal");
    }

    [Fact]
    public void Reject_WithEmptyReason_ThrowsArgumentExceptionAndStaysPending()
    {
        var reservation = CreatePendingReservation();

        var act = () => reservation.Reject(string.Empty, DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentException>();
        reservation.Status.Should().Be(ReservationStatus.Pending);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Constructor_WithNonPositiveQuantity_ThrowsArgumentOutOfRangeException(int quantity)
    {
        var act = () => new Reservation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), quantity, DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_WithEmptyId_ThrowsArgumentException()
    {
        var act = () => new Reservation(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentException>();
    }
}
