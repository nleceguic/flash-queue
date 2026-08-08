using FlashQueue.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace FlashQueue.Consumers.Payments.Consumers;

/// <summary>
/// Stub: no cobra de verdad, solo demuestra que Pagos se entera de una reserva confirmada sin
/// conocer nada del motor de reservas (desacoplamiento vía eventos, CLAUDE.md sección 1).
/// </summary>
public sealed class ReservationConfirmedConsumer(ILogger<ReservationConfirmedConsumer> logger)
    : IConsumer<ReservationConfirmed>
{
    public async Task Consume(ConsumeContext<ReservationConfirmed> context)
    {
        var message = context.Message;

        logger.LogInformation(
            "[Payments] Reserva {ReservationId} del evento {EventId} confirmada: iniciando cobro simulado.",
            message.ReservationId, message.EventId);

        await Task.Delay(Random.Shared.Next(200, 800), context.CancellationToken);

        logger.LogInformation("[Payments] Cobro simulado completado para la reserva {ReservationId}.", message.ReservationId);
    }
}
