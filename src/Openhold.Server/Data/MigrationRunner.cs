using System.Reflection;
using Dapper;
using Npgsql;

namespace Openhold.Server.Data;

/// <summary>
/// Applies embedded <c>Data/Migrations/NNN_name.sql</c> scripts in order, each in its own transaction.
/// A Postgres advisory lock stops two server instances migrating at once.
/// </summary>
public static class MigrationRunner
{
    private const long AdvisoryLockKey = 0x5374726174614D;

    public static async Task MigrateAsync(NpgsqlDataSource dataSource, ILogger logger, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync("SELECT pg_advisory_lock(@key)", new { key = AdvisoryLockKey });
        try
        {
            await connection.ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version    int PRIMARY KEY,
                    name       text NOT NULL,
                    applied_at timestamptz NOT NULL DEFAULT now()
                )
                """);
            var applied = (await connection.QueryAsync<int>("SELECT version FROM schema_migrations")).ToHashSet();

            foreach (var (version, name, sql) in LoadMigrations())
            {
                if (applied.Contains(version))
                    continue;

                logger.LogInformation("Applying migration {Version} {Name}", version, name);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await connection.ExecuteAsync(sql, transaction: transaction);
                await connection.ExecuteAsync(
                    "INSERT INTO schema_migrations (version, name) VALUES (@version, @name)",
                    new { version, name }, transaction);
                await transaction.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            await connection.ExecuteAsync("SELECT pg_advisory_unlock(@key)", new { key = AdvisoryLockKey });
        }
    }

    private static IEnumerable<(int Version, string Name, string Sql)> LoadMigrations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string prefix = "Openhold.Server.Data.Migrations.";

        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(resource =>
            {
                var fileName = resource[prefix.Length..^4];
                var separator = fileName.IndexOf('_');
                var version = int.Parse(fileName[..separator]);
                using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
                return (version, fileName[(separator + 1)..], reader.ReadToEnd());
            })
            .OrderBy(m => m.version);
    }
}
