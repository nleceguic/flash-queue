using System.Diagnostics;
using FlashQueue.Infrastructure.Chaos;
using FlashQueue.Infrastructure.Resilience;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FlashQueue.Tests.Unit.Chaos;

/// <summary>Comportamiento de <see cref="RandomChaosInjector"/>: latencia, tasa de fallo y logging con prefijo [CHAOS].</summary>
public class RandomChaosInjectorTests
{
    [Fact]
    public async Task BeforePostgresCallAsync_InjectsLatencyWithinConfiguredRange()
    {
        var logger = new CapturingLogger<RandomChaosInjector>();
        var injector = CreateInjector(logger, minLatencyMs: 50, maxLatencyMs: 80, postgresFailureProbability: 0);

        var stopwatch = Stopwatch.StartNew();
        await injector.BeforePostgresCallAsync(CancellationToken.None);
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(40));
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500), "no debe usar el rango por defecto (hasta 2000ms) en vez del configurado");
        logger.Messages.Should().Contain(m => m.StartsWith("[CHAOS]") && m.Contains("latencia") && m.Contains("Postgres"));
    }

    [Fact]
    public async Task BeforePostgresCallAsync_WithFailureProbabilityOne_AlwaysThrowsTransientNpgsqlException()
    {
        var logger = new CapturingLogger<RandomChaosInjector>();
        var injector = CreateInjector(logger, minLatencyMs: 0, maxLatencyMs: 0, postgresFailureProbability: 1.0);

        var act = () => injector.BeforePostgresCallAsync(CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<NpgsqlException>();
        PostgresResilience.IsTransientFailure(thrown.Which).Should().BeTrue(
            "el fallo inyectado debe disparar el mismo retry que un fallo de conexión real (ver ADR 0004)");
        logger.Messages.Should().Contain(m => m.StartsWith("[CHAOS]") && m.Contains("Postgres"));
    }

    [Fact]
    public async Task BeforePostgresCallAsync_WithFailureProbabilityZero_NeverThrows()
    {
        var injector = CreateInjector(NullLogger<RandomChaosInjector>.Instance, minLatencyMs: 0, maxLatencyMs: 0, postgresFailureProbability: 0);

        for (var i = 0; i < 50; i++)
        {
            await injector.BeforePostgresCallAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task BeforeRabbitMqPublishAsync_WithFailureProbabilityOne_AlwaysThrowsChaosInjectedException()
    {
        var logger = new CapturingLogger<RandomChaosInjector>();
        var injector = CreateInjector(logger, minLatencyMs: 0, maxLatencyMs: 0, rabbitMqFailureProbability: 1.0);

        var act = () => injector.BeforeRabbitMqPublishAsync(CancellationToken.None);

        await act.Should().ThrowAsync<ChaosInjectedException>();
        logger.Messages.Should().Contain(m => m.StartsWith("[CHAOS]") && m.Contains("RabbitMQ"));
    }

    [Fact]
    public async Task BeforeRabbitMqPublishAsync_ObservedFailureRate_IsApproximatelyTheConfiguredProbability()
    {
        var injector = CreateInjector(NullLogger<RandomChaosInjector>.Instance, minLatencyMs: 0, maxLatencyMs: 0, rabbitMqFailureProbability: 0.5);

        const int trials = 300;
        var failures = 0;
        for (var i = 0; i < trials; i++)
        {
            try
            {
                await injector.BeforeRabbitMqPublishAsync(CancellationToken.None);
            }
            catch (ChaosInjectedException)
            {
                failures++;
            }
        }

        // Media esperada 150; margen amplio (±60) para que el azar no haga fallar el test.
        failures.Should().BeInRange(90, 210);
    }

    [Fact]
    public async Task BothMethods_AlwaysLogLatencyInjection_RegardlessOfFailureOutcome()
    {
        var logger = new CapturingLogger<RandomChaosInjector>();
        var injector = CreateInjector(
            logger, minLatencyMs: 1, maxLatencyMs: 2, postgresFailureProbability: 0, rabbitMqFailureProbability: 0);

        await injector.BeforePostgresCallAsync(CancellationToken.None);
        await injector.BeforeRabbitMqPublishAsync(CancellationToken.None);

        logger.Messages.Should().Contain(m => m.StartsWith("[CHAOS]") && m.Contains("Postgres"));
        logger.Messages.Should().Contain(m => m.StartsWith("[CHAOS]") && m.Contains("RabbitMQ"));
    }

    private static RandomChaosInjector CreateInjector(
        ILogger<RandomChaosInjector> logger,
        int minLatencyMs,
        int maxLatencyMs,
        double postgresFailureProbability = 0,
        double rabbitMqFailureProbability = 0) =>
        new(logger, Options.Create(new ChaosOptions
        {
            MinLatency = TimeSpan.FromMilliseconds(minLatencyMs),
            MaxLatency = TimeSpan.FromMilliseconds(maxLatencyMs),
            PostgresFailureProbability = postgresFailureProbability,
            RabbitMqFailureProbability = rabbitMqFailureProbability,
        }));
}
