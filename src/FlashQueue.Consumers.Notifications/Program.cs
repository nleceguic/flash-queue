using FlashQueue.Consumers.Notifications.Consumers;
using FlashQueue.Infrastructure;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddRabbitMqMessaging(builder.Configuration, serviceName: "notifications", configureConsumers: x =>
{
    x.AddConsumer<ReservationConfirmedConsumer>();
    x.AddConsumer<ReservationRejectedConsumer>();
});

var host = builder.Build();

await host.RunAsync();
