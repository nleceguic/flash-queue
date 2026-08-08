using Dapper;
using FlashQueue.Domain.Entities;
using FlashQueue.Infrastructure.Chaos;
using FlashQueue.Infrastructure.Persistence;
using FluentAssertions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace FlashQueue.Tests.Integration.Persistence;

/// <summary>
/// El test más importante del proyecto: contra Postgres real, bajo contención extrema,
/// <see cref="ReservationRepository"/> nunca vende más stock del disponible.
/// </summary>
public sealed class ReservationRepositoryOversellingTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("flashqueue")
        .WithUsername("flashqueue")
        .WithPassword("flashqueue")
        .Build();

    private NpgsqlDataSource _dataSource = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Por debajo del max_connections por defecto de Postgres (100); el semáforo de abajo
        // limita las peticiones en vuelo al tamaño del pool para no encolar en Npgsql.
        var connectionString = $"{_postgres.GetConnectionString()};Maximum Pool Size=80;Timeout=30;Command Timeout=30";
        _dataSource = NpgsqlDataSource.Create(connectionString);

        await new SchemaMigrator(_dataSource).EnsureSchemaAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task ReserveAsync_With20000ConcurrentRequestsAgainstOneEvent_NeverOversells()
    {
        const int totalStock = 500;
        const int requestCount = 20_000;
        var eventId = Guid.NewGuid();

        await using (var setupConnection = await _dataSource.OpenConnectionAsync())
        {
            await setupConnection.ExecuteAsync(
                "INSERT INTO events (id, name, total_stock, reserved_stock) VALUES (@Id, @Name, @TotalStock, 0)",
                new { Id = eventId, Name = "Concierto de prueba (overselling)", TotalStock = totalStock });
        }

        var repository = new ReservationRepository(
            _dataSource,
            TimeProvider.System,
            new ReservationRepositoryOptions
            {
                LockAcquisitionTimeout = TimeSpan.FromMinutes(2),
                LockRetryDelay = TimeSpan.FromMilliseconds(2),
            },
            new NullChaosInjector());

        // Acota las conexiones en vuelo al tamaño del pool para que el único límite real sea
        // el lock de fila que este test verifica, no el pool de conexiones.
        using var admission = new SemaphoreSlim(64, 64);

        var results = await Task.WhenAll(Enumerable.Range(0, requestCount).Select(async _ =>
        {
            await admission.WaitAsync();
            try
            {
                return await repository.ReserveAsync(
                    new ReservationRequest(Guid.NewGuid(), eventId, Guid.NewGuid(), 1, DateTimeOffset.UtcNow),
                    CancellationToken.None);
            }
            finally
            {
                admission.Release();
            }
        }));

        results.Should().HaveCount(requestCount);
        results.Count(r => r.Status == ReservationStatus.Confirmed).Should().Be(totalStock);
        results.Count(r => r.Status == ReservationStatus.Rejected).Should().Be(requestCount - totalStock);

        await using var verifyConnection = await _dataSource.OpenConnectionAsync();

        var confirmedInDb = await verifyConnection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM reservations WHERE event_id = @EventId AND status = 'Confirmed'",
            new { EventId = eventId });
        var rejectedInDb = await verifyConnection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM reservations WHERE event_id = @EventId AND status = 'Rejected'",
            new { EventId = eventId });
        var totalInDb = await verifyConnection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM reservations WHERE event_id = @EventId",
            new { EventId = eventId });

        confirmedInDb.Should().Be(totalStock, "debe haber exactamente tantas reservas Confirmed como stock inicial");
        rejectedInDb.Should().Be(requestCount - totalStock);
        totalInDb.Should().Be(requestCount, "ninguna de las 20.000 peticiones debe perderse");

        var stock = await verifyConnection.QuerySingleAsync<EventStockRow>(
            "SELECT total_stock AS TotalStock, reserved_stock AS ReservedStock FROM events WHERE id = @EventId",
            new { EventId = eventId });

        stock.ReservedStock.Should().Be(totalStock, "todo el stock debe haberse agotado exactamente, ni más ni menos");
        stock.ReservedStock.Should().BeLessThanOrEqualTo(
            stock.TotalStock, "nunca debe reservarse más stock del que existe: cero overselling");
    }

    private sealed record EventStockRow(int TotalStock, int ReservedStock);
}
