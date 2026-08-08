using System.Diagnostics;
using FlashQueue.Application.Ingestion;
using FlashQueue.Application.Observability;
using FlashQueue.Domain.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;

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

        // Su contexto viaja con el item a través del channel para que el worker pueda reconectar
        // la traza al otro lado del boundary asíncrono.
        using var activity = FlashQueueDiagnostics.ActivitySource.StartActivity(
            "reservation.enqueue", ActivityKind.Producer);
        activity?.SetTag("reservation.id", reservationRequest.Id);
        activity?.SetTag("event.id", reservationRequest.EventId);
        activity?.SetTag("reservation.quantity", reservationRequest.Quantity);

        var traceContext = activity?.Context ?? Activity.Current?.Context ?? default;
        await ingestChannel.Writer.WriteAsync(new ReservationIngestItem(reservationRequest, traceContext), cancellationToken);

        return TypedResults.Accepted<ReservationAcceptedResponse>(
            uri: (string?)null,
            new ReservationAcceptedResponse(
                reservationRequest.Id, reservationRequest.EventId, reservationRequest.Quantity, reservationRequest.RequestedAt));
    }
}
