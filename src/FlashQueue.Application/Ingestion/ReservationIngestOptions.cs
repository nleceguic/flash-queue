namespace FlashQueue.Application.Ingestion;

public sealed class ReservationIngestOptions
{
    public const string SectionName = "ReservationIngest";
    public const int DefaultCapacity = 500;

    public int Capacity { get; set; } = DefaultCapacity;
}
