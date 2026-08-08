using System.Collections.Concurrent;
using System.Diagnostics;
using FlashQueue.Application.Ingestion;
using FlashQueue.Application.Observability;
using FlashQueue.Application.Processing;
using FlashQueue.Domain.Entities;
using FlashQueue.Tests.Unit.Support;
using FlashQueue.Tests.Unit.Workers;
using FlashQueue.Workers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlashQueue.Tests.Unit.Observability;

/// <summary>Cubre el tramo ingesta -> worker de la traza sin infraestructura real; el resto lo cubre <c>ObservabilityPipelineTests</c>.</summary>
public class ReservationTracingTests
{
    [Fact]
    public async Task ReservationProcessingWorker_ReconnectsTrace_AcrossTheIngestChannel()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == FlashQueueDiagnostics.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        var captured = new ConcurrentBag<Activity>();
        listener.ActivityStopped = captured.Add;
        ActivitySource.AddActivityListener(listener);

        // Simula lo que hace ReservationsEndpoints: abre "reservation.enqueue" y captura su contexto.
        using var enqueueActivity = FlashQueueDiagnostics.ActivitySource.StartActivity(
            "reservation.enqueue", ActivityKind.Producer);
        enqueueActivity.Should().NotBeNull("el listener registrado arriba debe hacer que StartActivity produzca una Activity real");

        var request = new ReservationRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow);
        var item = new ReservationIngestItem(request, enqueueActivity!.Context);

        var channel = new ReservationIngestChannel(new ReservationIngestOptions { Capacity = 10 });
        var processor = new RecordingReservationProcessor();

        using var worker = new ReservationProcessingWorker(
            channel,
            processor,
            Options.Create(new ReservationProcessingOptions { MaxConcurrency = 1, DrainTimeout = TimeSpan.FromSeconds(5) }),
            NullLogger<ReservationProcessingWorker>.Instance);

        await channel.Writer.WriteAsync(item);
        await worker.StartAsync(CancellationToken.None);

        await Polling.UntilAsync(
            () => captured.Any(a => a.OperationName == "reservation.process"),
            TimeSpan.FromSeconds(5));

        await worker.StopAsync(CancellationToken.None);

        var processActivity = captured.Should().ContainSingle(a => a.OperationName == "reservation.process").Which;

        processActivity.TraceId.Should().Be(enqueueActivity.TraceId,
            "el worker debe reconectar la traza usando el contexto capturado en el item, no abrir una traza nueva");
        processActivity.ParentSpanId.Should().Be(enqueueActivity.SpanId,
            "reservation.process debe ser hijo directo de reservation.enqueue, no un descendiente indirecto ni una traza huérfana");
        processActivity.Kind.Should().Be(ActivityKind.Consumer);
        processActivity.GetTagItem("reservation.id").Should().Be(request.Id);
        processActivity.GetTagItem("event.id").Should().Be(request.EventId);

        processor.ProcessedOrder.Should().ContainSingle(r => r.Id == request.Id);
    }
}
