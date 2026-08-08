using System.Net.Sockets;
using FlashQueue.Infrastructure.Persistence;
using FlashQueue.Infrastructure.Resilience;
using FluentAssertions;
using Npgsql;

namespace FlashQueue.Tests.Unit.Resilience;

/// <summary><see cref="PostgresResilience"/> reintenta solo fallos transitorios, nunca constraints ni validación.</summary>
public class PostgresResilienceTests
{
    [Theory]
    [InlineData("40001", true)] // serialization_failure
    [InlineData("40P01", true)] // deadlock_detected
    [InlineData("57P03", true)] // cannot_connect_now
    [InlineData("53300", true)] // too_many_connections
    [InlineData("08006", true)] // connection_failure
    [InlineData("23505", false)] // unique_violation
    [InlineData("23514", false)] // check_violation
    [InlineData("22001", false)] // string_data_right_truncation (validación)
    [InlineData("42601", false)] // syntax_error
    public void IsTransientFailure_ClassifiesPostgresExceptionsBySqlState(string sqlState, bool expectedTransient)
    {
        var exception = new PostgresException("boom", "ERROR", "ERROR", sqlState);

        PostgresResilience.IsTransientFailure(exception).Should().Be(expectedTransient);
    }

    [Fact]
    public void IsTransientFailure_ConnectionLevelNpgsqlException_IsTransient()
    {
        var exception = new NpgsqlException("connection lost", new SocketException());

        PostgresResilience.IsTransientFailure(exception).Should().BeTrue();
    }

    [Fact]
    public void IsTransientFailure_NonNpgsqlException_IsNeverTransient()
    {
        PostgresResilience.IsTransientFailure(new InvalidOperationException("boom")).Should().BeFalse();
    }

    [Fact]
    public async Task Pipeline_WithTransientFailures_RetriesUntilSuccess()
    {
        var pipeline = PostgresResilience.BuildTransientFaultPipeline<int>(new ReservationRepositoryOptions
        {
            TransientFaultMaxRetryAttempts = 3,
            TransientFaultBaseDelay = TimeSpan.FromMilliseconds(5),
        });
        var operation = new FailNTimesOperation(failuresBeforeSuccess: 2, TransientFailure);

        var result = await pipeline.ExecuteAsync(
            static (state, ct) => new ValueTask<int>(state.InvokeAsync(ct)), operation, CancellationToken.None);

        result.Should().Be(3, "el tercer intento (2 reintentos) es el que finalmente tiene éxito");
        operation.Attempts.Should().Be(3);
    }

    [Fact]
    public async Task Pipeline_WithPersistentTransientFailures_GivesUpAfterMaxRetryAttempts()
    {
        const int maxRetryAttempts = 3;
        var pipeline = PostgresResilience.BuildTransientFaultPipeline<int>(new ReservationRepositoryOptions
        {
            TransientFaultMaxRetryAttempts = maxRetryAttempts,
            TransientFaultBaseDelay = TimeSpan.FromMilliseconds(5),
        });
        var operation = new FailNTimesOperation(failuresBeforeSuccess: int.MaxValue, TransientFailure);

        var act = () => pipeline.ExecuteAsync(
            static (state, ct) => new ValueTask<int>(state.InvokeAsync(ct)), operation, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<PostgresException>();
        operation.Attempts.Should().Be(
            maxRetryAttempts + 1, "el intento original más el máximo de reintentos configurado, y ninguno más");
    }

    [Fact]
    public async Task Pipeline_WithConstraintViolation_NeverRetries()
    {
        var pipeline = PostgresResilience.BuildTransientFaultPipeline<int>(new ReservationRepositoryOptions
        {
            TransientFaultMaxRetryAttempts = 3,
            TransientFaultBaseDelay = TimeSpan.FromMilliseconds(5),
        });
        var operation = new FailNTimesOperation(failuresBeforeSuccess: int.MaxValue, ConstraintViolation);

        var act = () => pipeline.ExecuteAsync(
            static (state, ct) => new ValueTask<int>(state.InvokeAsync(ct)), operation, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<PostgresException>();
        operation.Attempts.Should().Be(1, "una violación de constraint nunca debe reintentarse");
    }

    [Fact]
    public async Task Pipeline_WithValidationError_NeverRetries()
    {
        var pipeline = PostgresResilience.BuildTransientFaultPipeline<int>(new ReservationRepositoryOptions
        {
            TransientFaultMaxRetryAttempts = 3,
            TransientFaultBaseDelay = TimeSpan.FromMilliseconds(5),
        });
        var operation = new FailNTimesOperation(failuresBeforeSuccess: int.MaxValue, ValidationError);

        var act = () => pipeline.ExecuteAsync(
            static (state, ct) => new ValueTask<int>(state.InvokeAsync(ct)), operation, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<PostgresException>();
        operation.Attempts.Should().Be(1, "un error de validación de datos nunca debe reintentarse");
    }

    private static PostgresException TransientFailure(int attempt) =>
        new($"fallo transitorio simulado (intento {attempt})", "ERROR", "ERROR", "40001");

    private static PostgresException ConstraintViolation(int attempt) =>
        new("llave duplicada simulada", "ERROR", "ERROR", "23505");

    private static PostgresException ValidationError(int attempt) =>
        new("dato inválido simulado", "ERROR", "ERROR", "22001");

    /// <summary>Falla las primeras <paramref name="failuresBeforeSuccess"/> veces, luego tiene éxito.</summary>
    private sealed class FailNTimesOperation(int failuresBeforeSuccess, Func<int, Exception> exceptionFactory)
    {
        public int Attempts { get; private set; }

        public Task<int> InvokeAsync(CancellationToken cancellationToken)
        {
            Attempts++;

            if (Attempts <= failuresBeforeSuccess)
            {
                throw exceptionFactory(Attempts);
            }

            return Task.FromResult(Attempts);
        }
    }
}
