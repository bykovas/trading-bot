using Npgsql;

namespace EasyBot.Data;

public interface IDbConnectionFactory
{
    NpgsqlConnection CreateConnection();
}

public sealed class DbConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public DbConnectionFactory(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Missing ConnectionStrings:Postgres configuration.");
    }

    public NpgsqlConnection CreateConnection() => new(_connectionString);
}

/// <summary>
/// Applies plain SQL migration files from Data/migrations in filename order, tracked via
/// the __migrations table. Runs once at startup before the trading loop or dashboard serve
/// any traffic.
/// </summary>
public sealed class Migrator
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<Migrator> _logger;
    private readonly string _migrationsDirectory;

    public Migrator(IDbConnectionFactory connectionFactory, ILogger<Migrator> logger, IHostEnvironment environment)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
        _migrationsDirectory = Path.Combine(environment.ContentRootPath, "Data", "migrations");
    }

    public async Task ApplyAsync(CancellationToken ct = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);

        await using (var createTable = connection.CreateCommand())
        {
            createTable.CommandText = """
                CREATE TABLE IF NOT EXISTS __migrations (
                    filename    text PRIMARY KEY,
                    applied_at  timestamptz NOT NULL DEFAULT now()
                );
                """;
            await createTable.ExecuteNonQueryAsync(ct);
        }

        var applied = new HashSet<string>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT filename FROM __migrations";
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                applied.Add(reader.GetString(0));
        }

        var files = Directory.Exists(_migrationsDirectory)
            ? Directory.GetFiles(_migrationsDirectory, "*.sql").OrderBy(f => f, StringComparer.Ordinal)
            : Enumerable.Empty<string>();

        foreach (var file in files)
        {
            var filename = Path.GetFileName(file);
            if (applied.Contains(filename))
                continue;

            _logger.LogInformation("Applying migration {Migration}", filename);
            var sql = await File.ReadAllTextAsync(file, ct);

            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using (var apply = connection.CreateCommand())
            {
                apply.Transaction = transaction;
                apply.CommandText = sql;
                await apply.ExecuteNonQueryAsync(ct);
            }

            await using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText = "INSERT INTO __migrations (filename) VALUES (@filename)";
                record.Parameters.AddWithValue("filename", filename);
                await record.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
    }
}
