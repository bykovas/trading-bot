using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace TradingBot.Worker;

internal interface IDryRunPortfolioStore
{
    string StateDescription { get; }
    string EventsDescription { get; }
    PortfolioState? Load();
    void Save(PortfolioState state);
    void AppendCycle(DryRunCycleRecord record);
}

internal sealed class FileDryRunPortfolioStore(DryRunOptions options) : IDryRunPortfolioStore
{
    private readonly JsonSerializerOptions _stateJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly JsonSerializerOptions _eventJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private string StatePath => Path.Combine(options.OutputDirectory, options.StateFile);
    private string EventsPath => Path.Combine(options.OutputDirectory, options.EventsFile);

    public string StateDescription => StatePath;
    public string EventsDescription => EventsPath;

    public PortfolioState? Load()
    {
        Directory.CreateDirectory(options.OutputDirectory);

        if (!File.Exists(StatePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(StatePath);
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<PortfolioState>(json, _stateJsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Console.WriteLine($"portfolio-load: failed to read {StatePath} ({ex.Message})");
            return null;
        }
    }

    public void Save(PortfolioState state)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, _stateJsonOptions));
    }

    public void AppendCycle(DryRunCycleRecord record)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        var line = JsonSerializer.Serialize(record, _eventJsonOptions);
        File.AppendAllText(EventsPath, line + Environment.NewLine);
    }
}

internal sealed class PostgresDryRunPortfolioStore(string connectionString) : IDryRunPortfolioStore
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private bool _schemaReady;

    public string StateDescription => "postgres:portfolio_state";
    public string EventsDescription => "postgres:dry_run_cycles";

    public PortfolioState? Load()
    {
        EnsureSchema();

        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(
            "select state_json::text from portfolio_state where id = 1",
            connection);
        var value = command.ExecuteScalar() as string;
        return string.IsNullOrWhiteSpace(value)
            ? null
            : JsonSerializer.Deserialize<PortfolioState>(value, _jsonOptions);
    }

    public void Save(PortfolioState state)
    {
        EnsureSchema();

        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(
            """
            insert into portfolio_state (id, updated_at, state_json)
            values (1, @updated_at, @state_json)
            on conflict (id) do update set
                updated_at = excluded.updated_at,
                state_json = excluded.state_json
            """,
            connection);
        command.Parameters.AddWithValue("updated_at", state.UpdatedAt.UtcDateTime);
        command.Parameters.Add("state_json", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(state, _jsonOptions);
        command.ExecuteNonQuery();
    }

    public void AppendCycle(DryRunCycleRecord record)
    {
        EnsureSchema();

        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(
            """
            insert into dry_run_cycles (cycle_id, utc, record_json)
            values (@cycle_id, @utc, @record_json)
            on conflict (cycle_id) do update set
                utc = excluded.utc,
                record_json = excluded.record_json
            """,
            connection);
        command.Parameters.AddWithValue("cycle_id", record.CycleId);
        command.Parameters.AddWithValue("utc", record.Utc.UtcDateTime);
        command.Parameters.Add("record_json", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(record, _jsonOptions);
        command.ExecuteNonQuery();
    }

    private void EnsureSchema()
    {
        if (_schemaReady)
        {
            return;
        }

        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(
            """
            create table if not exists portfolio_state (
                id integer primary key check (id = 1),
                updated_at timestamptz not null,
                state_json jsonb not null
            );

            create table if not exists dry_run_cycles (
                cycle_id text primary key,
                utc timestamptz not null,
                record_json jsonb not null
            );

            create index if not exists ix_dry_run_cycles_utc on dry_run_cycles (utc desc);
            """,
            connection);
        command.ExecuteNonQuery();
        _schemaReady = true;
    }

    private NpgsqlConnection OpenConnection()
    {
        var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        return connection;
    }
}
