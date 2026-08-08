using FlashQueue.Infrastructure.Messaging;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FlashQueue.Infrastructure;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>Configura MassTransit sobre RabbitMQ, leyendo el host de la sección "RabbitMq".</summary>
    /// <param name="serviceName">Prefijo de cola por servicio, para que los tres Consumers.* no compitan por la misma cola.</param>
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

                // Backoff exponencial; agotados los reintentos, MassTransit mueve el mensaje a la
                // cola dead-letter "<nombre-cola>_error" automáticamente.
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
