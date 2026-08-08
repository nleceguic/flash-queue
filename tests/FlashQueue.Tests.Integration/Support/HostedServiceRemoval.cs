using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlashQueue.Tests.Integration.Support;

/// <summary>Quita un <see cref="IHostedService"/> del host de test, para observar el channel sin que el worker real compita por leerlo.</summary>
internal static class HostedServiceRemoval
{
    public static void Remove<THostedService>(IServiceCollection services) where THostedService : IHostedService
    {
        var descriptor = services.SingleOrDefault(
            d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(THostedService));

        if (descriptor is not null)
        {
            services.Remove(descriptor);
        }
    }
}
