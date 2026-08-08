using System.Threading.RateLimiting;
using FlashQueue.Api.Reservations;
using Microsoft.AspNetCore.RateLimiting;

namespace FlashQueue.Api.RateLimiting;

public static class ReservationsRateLimiterExtensions
{
    public static IServiceCollection AddReservationsRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(ReservationsEndpoints.RateLimiterPolicyName, httpContext =>
            {
                var eventId = httpContext.Request.RouteValues["eventId"]?.ToString() ?? "unknown-event";

                return RateLimitPartition.GetFixedWindowLimiter(eventId, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 3_000,
                    Window = TimeSpan.FromSeconds(1),
                    QueueLimit = 2_000,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                });
            });
        });

        return services;
    }
}
