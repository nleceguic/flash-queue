using FlashQueue.Infrastructure.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Polly.CircuitBreaker;

namespace FlashQueue.Workers.Health;

public static class DependenciesHealthEndpoints
{
    public static IEndpointRouteBuilder MapDependenciesHealthEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health/dependencies", GetDependenciesHealth).WithName("DependenciesHealth");

        return app;
    }

    private static Ok<DependenciesHealthResponse> GetDependenciesHealth(
        RabbitMqPublishResiliencePipelineProvider rabbitMqResilience)
    {
        // Antes de la primera publicación, CircuitState ya reporta Closed por defecto: todavía no
        // hay evidencia de que RabbitMQ esté fallando.
        var state = rabbitMqResilience.CircuitBreakerStateProvider.CircuitState;

        var rabbitMq = new DependencyHealth(
            Name: "rabbitmq-publish",
            CircuitState: state.ToString(),
            Healthy: state is CircuitState.Closed or CircuitState.HalfOpen);

        return TypedResults.Ok(new DependenciesHealthResponse([rabbitMq]));
    }
}

public sealed record DependenciesHealthResponse(IReadOnlyList<DependencyHealth> Dependencies);

public sealed record DependencyHealth(string Name, string CircuitState, bool Healthy);
