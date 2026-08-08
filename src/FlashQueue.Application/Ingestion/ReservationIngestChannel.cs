using System.Threading.Channels;
using FlashQueue.Domain.Entities;

namespace FlashQueue.Application.Ingestion;

public sealed class ReservationIngestChannel
{
    private readonly Channel<ReservationRequest> _channel;

    public ReservationIngestChannel(ReservationIngestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.Capacity, "ReservationIngest:Capacity debe ser mayor que cero.");
        }

        _channel = Channel.CreateBounded<ReservationRequest>(new BoundedChannelOptions(options.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    public ChannelWriter<ReservationRequest> Writer => _channel.Writer;

    public ChannelReader<ReservationRequest> Reader => _channel.Reader;
}
