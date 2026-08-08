using FlashQueue.Domain.Entities;
using FluentAssertions;

namespace FlashQueue.Tests.Unit.Domain;

public class ReservationRequestTests
{
    [Fact]
    public void Constructor_WithValidData_CreatesRequest()
    {
        var id = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var requestedAt = DateTimeOffset.UtcNow;

        var request = new ReservationRequest(id, eventId, userId, 3, requestedAt);

        request.Id.Should().Be(id);
        request.EventId.Should().Be(eventId);
        request.UserId.Should().Be(userId);
        request.Quantity.Should().Be(3);
        request.RequestedAt.Should().Be(requestedAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WithNonPositiveQuantity_ThrowsArgumentOutOfRangeException(int quantity)
    {
        var act = () => new ReservationRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), quantity, DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_WithEmptyEventId_ThrowsArgumentException()
    {
        var act = () => new ReservationRequest(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), 1, DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentException>();
    }
}
