using FlashQueue.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace FlashQueue.Consumers.Analytics.Consumers;

/// <summary>Stub: registra (de mentira) el rechazo para métricas de conversión/abandono.</summary>
public sealed class ReservationRejectedConsumer(ILogger<ReservationRejectedConsumer> logger)
    : IConsumer<ReservationRejected>
{
    public async Task Consume(ConsumeContext<ReservationRejected> context)
    {
        var message = context.Message;

        logger.LogInformation(
            "[Analytics] Reserva {ReservationId} del evento {EventId} rechazada ({Reason}): registrando evento de analítica simulado.",
            message.ReservationId, message.EventId, message.Reason);

        await Task.Delay(Random.Shared.Next(20, 150), context.CancellationToken);

        logger.LogInformation("[Analytics] Evento de analítica registrado para la reserva {ReservationId}.", message.ReservationId);
    }
}
