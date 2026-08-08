using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace FlashQueue.Workers.Events;

/// <summary>Expone el stock de un evento en tiempo real, leyendo Postgres directamente (sin caché).</summary>
public static class EventStatusEndpoints
{
    public static IEndpointRouteBuilder MapEventStatusEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/events/{eventId:guid}/status", GetEventStatusAsync).WithName("EventStatus");

        return app;
    }

    private static async Task<Results<Ok<EventStatusResponse>, NotFound>> GetEventStatusAsync(
        Guid eventId, NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<EventStatusRow>(new CommandDefinition(
            """
            SELECT
                e.id AS EventId,
                e.name AS Name,
                e.total_stock AS TotalStock,
                e.reserved_stock AS ReservedStock,
                (SELECT COUNT(*) FROM reservations r WHERE r.event_id = e.id AND r.status = 'Confirmed') AS ConfirmedReservations,
                (SELECT COUNT(*) FROM reservations r WHERE r.event_id = e.id AND r.status = 'Rejected') AS RejectedReservations
            FROM events e
            WHERE e.id = @EventId
            """,
            new { EventId = eventId },
            cancellationToken: cancellationToken));

        if (row is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(new EventStatusResponse(
            row.EventId,
            row.Name,
            row.TotalStock,
            row.ReservedStock,
            AvailableStock: row.TotalStock - row.ReservedStock,
            row.ConfirmedReservations,
            row.RejectedReservations));
    }

    private sealed class EventStatusRow
    {
        public Guid EventId { get; init; }
        public string Name { get; init; } = "";
        public int TotalStock { get; init; }
        public int ReservedStock { get; init; }
        public long ConfirmedReservations { get; init; }
        public long RejectedReservations { get; init; }
    }
}

public sealed record EventStatusResponse(
    Guid EventId,
    string Name,
    int TotalStock,
    int ReservedStock,
    int AvailableStock,
    long ConfirmedReservations,
    long RejectedReservations);
