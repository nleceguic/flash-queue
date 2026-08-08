using FlashQueue.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace FlashQueue.Consumers.Payments.Consumers;

/// <summary>Stub: libera una preautorización simulada cuando la reserva no se pudo confirmar.</summary>
public sealed class ReservationRejectedConsumer(ILogger<ReservationRejectedConsumer> logger)
    : IConsumer<ReservationRejected>
{
    public async Task Consume(ConsumeContext<ReservationRejected> context)
    {
        var message = context.Message;

        logger.LogInformation(
            "[Payments] Reserva {ReservationId} del evento {EventId} rechazada ({Reason}): liberando preautorización simulada.",
            message.ReservationId, message.EventId, message.Reason);

        await Task.Delay(Random.Shared.Next(200, 800), context.CancellationToken);

        logger.LogInformation("[Payments] Preautorización liberada para la reserva {ReservationId}.", message.ReservationId);
    }
}
