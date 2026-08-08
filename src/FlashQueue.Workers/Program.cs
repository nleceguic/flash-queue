using FlashQueue.Api.RateLimiting;
using FlashQueue.Api.Reservations;
using FlashQueue.Application.Ingestion;
using FlashQueue.Application.Observability;
using FlashQueue.Application.Processing;
using FlashQueue.Infrastructure;
using FlashQueue.Infrastructure.Observability;
using FlashQueue.Infrastructure.Persistence;
using FlashQueue.Workers;
using FlashQueue.Workers.Events;
using FlashQueue.Workers.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
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

builder.Services.AddHostedService<ReservationProcessingWorker>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapReservationsEndpoints();
app.MapDependenciesHealthEndpoint();
app.MapEventStatusEndpoint();

FlashQueueDiagnostics.ObserveChannelSize(() => app.Services.GetRequiredService<ReservationIngestChannel>().Reader.Count);

var migrator = app.Services.GetRequiredService<SchemaMigrator>();
await migrator.EnsureSchemaAsync(CancellationToken.None);

await app.RunAsync();

public partial class Program
{
}
