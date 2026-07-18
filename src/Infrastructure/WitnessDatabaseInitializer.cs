using System.Reflection;
using Npgsql;

namespace EcmisWitness.Api.Infrastructure;

public sealed class WitnessDatabaseInitializer(NpgsqlDataSource dataSource)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.Contains("Migrations", StringComparison.Ordinal)
                           && name.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (resources.Length == 0)
            throw new InvalidOperationException("ไม่พบ migration ของระบบคุ้มครองพยาน");

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using (var bootstrap = new NpgsqlCommand("""
            CREATE SCHEMA IF NOT EXISTS witness;
            CREATE TABLE IF NOT EXISTS witness.schema_migrations (
                version text PRIMARY KEY,
                applied_at timestamptz NOT NULL DEFAULT NOW()
            );
            """, connection))
        {
            bootstrap.CommandTimeout = 120;
            await bootstrap.ExecuteNonQueryAsync(ct);
        }

        var applied = new HashSet<string>(StringComparer.Ordinal);
        await using (var appliedCommand = new NpgsqlCommand(
            "SELECT version FROM witness.schema_migrations", connection))
        await using (var appliedReader = await appliedCommand.ExecuteReaderAsync(ct))
        {
            while (await appliedReader.ReadAsync(ct))
                applied.Add(appliedReader.GetString(0));
        }

        foreach (var resourceName in resources)
        {
            var version = MigrationVersion(resourceName);
            if (applied.Contains(version))
                continue;

            await using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"ไม่พบ migration {resourceName}");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, connection) { CommandTimeout = 120 };
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static string MigrationVersion(string resourceName)
    {
        const string marker = ".Migrations.";
        var start = resourceName.IndexOf(marker, StringComparison.Ordinal);
        var end = resourceName.LastIndexOf(".sql", StringComparison.Ordinal);
        if (start < 0 || end <= start + marker.Length)
            throw new InvalidOperationException($"ชื่อ migration ไม่ถูกต้อง: {resourceName}");
        return resourceName[(start + marker.Length)..end];
    }
}
