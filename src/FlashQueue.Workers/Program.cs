using FlashQueue.Application.Ingestion;
using FlashQueue.Application.Processing;
using FlashQueue.Infrastructure;
using FlashQueue.Infrastructure.Persistence;
using FlashQueue.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<ReservationIngestOptions>(
    builder.Configuration.GetSection(ReservationIngestOptions.SectionName));
builder.Services.AddSingleton(sp =>
    new ReservationIngestChannel(sp.GetRequiredService<IOptions<ReservationIngestOptions>>().Value));

builder.Services.Configure<ReservationProcessingOptions>(
    builder.Configuration.GetSection(ReservationProcessingOptions.SectionName));
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddHostedService<ReservationProcessingWorker>();

var host = builder.Build();

var migrator = host.Services.GetRequiredService<SchemaMigrator>();
await migrator.EnsureSchemaAsync(CancellationToken.None);

await host.RunAsync();
