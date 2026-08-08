using FlashQueue.Consumers.Notifications.Consumers;
using FlashQueue.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

// WebApplication solo para exponer /health; el trabajo real es consumir de RabbitMQ.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRabbitMqMessaging(builder.Configuration, serviceName: "notifications", configureConsumers: x =>
{
    x.AddConsumer<ReservationConfirmedConsumer>();
    x.AddConsumer<ReservationRejectedConsumer>();
});

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

await app.RunAsync();
