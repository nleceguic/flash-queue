using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace FlashQueue.Tests.Integration.Support;

/// <summary><c>AddInfrastructure</c> lee la connection string antes de que <c>WithWebHostBuilder</c> pueda sobrescribirla, así que hace falta una variable de entorno.</summary>
internal static class InfrastructureEnvironmentVariables
{
    public static void SetFor(PostgreSqlContainer postgres, RabbitMqContainer rabbitMq)
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__FlashQueueDb", postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("RabbitMq__Host", rabbitMq.Hostname);
        Environment.SetEnvironmentVariable("RabbitMq__Port", rabbitMq.GetMappedPublicPort(5672).ToString());
        Environment.SetEnvironmentVariable("RabbitMq__VirtualHost", "/");
        Environment.SetEnvironmentVariable("RabbitMq__Username", "flashqueue");
        Environment.SetEnvironmentVariable("RabbitMq__Password", "flashqueue");
    }

    public static void Clear()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__FlashQueueDb", null);
        Environment.SetEnvironmentVariable("RabbitMq__Host", null);
        Environment.SetEnvironmentVariable("RabbitMq__Port", null);
        Environment.SetEnvironmentVariable("RabbitMq__VirtualHost", null);
        Environment.SetEnvironmentVariable("RabbitMq__Username", null);
        Environment.SetEnvironmentVariable("RabbitMq__Password", null);
    }
}
