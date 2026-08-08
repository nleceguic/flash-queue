using FlashQueue.Domain.Entities;

namespace FlashQueue.Application.Processing;

/// <summary>Puerto hacia el motor de reserva, para que <c>ReservationProcessingWorker</c> se pueda probar sin infraestructura.</summary>
public interface IReservationProcessor
{
    Task ProcessAsync(ReservationRequest request, CancellationToken cancellationToken);
}
