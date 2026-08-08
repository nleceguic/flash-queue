namespace FlashQueue.Domain.Exceptions;

public sealed class EventNotFoundException : DomainException
{
    public Guid EventId { get; }

    public EventNotFoundException(Guid eventId)
        : base($"No existe ningún evento con id {eventId}.")
    {
        EventId = eventId;
    }
}
