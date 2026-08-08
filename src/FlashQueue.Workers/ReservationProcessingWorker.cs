using System.Collections.Concurrent;
using System.Threading.Channels;
using FlashQueue.Application.Ingestion;
using FlashQueue.Application.Processing;
using FlashQueue.Domain.Entities;
using Microsoft.Extensions.Options;

namespace FlashQueue.Workers;

/// <summary>
/// Consume <see cref="ReservationIngestChannel"/> y procesa las reservas con concurrencia
/// acotada y fairness por evento (round-robin). Ver docs/adr/0001-fairness-round-robin-en-worker.md
/// para el razonamiento detrás del diseño.
/// </summary>
public sealed class ReservationProcessingWorker : BackgroundService
{
    private readonly ReservationIngestChannel _ingestChannel;
    private readonly IReservationProcessor _processor;
    private readonly ReservationProcessingOptions _options;
    private readonly ILogger<ReservationProcessingWorker> _logger;

    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<ReservationRequest>> _eventQueues = new();
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
        // La ingesta y el despacho corren como bucles independientes: la ingesta nunca debe
        // bloquearse esperando un hueco de procesamiento, o un evento con mucho tráfico podría
        // retrasar la lectura del canal de entrada (y con ella, el descubrimiento de nuevos
        // eventos que también necesitan su turno).
        var ingestLoop = IngestLoopAsync(stoppingToken);
        var dispatchLoop = DispatchLoopAsync(stoppingToken);

        await Task.WhenAll(ingestLoop, dispatchLoop);

        await DrainAsync();
    }

    private async Task IngestLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in _ingestChannel.Reader.ReadAllAsync(stoppingToken))
            {
                Enqueue(request);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown solicitado: dejamos de aceptar nuevos items del canal de ingesta.
        }
    }

    private void Enqueue(ReservationRequest request)
    {
        var eventId = request.EventId;
        var queue = _eventQueues.GetOrAdd(eventId, static _ => new ConcurrentQueue<ReservationRequest>());
        queue.Enqueue(request);

        // Solo se concede un turno en la ronda por evento a la vez: mientras el evento ya
        // tenga un turno pendiente, los nuevos items simplemente se acumulan en su cola.
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
                // El permiso se adquiere ANTES de tomar el siguiente turno de la ronda: así el
                // dispatcher nunca "reserva" un turno mientras espera un hueco de concurrencia,
                // y el orden de la ronda refleja fielmente el orden real de llegada de eventos.
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
                if (!queue.TryDequeue(out var request))
                {
                    // No debería ocurrir: un turno solo se concede cuando hay al menos un item
                    // en la cola del evento. Nos protegemos igualmente de una condición imposible.
                    _activeEvents.TryRemove(eventId, out _);
                    _concurrencyLimiter.Release();
                    continue;
                }

                CompleteTurn(eventId, queue);

                _ = ProcessAsync(request);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown solicitado: dejamos de despachar nuevos turnos.
        }
    }

    /// <summary>
    /// Decide si el evento vuelve al final de la ronda (le quedan items) o se marca inactivo.
    /// Usa doble comprobación para no perder el turno si un productor encola concurrentemente
    /// justo entre la comprobación de vacío y la retirada de la marca de "activo".
    /// </summary>
    private void CompleteTurn(Guid eventId, ConcurrentQueue<ReservationRequest> queue)
    {
        if (!queue.IsEmpty)
        {
            _turns.Writer.TryWrite(eventId);
            return;
        }

        _activeEvents.TryRemove(eventId, out _);

        if (!queue.IsEmpty && _activeEvents.TryAdd(eventId, 0))
        {
            _turns.Writer.TryWrite(eventId);
        }
    }

    private async Task ProcessAsync(ReservationRequest request)
    {
        try
        {
            await _processor.ProcessAsync(request, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Error procesando la reserva {ReservationId} del evento {EventId}", request.Id, request.EventId);
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    /// <summary>
    /// Espera a que los procesamientos en vuelo terminen (liberando todos los permisos del
    /// semáforo) con un límite de tiempo, en vez de abandonarlos inmediatamente al hacer shutdown.
    /// </summary>
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
