using FlashQueue.Infrastructure.Messaging;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FlashQueue.Infrastructure;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Configura MassTransit sobre RabbitMQ. El host se lee de la sección de configuración
    /// "RabbitMq" (appsettings o variables de entorno, p. ej. <c>RabbitMq__Host</c>).
    /// </summary>
    /// <param name="serviceName">
    /// Prefijo de cola por servicio (p. ej. "payments"). Necesario cuando varios procesos
    /// distintos registran consumidores con el mismo nombre de tipo (cada
    /// FlashQueue.Consumers.* tiene su propio <c>ReservationConfirmedConsumer</c>): sin prefijo,
    /// el formateador de nombres de MassTransit generaría el mismo nombre de cola para los tres
    /// servicios y, en vez de que cada uno reciba su propia copia del evento, competirían por los
    /// mismos mensajes. FlashQueue.Workers no registra consumidores, así que no lo necesita.
    /// </param>
    /// <param name="configureConsumers">Registro de consumidores propio de cada servicio.</param>
    public static IServiceCollection AddRabbitMqMessaging(
        this IServiceCollection services,
        IConfiguration configuration,
        string? serviceName = null,
        Action<IBusRegistrationConfigurator>? configureConsumers = null)
    {
        services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.SectionName));

        services.AddMassTransit(x =>
        {
            configureConsumers?.Invoke(x);

            x.UsingRabbitMq((context, cfg) =>
            {
                var options = context.GetRequiredService<IOptions<RabbitMqOptions>>().Value;

                cfg.Host(options.Host, options.Port, options.VirtualHost, host =>
                {
                    host.Username(options.Username);
                    host.Password(options.Password);
                });

                // Reintentos por defecto para todos los endpoints de recepción configurados más
                // abajo (backoff exponencial con jitter implícito en la progresión). Cuando un
                // mensaje agota los reintentos, el transporte de RabbitMQ de MassTransit lo mueve
                // automáticamente a la cola dead-letter "<nombre-cola>_error" — no requiere
                // configuración adicional, es el comportamiento por defecto del transporte una vez
                // que el pipeline de reintentos se da por vencido.
                cfg.UseMessageRetry(retry => retry.Exponential(
                    retryLimit: 3,
                    minInterval: TimeSpan.FromMilliseconds(200),
                    maxInterval: TimeSpan.FromSeconds(5),
                    intervalDelta: TimeSpan.FromMilliseconds(500)));

                var endpointNameFormatter = serviceName is null
                    ? (IEndpointNameFormatter)DefaultEndpointNameFormatter.Instance
                    : new KebabCaseEndpointNameFormatter(serviceName, includeNamespace: false);

                cfg.ConfigureEndpoints(context, endpointNameFormatter);
            });
        });

        return services;
    }
}
