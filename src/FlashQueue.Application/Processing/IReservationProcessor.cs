using FlashQueue.Domain.Entities;

namespace FlashQueue.Application.Processing;

/// <summary>
/// Puerto hacia el motor de reserva (persistencia/stock). La implementación real
/// (Postgres, SELECT ... FOR UPDATE SKIP LOCKED) llega en una fase posterior del
/// proyecto; este puerto permite que <c>ReservationProcessingWorker</c> se pueda
/// probar sin infraestructura.
/// </summary>
public interface IReservationProcessor
{
    Task ProcessAsync(ReservationRequest request, CancellationToken cancellationToken);
}
