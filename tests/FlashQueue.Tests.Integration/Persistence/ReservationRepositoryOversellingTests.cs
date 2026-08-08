using Dapper;
using FlashQueue.Domain.Entities;
using FlashQueue.Infrastructure.Persistence;
using FluentAssertions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace FlashQueue.Tests.Integration.Persistence;

/// <summary>
/// El test más importante del proyecto: prueba, contra un Postgres real (Testcontainers, no
/// un mock), que <see cref="ReservationRepository"/> nunca vende más stock del disponible bajo
/// contención extrema. Ver docs/adr/0002-locking-skip-locked-con-reintentos.md.
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

        // Maximum Pool Size se mantiene deliberadamente por debajo del max_connections por
        // defecto de Postgres (100). El test admite las 20.000 peticiones con un semáforo
        // propio (ver más abajo) del mismo tamaño que el pool, así ninguna llamada llega a
        // esperar dentro de la cola de conexiones de Npgsql (que tiene su propio timeout).
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

        var repository = new ReservationRepository(_dataSource, TimeProvider.System, new ReservationRepositoryOptions
        {
            LockAcquisitionTimeout = TimeSpan.FromMinutes(2),
            LockRetryDelay = TimeSpan.FromMilliseconds(2),
        });

        // Admite las 20.000 peticiones concurrentemente, pero acota cuántas están realmente
        // en vuelo (y por tanto cuántas conexiones Npgsql abiertas a la vez) al tamaño del
        // pool: así el pool de conexiones nunca actúa como cuello de botella artificial, y el
        // único límite real sigue siendo el lock de fila que este test está verificando.
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
