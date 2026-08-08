using System.Threading.Channels;

namespace FlashQueue.Application.Ingestion;

public sealed class ReservationIngestChannel
{
    private readonly Channel<ReservationIngestItem> _channel;

    public ReservationIngestChannel(ReservationIngestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.Capacity, "ReservationIngest:Capacity debe ser mayor que cero.");
        }

        _channel = Channel.CreateBounded<ReservationIngestItem>(new BoundedChannelOptions(options.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    public ChannelWriter<ReservationIngestItem> Writer => _channel.Writer;

    public ChannelReader<ReservationIngestItem> Reader => _channel.Reader;
}
