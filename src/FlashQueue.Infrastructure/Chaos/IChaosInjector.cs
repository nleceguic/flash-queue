namespace FlashQueue.Infrastructure.Chaos;

/// <summary>Punto de inyección de caos delante de cada dependencia externa. Ver docs/adr/0005-modo-caos.md.</summary>
public interface IChaosInjector
{
    Task BeforePostgresCallAsync(CancellationToken cancellationToken);

    Task BeforeRabbitMqPublishAsync(CancellationToken cancellationToken);
}
