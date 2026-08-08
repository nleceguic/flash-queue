using FlashQueue.Api.RateLimiting;
using FlashQueue.Api.Reservations;
using FlashQueue.Application.Ingestion;
using FlashQueue.Application.Observability;
using FlashQueue.Infrastructure.Observability;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.Configure<ReservationIngestOptions>(
    builder.Configuration.GetSection(ReservationIngestOptions.SectionName));
builder.Services.AddSingleton(sp =>
    new ReservationIngestChannel(sp.GetRequiredService<IOptions<ReservationIngestOptions>>().Value));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddReservationsRateLimiting();
builder.Services.AddObservability(builder.Configuration, serviceName: "flashqueue-api");

var app = builder.Build();

// Tamaño del channel de ingesta, muestreado bajo demanda por el exportador de métricas (no hay
// timer propio: OpenTelemetry ya hace el scraping periódico de los ObservableGauge).
FlashQueueDiagnostics.ObserveChannelSize(() => app.Services.GetRequiredService<ReservationIngestChannel>().Reader.Count);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapReservationsEndpoints();

app.Run();

public partial class Program
{
}
