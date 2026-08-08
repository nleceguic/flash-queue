using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using FlashQueue.Api.Reservations;
using FlashQueue.Application.Ingestion;
using FlashQueue.Tests.Integration.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FlashQueue.Tests.Integration.Reservations;

public sealed class ReservationsEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ReservationsEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task PostReservations_With2000ConcurrentRequests_QueuesEveryRequestExactlyOnce()
    {
        const int requestCount = 2000;
        var eventId = Guid.NewGuid();

        using var client = _factory.CreateClient();
        var ingestChannel = _factory.Services.GetRequiredService<ReservationIngestChannel>();

        var drainedIds = new ConcurrentBag<Guid>();
        using var drainCts = new CancellationTokenSource();
        var drainTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var request in ingestChannel.Reader.ReadAllAsync(drainCts.Token))
                {
                    drainedIds.Add(request.Id);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        var responses = await Task.WhenAll(Enumerable.Range(0, requestCount).Select(async _ =>
        {
            var response = await client.PostAsJsonAsync(
                $"/events/{eventId}/reservations", new CreateReservationRequestBody(Guid.NewGuid(), 1));
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            return await response.Content.ReadFromJsonAsync<ReservationAcceptedResponse>();
        }));

        await Polling.UntilAsync(() => drainedIds.Count >= requestCount, TimeSpan.FromSeconds(15));
        drainCts.Cancel();
        await drainTask;

        var returnedIds = responses.Select(r => r!.ReservationId).ToList();
        returnedIds.Should().OnlyHaveUniqueItems();
        returnedIds.Should().HaveCount(requestCount);
        drainedIds.Should().BeEquivalentTo(returnedIds);
    }
}
