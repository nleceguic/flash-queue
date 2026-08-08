using FlashQueue.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace FlashQueue.Consumers.Notifications.Consumers;

/// <summary>Stub: avisa (de mentira) al usuario de que su reserva no se pudo confirmar.</summary>
public sealed class ReservationRejectedConsumer(ILogger<ReservationRejectedConsumer> logger)
    : IConsumer<ReservationRejected>
{
    public async Task Consume(ConsumeContext<ReservationRejected> context)
    {
        var message = context.Message;

        logger.LogInformation(
            "[Notifications] Reserva {ReservationId} del evento {EventId} rechazada ({Reason}): enviando email de disculpa simulado.",
            message.ReservationId, message.EventId, message.Reason);

        await Task.Delay(Random.Shared.Next(50, 300), context.CancellationToken);

        logger.LogInformation("[Notifications] Email de disculpa enviado para la reserva {ReservationId}.", message.ReservationId);
    }
}
