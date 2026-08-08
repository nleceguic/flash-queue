using FlashQueue.Consumers.Payments.Consumers;
using FlashQueue.Infrastructure;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddRabbitMqMessaging(builder.Configuration, serviceName: "payments", configureConsumers: x =>
{
    x.AddConsumer<ReservationConfirmedConsumer>();
    x.AddConsumer<ReservationRejectedConsumer>();
});

var host = builder.Build();

await host.RunAsync();
