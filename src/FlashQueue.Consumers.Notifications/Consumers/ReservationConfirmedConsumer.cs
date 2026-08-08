using FlashQueue.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace FlashQueue.Consumers.Notifications.Consumers;

/// <summary>Stub: no envía ningún email de verdad, solo demuestra el desacoplamiento por eventos.</summary>
public sealed class ReservationConfirmedConsumer(ILogger<ReservationConfirmedConsumer> logger)
    : IConsumer<ReservationConfirmed>
{
    public async Task Consume(ConsumeContext<ReservationConfirmed> context)
    {
        var message = context.Message;

        logger.LogInformation(
            "[Notifications] Reserva {ReservationId} del evento {EventId} confirmada: enviando email de confirmación simulado.",
            message.ReservationId, message.EventId);

        await Task.Delay(Random.Shared.Next(50, 300), context.CancellationToken);

        logger.LogInformation("[Notifications] Email de confirmación enviado para la reserva {ReservationId}.", message.ReservationId);
    }
}
