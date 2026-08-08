using FlashQueue.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace FlashQueue.Consumers.Analytics.Consumers;

/// <summary>Stub: no escribe en ningún almacén de analítica real, solo demuestra el desacoplamiento.</summary>
public sealed class ReservationConfirmedConsumer(ILogger<ReservationConfirmedConsumer> logger)
    : IConsumer<ReservationConfirmed>
{
    public async Task Consume(ConsumeContext<ReservationConfirmed> context)
    {
        var message = context.Message;

        logger.LogInformation(
            "[Analytics] Reserva {ReservationId} del evento {EventId} confirmada: registrando evento de analítica simulado.",
            message.ReservationId, message.EventId);

        await Task.Delay(Random.Shared.Next(20, 150), context.CancellationToken);

        logger.LogInformation("[Analytics] Evento de analítica registrado para la reserva {ReservationId}.", message.ReservationId);
    }
}
