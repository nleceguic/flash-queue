using FlashQueue.Api.RateLimiting;
using FlashQueue.Api.Reservations;
using FlashQueue.Application.Ingestion;
using FlashQueue.Application.Observability;
using FlashQueue.Application.Processing;
using FlashQueue.Application.Stats;
using FlashQueue.Infrastructure;
using FlashQueue.Infrastructure.Observability;
using FlashQueue.Infrastructure.Persistence;
using FlashQueue.Workers;
using FlashQueue.Workers.Events;
using FlashQueue.Workers.Health;
using FlashQueue.Workers.Stats;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

// Proceso único: hospeda la ingesta HTTP (FlashQueue.Api, como librería) y el worker en el mismo
// host, para que compartan la misma instancia de ReservationIngestChannel (ver ADR 0013).
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddReservationsRateLimiting();

builder.Services.Configure<ReservationIngestOptions>(
    builder.Configuration.GetSection(ReservationIngestOptions.SectionName));
builder.Services.AddSingleton(sp =>
    new ReservationIngestChannel(sp.GetRequiredService<IOptions<ReservationIngestOptions>>().Value));

builder.Services.Configure<ReservationProcessingOptions>(
    builder.Configuration.GetSection(ReservationProcessingOptions.SectionName));
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddObservability(builder.Configuration, serviceName: "flashqueue");

// Panel en vivo (wwwroot/dashboard.html): sustituye el notifier no-op por el real.
builder.Services.AddSignalR();
builder.Services.RemoveAll<IReservationStatsNotifier>();
builder.Services.AddSingleton<IReservationStatsNotifier, SignalRReservationStatsNotifier>();

builder.Services.AddHostedService<ReservationProcessingWorker>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseStaticFiles();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapReservationsEndpoints();
app.MapDependenciesHealthEndpoint();
app.MapEventStatusEndpoint();
app.MapHub<ReservationStatsHub>("/hubs/reservation-stats");

FlashQueueDiagnostics.ObserveChannelSize(() => app.Services.GetRequiredService<ReservationIngestChannel>().Reader.Count);

var migrator = app.Services.GetRequiredService<SchemaMigrator>();
await migrator.EnsureSchemaAsync(CancellationToken.None);

await app.RunAsync();

public partial class Program
{
}
