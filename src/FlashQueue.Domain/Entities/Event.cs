using FlashQueue.Domain.Exceptions;

namespace FlashQueue.Domain.Entities;

public sealed class Event
{
    public Guid Id { get; }
    public string Name { get; }
    public int TotalStock { get; }
    public int ReservedStock { get; private set; }

    public int AvailableStock => TotalStock - ReservedStock;

    public Event(Guid id, string name, int totalStock)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("El identificador del evento no puede estar vacío.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("El nombre del evento no puede estar vacío.", nameof(name));
        }

        if (totalStock < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalStock), totalStock, "El stock total no puede ser negativo.");
        }

        Id = id;
        Name = name;
        TotalStock = totalStock;
        ReservedStock = 0;
    }

    public void Reserve(int quantity)
    {
        if (quantity <= 0)
        {
            throw new InvalidStockOperationException("La cantidad a reservar debe ser mayor que cero.");
        }

        if (quantity > AvailableStock)
        {
            throw new InsufficientStockException(Id, quantity, AvailableStock);
        }

        ReservedStock += quantity;
    }

    public void Release(int quantity)
    {
        if (quantity <= 0)
        {
            throw new InvalidStockOperationException("La cantidad a liberar debe ser mayor que cero.");
        }

        if (quantity > ReservedStock)
        {
            throw new InvalidStockOperationException("No se puede liberar más stock del que está reservado.");
        }

        ReservedStock -= quantity;
    }
}
