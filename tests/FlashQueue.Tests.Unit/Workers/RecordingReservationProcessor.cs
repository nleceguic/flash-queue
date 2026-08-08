using FlashQueue.Application.Processing;
using FlashQueue.Domain.Entities;

namespace FlashQueue.Tests.Unit.Workers;

/// <summary>
/// Doble de prueba de <see cref="IReservationProcessor"/>: registra el orden de procesamiento,
/// mide la concurrencia observada y permite conectar un comportamiento asíncrono controlado por
/// el test (p. ej. bloquear una llamada hasta que el test decida liberarla).
/// </summary>
internal sealed class RecordingReservationProcessor : IReservationProcessor
{
    private readonly List<ReservationRequest> _processedOrder = [];
    private readonly object _lock = new();
    private int _current;
    private int _maxObservedConcurrency;

    public Func<ReservationRequest, CancellationToken, Task>? OnProcessing { get; set; }

    public IReadOnlyList<ReservationRequest> ProcessedOrder
    {
        get { lock (_lock) { return _processedOrder.ToArray(); } }
    }

    public int MaxObservedConcurrency => Volatile.Read(ref _maxObservedConcurrency);

    public async Task ProcessAsync(ReservationRequest request, CancellationToken cancellationToken)
    {
        var current = Interlocked.Increment(ref _current);
        TrackMax(current);

        lock (_lock)
        {
            _processedOrder.Add(request);
        }

        try
        {
            if (OnProcessing is not null)
            {
                await OnProcessing(request, cancellationToken);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _current);
        }
    }

    private void TrackMax(int value)
    {
        int initial, computed;
        do
        {
            initial = _maxObservedConcurrency;
            computed = Math.Max(initial, value);
        } while (Interlocked.CompareExchange(ref _maxObservedConcurrency, computed, initial) != initial);
    }
}
