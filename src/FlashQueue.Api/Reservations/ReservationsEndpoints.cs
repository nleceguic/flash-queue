using FlashQueue.Application.Ingestion;
using FlashQueue.Domain.Entities;
using Microsoft.AspNetCore.Http.HttpResults;

namespace FlashQueue.Api.Reservations;

public static class ReservationsEndpoints
{
    public const string RateLimiterPolicyName = "reservations-per-event";

    public static IEndpointRouteBuilder MapReservationsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/events/{eventId:guid}/reservations", CreateReservationAsync)
            .WithName("CreateReservation")
            .RequireRateLimiting(RateLimiterPolicyName);

        return app;
    }

    private static async Task<Results<Accepted<ReservationAcceptedResponse>, ValidationProblem>> CreateReservationAsync(
        Guid eventId,
        CreateReservationRequestBody body,
        ReservationIngestChannel ingestChannel,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        if (eventId == Guid.Empty)
        {
            errors[nameof(eventId)] = ["El identificador del evento es obligatorio."];
        }

        if (body.UserId == Guid.Empty)
        {
            errors[nameof(body.UserId)] = ["El identificador de usuario es obligatorio."];
        }

        if (body.Quantity <= 0)
        {
            errors[nameof(body.Quantity)] = ["La cantidad debe ser mayor que cero."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var reservationRequest = new ReservationRequest(
            Guid.NewGuid(), eventId, body.UserId, body.Quantity, timeProvider.GetUtcNow());

        await ingestChannel.Writer.WriteAsync(reservationRequest, cancellationToken);

        return TypedResults.Accepted<ReservationAcceptedResponse>(
            uri: (string?)null,
            new ReservationAcceptedResponse(
                reservationRequest.Id, reservationRequest.EventId, reservationRequest.Quantity, reservationRequest.RequestedAt));
    }
}
