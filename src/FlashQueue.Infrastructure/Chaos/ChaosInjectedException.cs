namespace FlashQueue.Infrastructure.Chaos;

/// <summary>Fallo artificial inyectado por el modo caos en la publicación a RabbitMQ.</summary>
public sealed class ChaosInjectedException(string message) : Exception(message);
