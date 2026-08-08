using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FlashQueue.Application.Observability;

/// <summary>ActivitySource/Meter compartidos por todos los procesos de FlashQueue.</summary>
public static class FlashQueueDiagnostics
{
    public const string Name = "FlashQueue";
    public const string Version = "1.0.0";

    public static readonly ActivitySource ActivitySource = new(Name, Version);
    public static readonly Meter Meter = new(Name, Version);

    /// <summary>Reservas terminadas de procesar (Confirmed o Rejected). Tag <c>reservation.status</c>.</summary>
    public static readonly Counter<long> ReservationsProcessed =
        Meter.CreateCounter<long>("flashqueue.reservations.processed", unit: "{reservation}");

    /// <summary>Latencia desde que la petición HTTP quedó aceptada hasta resolverse en Postgres. Tag <c>reservation.status</c>.</summary>
    public static readonly Histogram<double> ProcessingDuration =
        Meter.CreateHistogram<double>("flashqueue.reservations.processing_duration", unit: "ms");

    private static bool _channelSizeGaugeRegistered;

    /// <summary>Registra el gauge de tamaño del channel una sola vez por proceso; llamadas posteriores son no-op.</summary>
    public static void ObserveChannelSize(Func<int> sizeProvider)
    {
        if (_channelSizeGaugeRegistered)
        {
            return;
        }

        Meter.CreateObservableGauge(
            "flashqueue.reservation_channel.size",
            sizeProvider,
            unit: "{item}",
            description: "Número de ReservationRequest actualmente en el channel de ingesta, pendientes de procesar.");

        _channelSizeGaugeRegistered = true;
    }
}
