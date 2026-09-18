using Npgsql;

namespace TradingBot.FuturesWorker;

internal sealed record BotUniversePreferences(
    int AutoInstrumentCount,
    IReadOnlyList<string> ForceIncludePairs,
    IReadOnlyList<string> ForceExcludePairs);

internal interface IBotUniversePreferenceStore
{
    Task<BotUniversePreferences?> LoadAsync(string botInstanceId, CancellationToken cancellationToken);
}

internal sealed class NullBotUniversePreferenceStore : IBotUniversePreferenceStore
{
    public Task<BotUniversePreferences?> LoadAsync(string botInstanceId, CancellationToken cancellationToken) =>
        Task.FromResult<BotUniversePreferences?>(null);
}

internal sealed class PostgresBotUniversePreferenceStore : IBotUniversePreferenceStore
{
    private readonly string _connectionString;

    public PostgresBotUniversePreferenceStore(string connectionString)
    {
        _connectionString = connectionString;
        EnsureSchema();
    }

    public async Task<BotUniversePreferences?> LoadAsync(string botInstanceId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select auto_instrument_count, force_include_pairs, force_exclude_pairs
            from bot_universe_preferences
            where bot_instance_id = @bot_instance_id
            """,
            connection);
        command.Parameters.AddWithValue("bot_instance_id", botInstanceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new BotUniversePreferences(
            reader.GetInt32(0),
            NormalizePairs(reader.GetFieldValue<string[]>(1)),
            NormalizePairs(reader.GetFieldValue<string[]>(2)));
    }

    private void EnsureSchema()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(
            """
            create table if not exists bot_universe_preferences (
                bot_instance_id text primary key,
                auto_instrument_count integer not null check (auto_instrument_count between 0 and 500),
                force_include_pairs text[] not null default '{}'::text[],
                force_exclude_pairs text[] not null default '{}'::text[],
                updated_at timestamptz not null default now(),
                updated_by text,
                check (btrim(bot_instance_id) <> ''),
                check (not (force_include_pairs && force_exclude_pairs))
            );
            """,
            connection);
        command.ExecuteNonQuery();
    }

    internal static IReadOnlyList<string> NormalizePairs(IEnumerable<string> pairs) =>
        pairs
            .SelectMany(pair => pair.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(pair => pair.Trim().ToUpperInvariant())
            .Where(pair => pair.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(pair => pair, StringComparer.Ordinal)
            .ToArray();
}
