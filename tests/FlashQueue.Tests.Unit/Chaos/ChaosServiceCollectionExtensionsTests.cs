using System.Diagnostics;
using FlashQueue.Infrastructure.Chaos;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlashQueue.Tests.Unit.Chaos;

/// <summary>
/// CHAOS_MODE ausente y CHAOS_MODE=false deben producir exactamente el mismo comportamiento
/// (cero overhead): ambos casos deben registrar <see cref="NullChaosInjector"/>, nunca
/// <see cref="RandomChaosInjector"/>. Ver docs/adr/0005-modo-caos.md.
/// </summary>
public class ChaosServiceCollectionExtensionsTests
{
    [Fact]
    public void IsEnabled_WhenVariableAbsent_ReturnsFalse() =>
        ChaosServiceCollectionExtensions.IsEnabled(new ConfigurationBuilder().Build()).Should().BeFalse();

    [Fact]
    public void IsEnabled_WhenVariableExplicitlyFalse_ReturnsFalse() =>
        ChaosServiceCollectionExtensions.IsEnabled(BuildConfiguration("false")).Should().BeFalse();

    [Fact]
    public void IsEnabled_WhenVariableTrue_ReturnsTrue() =>
        ChaosServiceCollectionExtensions.IsEnabled(BuildConfiguration("true")).Should().BeTrue();

    [Fact]
    public void AddChaos_WithVariableTrue_RegistersRandomChaosInjector()
    {
        var injector = BuildInjector(BuildConfiguration("true"));

        injector.Should().BeOfType<RandomChaosInjector>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void AddChaos_WithVariableAbsentOrExplicitlyFalse_RegistersTheSameNoOpImplementation(string? chaosModeValue)
    {
        var configuration = chaosModeValue is null ? new ConfigurationBuilder().Build() : BuildConfiguration(chaosModeValue);

        var injector = BuildInjector(configuration);

        injector.Should().BeOfType<NullChaosInjector>();
    }

    [Fact]
    public async Task ChaosInjector_WithVariableAbsentOrExplicitlyFalse_BehavesIdentically_NeverDelaysNeverFails()
    {
        var absentInjector = BuildInjector(new ConfigurationBuilder().Build());
        var explicitFalseInjector = BuildInjector(BuildConfiguration("false"));

        absentInjector.Should().BeOfType<NullChaosInjector>();
        explicitFalseInjector.Should().BeOfType<NullChaosInjector>();

        foreach (var injector in new[] { absentInjector, explicitFalseInjector })
        {
            var stopwatch = Stopwatch.StartNew();

            for (var i = 0; i < 200; i++)
            {
                await injector.BeforePostgresCallAsync(CancellationToken.None);
                await injector.BeforeRabbitMqPublishAsync(CancellationToken.None);
            }

            stopwatch.Stop();

            // 400 llamadas a un no-op deben completarse en milisegundos: si "ausente" y "false" se
            // comportaran de forma distinta (p. ej. una de las dos inyectando latencia por error),
            // este margen lo detectaría sin ambigüedad — nunca se lanza ninguna excepción tampoco.
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        }
    }

    private static IChaosInjector BuildInjector(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChaos(configuration);

        return services.BuildServiceProvider().GetRequiredService<IChaosInjector>();
    }

    private static IConfiguration BuildConfiguration(string chaosModeValue) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ChaosServiceCollectionExtensions.EnabledVariableName] = chaosModeValue,
            })
            .Build();
}
