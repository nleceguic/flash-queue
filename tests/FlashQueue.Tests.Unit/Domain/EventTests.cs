using FlashQueue.Domain.Entities;
using FlashQueue.Domain.Exceptions;
using FluentAssertions;

namespace FlashQueue.Tests.Unit.Domain;

public class EventTests
{
    [Fact]
    public void Constructor_WithNegativeTotalStock_ThrowsArgumentOutOfRangeException()
    {
        var act = () => new Event(Guid.NewGuid(), "Concierto", -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Reserve_WithinAvailableStock_ReducesAvailableStock()
    {
        var @event = new Event(Guid.NewGuid(), "Concierto", 10);

        @event.Reserve(4);

        @event.ReservedStock.Should().Be(4);
        @event.AvailableStock.Should().Be(6);
    }

    [Fact]
    public void Reserve_ExactlyAvailableStock_LeavesZeroAvailable()
    {
        var @event = new Event(Guid.NewGuid(), "Concierto", 5);

        @event.Reserve(5);

        @event.AvailableStock.Should().Be(0);
    }

    [Fact]
    public void Reserve_MoreThanAvailableStock_ThrowsInsufficientStockException()
    {
        var @event = new Event(Guid.NewGuid(), "Concierto", 5);

        var act = () => @event.Reserve(6);

        act.Should().Throw<InsufficientStockException>();
        @event.ReservedStock.Should().Be(0, "una reserva rechazada por falta de stock no debe modificar el stock reservado");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Reserve_WithNonPositiveQuantity_ThrowsInvalidStockOperationException(int quantity)
    {
        var @event = new Event(Guid.NewGuid(), "Concierto", 10);

        var act = () => @event.Reserve(quantity);

        act.Should().Throw<InvalidStockOperationException>();
    }

    [Fact]
    public void Release_WithinReservedStock_DecreasesReservedStock()
    {
        var @event = new Event(Guid.NewGuid(), "Concierto", 10);
        @event.Reserve(3);

        @event.Release(3);

        @event.ReservedStock.Should().Be(0);
    }

    [Fact]
    public void Release_MoreThanReservedStock_ThrowsAndKeepsReservedStockNonNegative()
    {
        var @event = new Event(Guid.NewGuid(), "Concierto", 10);
        @event.Reserve(3);

        var act = () => @event.Release(4);

        act.Should().Throw<InvalidStockOperationException>();
        @event.ReservedStock.Should().Be(3, "el stock reservado nunca debe quedar negativo");
    }

    [Fact]
    public void Release_WithNoReservedStock_ThrowsInvalidStockOperationException()
    {
        var @event = new Event(Guid.NewGuid(), "Concierto", 10);

        var act = () => @event.Release(1);

        act.Should().Throw<InvalidStockOperationException>();
        @event.ReservedStock.Should().Be(0);
    }
}
