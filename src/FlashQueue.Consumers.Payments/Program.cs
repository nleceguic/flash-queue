using FlashQueue.Consumers.Payments.Consumers;
using FlashQueue.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

// WebApplication (no Host.CreateApplicationBuilder) solo para exponer /health: docker-compose.yml
// necesita una superficie HTTP para el healthcheck de este servicio, aunque su trabajo real sea
// consumir de RabbitMQ, no servir peticiones.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRabbitMqMessaging(builder.Configuration, serviceName: "payments", configureConsumers: x =>
{
    x.AddConsumer<ReservationConfirmedConsumer>();
    x.AddConsumer<ReservationRejectedConsumer>();
});

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

await app.RunAsync();
