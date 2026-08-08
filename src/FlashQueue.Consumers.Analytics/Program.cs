using FlashQueue.Consumers.Analytics.Consumers;
using FlashQueue.Infrastructure;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddRabbitMqMessaging(builder.Configuration, serviceName: "analytics", configureConsumers: x =>
{
    x.AddConsumer<ReservationConfirmedConsumer>();
    x.AddConsumer<ReservationRejectedConsumer>();
});

var host = builder.Build();

await host.RunAsync();
