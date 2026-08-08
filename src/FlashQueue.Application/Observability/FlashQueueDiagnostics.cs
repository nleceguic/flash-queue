using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FlashQueue.Application.Observability;

/// <summary>
/// Identidad de instrumentación compartida por todos los procesos de FlashQueue (Api, Workers).
/// Cada proceso sigue teniendo su propio <c>service.name</c> como recurso de OpenTelemetry (eso
/// es lo que los distingue en Tempo/Prometheus); esto es solo el nombre del "instrumentation
/// scope" dentro de cada uno, y por eso tiene sentido que sea el mismo en ambos — son partes de
/// un único flujo de negocio (una reserva), no herramientas distintas.
/// </summary>
public static class FlashQueueDiagnostics
{
    public const string Name = "FlashQueue";
    public const string Version = "1.0.0";

    public static readonly ActivitySource ActivitySource = new(Name, Version);
    public static readonly Meter Meter = new(Name, Version);

    /// <summary>Reservas terminadas de procesar (Confirmed o Rejected). Tag <c>reservation.status</c>.</summary>
    public static readonly Counter<long> ReservationsProcessed =
        Meter.CreateCounter<long>("flashqueue.reservations.processed", unit: "{reservation}");

    /// <summary>
    /// Latencia end-to-end: desde que la petición HTTP quedó aceptada (<c>ReservationRequest.RequestedAt</c>)
    /// hasta que la reserva quedó resuelta en Postgres. Tag <c>reservation.status</c>.
    /// </summary>
    public static readonly Histogram<double> ProcessingDuration =
        Meter.CreateHistogram<double>("flashqueue.reservations.processing_duration", unit: "ms");

    private static bool _channelSizeGaugeRegistered;

    /// <summary>
    /// Registra el gauge de tamaño del channel de ingesta. Se llama una única vez por proceso
    /// (Api y Workers tienen cada uno su propia instancia de <c>ReservationIngestChannel</c>), justo
    /// después de construir el contenedor de DI, con un callback que lee <c>Reader.Count</c> en el
    /// momento en que el exportador de métricas lo pida (no hay un timer propio: OpenTelemetry ya
    /// hace el scraping periódico de los ObservableGauge).
    /// </summary>
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
