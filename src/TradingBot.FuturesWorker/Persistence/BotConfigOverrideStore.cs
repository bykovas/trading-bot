using Npgsql;

namespace TradingBot.FuturesWorker;

internal interface IBotConfigOverrideStore
{
    Task<IReadOnlyDictionary<string, decimal>> LoadAsync(
        string botInstanceId,
        CancellationToken cancellationToken);
}

internal sealed class NullBotConfigOverrideStore : IBotConfigOverrideStore
{
    public Task<IReadOnlyDictionary<string, decimal>> LoadAsync(
        string botInstanceId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, decimal>>(
            new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase));
}

internal sealed class PostgresBotConfigOverrideStore : IBotConfigOverrideStore
{
    private readonly string _connectionString;

    public PostgresBotConfigOverrideStore(string connectionString)
    {
        _connectionString = connectionString;
        EnsureSchema();
    }

    public async Task<IReadOnlyDictionary<string, decimal>> LoadAsync(
        string botInstanceId,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select override_key, numeric_value
            from bot_config_overrides
            where bot_instance_id = @bot_instance_id
            order by override_key
            """,
            connection);
        command.Parameters.AddWithValue("bot_instance_id", botInstanceId);

        var values = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values[reader.GetString(0)] = reader.GetDecimal(1);
        }

        return values;
    }

    private void EnsureSchema()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(
            """
            create table if not exists bot_config_overrides (
                bot_instance_id text not null,
                override_key text not null,
                numeric_value numeric not null,
                updated_at timestamptz not null default now(),
                primary key (bot_instance_id, override_key)
            );
            """,
            connection);
        command.ExecuteNonQuery();
    }
}
