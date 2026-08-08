using System.Reflection;
using Dapper;
using Npgsql;

namespace FlashQueue.Infrastructure.Persistence;

/// <summary>
/// Aplica el esquema SQL embebido (<c>Persistence/Migrations/*.sql</c>) de forma idempotente,
/// vía Npgsql/Dapper directo (sin EF), tal como exige CLAUDE.md para las tablas de stock/reserva.
/// </summary>
public sealed class SchemaMigrator(NpgsqlDataSource dataSource)
{
    private const string ScriptFileName = "0001_initial_schema.sql";

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        var script = ReadEmbeddedScript();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(script, cancellationToken: cancellationToken));
    }

    private static string ReadEmbeddedScript()
    {
        var assembly = typeof(SchemaMigrator).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(ScriptFileName, StringComparison.Ordinal));

        if (resourceName is null)
        {
            throw new InvalidOperationException(
                $"No se encontró el recurso embebido '{ScriptFileName}' en el ensamblado {assembly.FullName}.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"No se pudo abrir el recurso embebido '{resourceName}'.");
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
