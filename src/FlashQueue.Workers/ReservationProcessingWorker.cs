using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using FlashQueue.Application.Ingestion;
using FlashQueue.Application.Observability;
using FlashQueue.Application.Processing;
using Microsoft.Extensions.Options;

namespace FlashQueue.Workers;

/// <summary>Consume el channel de ingesta con concurrencia acotada y fairness round-robin por evento.</summary>
public sealed class ReservationProcessingWorker : BackgroundService
{
    private readonly ReservationIngestChannel _ingestChannel;
    private readonly IReservationProcessor _processor;
    private readonly ReservationProcessingOptions _options;
    private readonly ILogger<ReservationProcessingWorker> _logger;

    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<ReservationIngestItem>> _eventQueues = new();
    private readonly ConcurrentDictionary<Guid, byte> _activeEvents = new();
    private readonly Channel<Guid> _turns = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    private readonly SemaphoreSlim _concurrencyLimiter;

    public ReservationProcessingWorker(
        ReservationIngestChannel ingestChannel,
        IReservationProcessor processor,
        IOptions<ReservationProcessingOptions> options,
        ILogger<ReservationProcessingWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(ingestChannel);
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var value = options.Value;
        if (value.MaxConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), value.MaxConcurrency, "ReservationProcessing:MaxConcurrency debe ser mayor que cero.");
        }

        _ingestChannel = ingestChannel;
        _processor = processor;
        _options = value;
        _logger = logger;
        _concurrencyLimiter = new SemaphoreSlim(value.MaxConcurrency, value.MaxConcurrency);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Bucles independientes: si la ingesta esperara a un hueco de procesamiento, un evento
        // con mucho tráfico retrasaría la lectura del channel para todos los demás.
        var ingestLoop = IngestLoopAsync(stoppingToken);
        var dispatchLoop = DispatchLoopAsync(stoppingToken);

        await Task.WhenAll(ingestLoop, dispatchLoop);

        await DrainAsync();
    }

    private async Task IngestLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in _ingestChannel.Reader.ReadAllAsync(stoppingToken))
            {
                Enqueue(item);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void Enqueue(ReservationIngestItem item)
    {
        var eventId = item.Request.EventId;
        var queue = _eventQueues.GetOrAdd(eventId, static _ => new ConcurrentQueue<ReservationIngestItem>());
        queue.Enqueue(item);

        // Un evento solo tiene un turno pendiente a la vez; los items nuevos se acumulan en su cola.
        if (_activeEvents.TryAdd(eventId, 0))
        {
            _turns.Writer.TryWrite(eventId);
        }
    }

    private async Task DispatchLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                // El permiso se adquiere antes de tomar turno, para que el orden de la ronda
                // refleje el orden real de llegada y no quede "reservado" mientras se espera.
                await _concurrencyLimiter.WaitAsync(stoppingToken);

                Guid eventId;
                try
                {
                    eventId = await _turns.Reader.ReadAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _concurrencyLimiter.Release();
                    throw;
                }

                var queue = _eventQueues[eventId];
                if (!queue.TryDequeue(out var item))
                {
                    // No debería pasar (un turno solo se concede con al menos un item en cola).
                    _activeEvents.TryRemove(eventId, out _);
                    _concurrencyLimiter.Release();
                    continue;
                }

                CompleteTurn(eventId, queue);

                _ = ProcessAsync(item);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Devuelve el evento al final de la ronda si le quedan items, o lo marca inactivo.</summary>
    private void CompleteTurn(Guid eventId, ConcurrentQueue<ReservationIngestItem> queue)
    {
        if (!queue.IsEmpty)
        {
            _turns.Writer.TryWrite(eventId);
            return;
        }

        _activeEvents.TryRemove(eventId, out _);

        // Doble comprobación: un productor pudo encolar justo entre el IsEmpty de arriba y el TryRemove.
        if (!queue.IsEmpty && _activeEvents.TryAdd(eventId, 0))
        {
            _turns.Writer.TryWrite(eventId);
        }
    }

    private async Task ProcessAsync(ReservationIngestItem item)
    {
        var request = item.Request;

        // Reconecta la traza al otro lado del channel usando el contexto capturado al encolar,
        // en vez de abrir una traza nueva sin relación con la petición HTTP original.
        using var activity = FlashQueueDiagnostics.ActivitySource.StartActivity(
            "reservation.process", ActivityKind.Consumer, item.TraceContext);
        activity?.SetTag("reservation.id", request.Id);
        activity?.SetTag("event.id", request.EventId);
        activity?.SetTag("reservation.quantity", request.Quantity);

        try
        {
            await _processor.ProcessAsync(request, CancellationToken.None);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(
                ex, "Error procesando la reserva {ReservationId} del evento {EventId}", request.Id, request.EventId);
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    /// <summary>Espera a que las reservas en vuelo terminen antes de apagar, con un límite de tiempo.</summary>
    private async Task DrainAsync()
    {
        var acquired = 0;
        using var drainCts = new CancellationTokenSource(_options.DrainTimeout);

        try
        {
            for (; acquired < _options.MaxConcurrency; acquired++)
            {
                await _concurrencyLimiter.WaitAsync(drainCts.Token);
            }

            _logger.LogInformation("Drenado completo: no quedan reservas en vuelo.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Tiempo de drenado ({DrainTimeout}) agotado con reservas aún en vuelo.", _options.DrainTimeout);
        }
        finally
        {
            for (var i = 0; i < acquired; i++)
            {
                _concurrencyLimiter.Release();
            }
        }
    }
}
