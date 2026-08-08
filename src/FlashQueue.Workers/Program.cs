using FlashQueue.Application.Ingestion;
using FlashQueue.Application.Processing;
using FlashQueue.Workers;
using FlashQueue.Workers.Processing;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<ReservationIngestOptions>(
    builder.Configuration.GetSection(ReservationIngestOptions.SectionName));
builder.Services.AddSingleton(sp =>
    new ReservationIngestChannel(sp.GetRequiredService<IOptions<ReservationIngestOptions>>().Value));

builder.Services.Configure<ReservationProcessingOptions>(
    builder.Configuration.GetSection(ReservationProcessingOptions.SectionName));
builder.Services.AddSingleton<IReservationProcessor, LoggingReservationProcessor>();

builder.Services.AddHostedService<ReservationProcessingWorker>();

var host = builder.Build();
host.Run();
