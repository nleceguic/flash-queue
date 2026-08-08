using FlashQueue.Application.Ingestion;
using FlashQueue.Application.Processing;
using FlashQueue.Domain.Entities;
using FlashQueue.Tests.Unit.Support;
using FlashQueue.Workers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlashQueue.Tests.Unit.Workers;

public class ReservationProcessingWorkerTests
{
    [Fact]
    public async Task Worker_WithBusyEventAndQuietEvent_DoesNotStarveTheQuietEvent()
    {
        var channel = new ReservationIngestChannel(new ReservationIngestOptions { Capacity = 200 });
        var processor = new RecordingReservationProcessor();

        // La primera petición procesada se bloquea deliberadamente: con MaxConcurrency = 1, esto
        // congela el despacho justo después de arrancar, mientras el bucle de ingesta -que no
        // depende del procesamiento- drena en paralelo las 105 peticiones a las colas internas
        // por evento. El retraso de 300 ms es un margen amplísimo frente al coste real (~microsegundos
        // para 105 operaciones puramente en memoria), así que no debería producir un test inestable.
        var firstCallGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCallClaimed = 0;
        processor.OnProcessing = (_, _) =>
            Interlocked.Exchange(ref firstCallClaimed, 1) == 0 ? firstCallGate.Task : Task.CompletedTask;

        using var worker = new ReservationProcessingWorker(
            channel,
            processor,
            Options.Create(new ReservationProcessingOptions { MaxConcurrency = 1, DrainTimeout = TimeSpan.FromSeconds(5) }),
            NullLogger<ReservationProcessingWorker>.Instance);

        var busyEvent = Guid.NewGuid();
        var quietEvent = Guid.NewGuid();

        for (var i = 0; i < 100; i++)
        {
            channel.Writer.TryWrite(NewRequest(busyEvent)).Should().BeTrue();
        }

        for (var i = 0; i < 5; i++)
        {
            channel.Writer.TryWrite(NewRequest(quietEvent)).Should().BeTrue();
        }

        await worker.StartAsync(CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(300));
        firstCallGate.SetResult();

        await Polling.UntilAsync(() => processor.ProcessedOrder.Count == 105, TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        var order = processor.ProcessedOrder;
        order.Should().HaveCount(105);
        order.Count(r => r.EventId == busyEvent).Should().Be(100);
        order.Count(r => r.EventId == quietEvent).Should().Be(5);

        var firstQuietEventIndex = order.Select((request, index) => (request, index))
            .First(x => x.request.EventId == quietEvent).index;

        firstQuietEventIndex.Should().BeLessThan(
            10, "el evento con poco tráfico no debería esperar a que el evento con 100 peticiones termine");
    }

    [Fact]
    public async Task Worker_LimitsConcurrentProcessing_ToConfiguredMaximum()
    {
        const int maxConcurrency = 3;
        var channel = new ReservationIngestChannel(new ReservationIngestOptions { Capacity = 50 });
        var processor = new RecordingReservationProcessor();

        var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        processor.OnProcessing = (_, _) => releaseGate.Task;

        using var worker = new ReservationProcessingWorker(
            channel,
            processor,
            Options.Create(new ReservationProcessingOptions { MaxConcurrency = maxConcurrency, DrainTimeout = TimeSpan.FromSeconds(5) }),
            NullLogger<ReservationProcessingWorker>.Instance);

        // Eventos distintos por petición: así la fairness por evento (un item por turno de ronda)
        // no limita artificialmente la concurrencia observada.
        for (var i = 0; i < 20; i++)
        {
            channel.Writer.TryWrite(NewRequest(Guid.NewGuid())).Should().BeTrue();
        }

        await worker.StartAsync(CancellationToken.None);

        await Polling.UntilAsync(() => processor.ProcessedOrder.Count >= maxConcurrency, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        processor.MaxObservedConcurrency.Should().Be(maxConcurrency);

        releaseGate.SetResult();
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_WaitsForInFlightProcessing_BeforeCompleting()
    {
        var channel = new ReservationIngestChannel(new ReservationIngestOptions { Capacity = 10 });
        var processor = new RecordingReservationProcessor();

        var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlightCompleted = false;
        processor.OnProcessing = async (_, _) =>
        {
            await releaseGate.Task;
            inFlightCompleted = true;
        };

        using var worker = new ReservationProcessingWorker(
            channel,
            processor,
            Options.Create(new ReservationProcessingOptions { MaxConcurrency = 1, DrainTimeout = TimeSpan.FromSeconds(5) }),
            NullLogger<ReservationProcessingWorker>.Instance);

        channel.Writer.TryWrite(NewRequest(Guid.NewGuid())).Should().BeTrue();

        await worker.StartAsync(CancellationToken.None);
        await Polling.UntilAsync(() => processor.ProcessedOrder.Count == 1, TimeSpan.FromSeconds(5));

        var stopTask = worker.StopAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        stopTask.IsCompleted.Should().BeFalse("el procesamiento en vuelo todavía no ha terminado");

        releaseGate.SetResult();

        var completedInTime = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(5)));
        completedInTime.Should().Be(stopTask);
        inFlightCompleted.Should().BeTrue();
    }

    [Fact]
    public void Constructor_WithNonPositiveMaxConcurrency_ThrowsArgumentOutOfRangeException()
    {
        var channel = new ReservationIngestChannel(new ReservationIngestOptions { Capacity = 1 });
        var processor = new RecordingReservationProcessor();

        var act = () => new ReservationProcessingWorker(
            channel,
            processor,
            Options.Create(new ReservationProcessingOptions { MaxConcurrency = 0 }),
            NullLogger<ReservationProcessingWorker>.Instance);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static ReservationRequest NewRequest(Guid eventId) =>
        new(Guid.NewGuid(), eventId, Guid.NewGuid(), 1, DateTimeOffset.UtcNow);
}
