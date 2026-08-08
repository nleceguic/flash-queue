using Microsoft.Extensions.Logging;

namespace FlashQueue.Tests.Unit.Chaos;

/// <summary>Captura los mensajes logueados para poder comprobar el prefijo "[CHAOS]" sin mocks.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Add(formatter(state, exception));
}
