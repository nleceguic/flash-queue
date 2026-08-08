using FlashQueue.Application.Ingestion;
using FlashQueue.Application.Observability;
using FlashQueue.Application.Processing;
using FlashQueue.Infrastructure;
using FlashQueue.Infrastructure.Observability;
using FlashQueue.Infrastructure.Persistence;
using FlashQueue.Workers;
using FlashQueue.Workers.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ReservationIngestOptions>(
    builder.Configuration.GetSection(ReservationIngestOptions.SectionName));
builder.Services.AddSingleton(sp =>
    new ReservationIngestChannel(sp.GetRequiredService<IOptions<ReservationIngestOptions>>().Value));

builder.Services.Configure<ReservationProcessingOptions>(
    builder.Configuration.GetSection(ReservationProcessingOptions.SectionName));
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddObservability(builder.Configuration, serviceName: "flashqueue-workers");

builder.Services.AddHostedService<ReservationProcessingWorker>();

var app = builder.Build();

app.MapDependenciesHealthEndpoint();

// Tamaño del channel de ingesta DE ESTE PROCESO (Workers tiene su propia instancia, separada de
// la de Api — ver docs/adr/0006, sección de limitaciones conocidas). Mismo mecanismo que en Api.
FlashQueueDiagnostics.ObserveChannelSize(() => app.Services.GetRequiredService<ReservationIngestChannel>().Reader.Count);

var migrator = app.Services.GetRequiredService<SchemaMigrator>();
await migrator.EnsureSchemaAsync(CancellationToken.None);

await app.RunAsync();
