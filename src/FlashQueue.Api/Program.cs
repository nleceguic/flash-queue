using FlashQueue.Api.RateLimiting;
using FlashQueue.Api.Reservations;
using FlashQueue.Application.Ingestion;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.Configure<ReservationIngestOptions>(
    builder.Configuration.GetSection(ReservationIngestOptions.SectionName));
builder.Services.AddSingleton(sp =>
    new ReservationIngestChannel(sp.GetRequiredService<IOptions<ReservationIngestOptions>>().Value));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddReservationsRateLimiting();

var app = builder.Build();

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
