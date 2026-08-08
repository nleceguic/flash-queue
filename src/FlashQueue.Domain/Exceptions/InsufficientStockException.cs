namespace FlashQueue.Domain.Exceptions;

public sealed class InsufficientStockException : DomainException
{
    public Guid EventId { get; }
    public int RequestedQuantity { get; }
    public int AvailableStock { get; }

    public InsufficientStockException(Guid eventId, int requestedQuantity, int availableStock)
        : base($"No hay stock suficiente para el evento {eventId}: solicitado {requestedQuantity}, disponible {availableStock}.")
    {
        EventId = eventId;
        RequestedQuantity = requestedQuantity;
        AvailableStock = availableStock;
    }
}
