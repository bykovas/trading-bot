using System.Text.Json.Nodes;
using Npgsql;

namespace TradingBot.FuturesWorker;

internal sealed record BotStrategyProfileAssignment(
    Guid ProfileId,
    string ProfileName,
    int Revision,
    JsonObject Values,
    JsonObject Overrides);

internal interface IBotStrategyProfileStore
{
    Task<BotStrategyProfileAssignment?> LoadAsync(
        string botInstanceId,
        CancellationToken cancellationToken);
}

internal sealed class NullBotStrategyProfileStore : IBotStrategyProfileStore
{
    public Task<BotStrategyProfileAssignment?> LoadAsync(
        string botInstanceId,
        CancellationToken cancellationToken) =>
        Task.FromResult<BotStrategyProfileAssignment?>(null);
}

internal sealed class PostgresBotStrategyProfileStore : IBotStrategyProfileStore
{
    private readonly string _connectionString;

    public PostgresBotStrategyProfileStore(string connectionString)
    {
        _connectionString = connectionString;
        EnsureSchema();
    }

    public async Task<BotStrategyProfileAssignment?> LoadAsync(
        string botInstanceId,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select assignment.profile_id,
                   profile.name,
                   assignment.profile_revision,
                   revision.values_jsonb::text,
                   assignment.overrides_jsonb::text
            from bot_instance_strategy_profiles assignment
            join bot_strategy_profiles profile on profile.profile_id = assignment.profile_id
            join bot_strategy_profile_revisions revision
              on revision.profile_id = assignment.profile_id
             and revision.revision = assignment.profile_revision
            where assignment.bot_instance_id = @bot_instance_id
              and assignment.is_active = true
              and profile.is_archived = false
            """,
            connection);
        command.Parameters.AddWithValue("bot_instance_id", botInstanceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new BotStrategyProfileAssignment(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetInt32(2),
            ParseObject(reader.GetString(3), "values_jsonb"),
            ParseObject(reader.GetString(4), "overrides_jsonb"));
    }

    private void EnsureSchema()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(
            """
            create table if not exists bot_strategy_profiles (
                profile_id uuid primary key,
                name text not null unique,
                description text,
                is_archived boolean not null default false,
                created_at timestamptz not null default now(),
                created_by text,
                updated_at timestamptz not null default now(),
                updated_by text,
                check (btrim(name) <> '')
            );

            create table if not exists bot_strategy_profile_revisions (
                profile_id uuid not null references bot_strategy_profiles(profile_id) on delete restrict,
                revision integer not null,
                values_jsonb jsonb not null,
                change_note text,
                created_at timestamptz not null default now(),
                created_by text,
                primary key (profile_id, revision),
                check (revision > 0),
                check (jsonb_typeof(values_jsonb) = 'object')
            );

            create table if not exists bot_instance_strategy_profiles (
                bot_instance_id text primary key,
                profile_id uuid not null,
                profile_revision integer not null,
                overrides_jsonb jsonb not null default '{}'::jsonb,
                is_active boolean not null default true,
                created_at timestamptz not null default now(),
                created_by text,
                updated_at timestamptz not null default now(),
                updated_by text,
                foreign key (profile_id, profile_revision)
                    references bot_strategy_profile_revisions(profile_id, revision)
                    on delete restrict,
                check (btrim(bot_instance_id) <> ''),
                check (jsonb_typeof(overrides_jsonb) = 'object')
            );

            create index if not exists ix_bot_instance_strategy_profiles_profile
                on bot_instance_strategy_profiles (profile_id, profile_revision)
                where is_active;
            """,
            connection);
        command.ExecuteNonQuery();
    }

    private static JsonObject ParseObject(string json, string columnName) =>
        JsonNode.Parse(json)?.AsObject()
        ?? throw new InvalidOperationException($"Strategy profile {columnName} must be a JSON object.");
}
