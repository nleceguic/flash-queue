using FlashQueue.Application.Ingestion;
using FlashQueue.Domain.Entities;
using FluentAssertions;

namespace FlashQueue.Tests.Unit.Application;

public class ReservationIngestChannelTests
{
    [Fact]
    public async Task WriteAsync_OnSaturatedChannel_DoesNotBlockTheCallingThread()
    {
        var channel = new ReservationIngestChannel(new ReservationIngestOptions { Capacity = 1 });
        const int writerCount = 2_000;

        var writeLoop = Task.Run(() =>
        {
            var pending = new Task[writerCount];
            for (var i = 0; i < writerCount; i++)
            {
                pending[i] = channel.Writer.WriteAsync(NewItem()).AsTask();
            }

            return pending;
        });

        var completedInTime = await Task.WhenAny(writeLoop, Task.Delay(TimeSpan.FromSeconds(5)));
        completedInTime.Should().Be(writeLoop, "encolar 2000 escrituras sobre un canal saturado no debe bloquear el hilo emisor");

        var pendingWrites = await writeLoop;
        pendingWrites.Should().Contain(t => !t.IsCompleted);

        for (var i = 0; i < writerCount; i++)
        {
            await channel.Reader.ReadAsync();
        }

        var allWrites = Task.WhenAll(pendingWrites);
        var allCompletedInTime = await Task.WhenAny(allWrites, Task.Delay(TimeSpan.FromSeconds(5)));
        allCompletedInTime.Should().Be(allWrites);
        pendingWrites.Should().OnlyContain(t => t.IsCompletedSuccessfully);
    }

    [Fact]
    public void Constructor_WithNonPositiveCapacity_ThrowsArgumentOutOfRangeException()
    {
        var act = () => new ReservationIngestChannel(new ReservationIngestOptions { Capacity = 0 });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TryWrite_WhenChannelIsFull_ReturnsFalseImmediately()
    {
        var channel = new ReservationIngestChannel(new ReservationIngestOptions { Capacity = 1 });

        channel.Writer.TryWrite(NewItem()).Should().BeTrue();
        channel.Writer.TryWrite(NewItem()).Should().BeFalse();
    }

    [Fact]
    public async Task WriteAsync_WhenChannelIsFull_CompletesOnlyAfterReaderMakesRoom()
    {
        var channel = new ReservationIngestChannel(new ReservationIngestOptions { Capacity = 1 });
        await channel.Writer.WriteAsync(NewItem());

        var secondWrite = channel.Writer.WriteAsync(NewItem()).AsTask();
        secondWrite.IsCompleted.Should().BeFalse();

        await channel.Reader.ReadAsync();
        await secondWrite;

        secondWrite.IsCompletedSuccessfully.Should().BeTrue();
    }

    private static ReservationIngestItem NewItem() =>
        new(new ReservationRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow), default);
}
