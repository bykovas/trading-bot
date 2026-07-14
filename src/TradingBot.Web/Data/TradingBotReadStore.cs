using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TradingBot.Web.Models;

namespace TradingBot.Web.Data;

public sealed class TradingBotReadStore(IConfiguration configuration)
{
    private string ConnectionString => Environment.GetEnvironmentVariable("TRADINGBOT_DATABASE_CONNECTION_STRING")
        ?? configuration.GetConnectionString("TradingBot")
        ?? string.Empty;

    public async Task<ShellViewModel> BuildShellAsync(string? botInstanceId, CancellationToken cancellationToken)
    {
        var instances = await GetBotInstancesAsync(cancellationToken);
        var selected = Clean(botInstanceId) ?? instances.FirstOrDefault() ?? "spot-live";
        var status = await GetStatusAsync(selected, cancellationToken);
        return new ShellViewModel(selected, status, instances);
    }

    public async Task<IReadOnlyList<string>> GetBotInstancesAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return Array.Empty<string>();
        try
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("select distinct bot_instance_id from dry_run_cycles order by bot_instance_id", connection);
            var items = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) items.Add(reader.GetString(0));
            return items;
        }
        catch (PostgresException ex) when (IsMissingSchema(ex)) { return Array.Empty<string>(); }
    }

    public async Task<BotStatusViewModel> GetStatusAsync(string? botInstanceId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return new(now, botInstanceId, "no-db", "unknown", null, null, "Database not configured", "bad");
        }

        try
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                """
                select utc, coalesce(market_data_mode, 'unknown')
                from dry_run_cycles
                where (@bot_instance_id is null or bot_instance_id = @bot_instance_id)
                order by utc desc
                limit 1
                """,
                connection);
            command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = (object?)Clean(botInstanceId) ?? DBNull.Value;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new(now, botInstanceId, "no-data", "unknown", null, null, "No cycles", "bad");
            }

            var utc = reader.GetFieldValue<DateTimeOffset>(0);
            var mode = reader.GetString(1);
            var age = Math.Max(0, (now - utc).TotalSeconds);
            var stale = age > 600;
            var runtime = stale ? "stale" : "running";
            var tone = stale ? "warn" : "ok";
            var label = $"{DataLabel(mode)} · {(stale ? "Stale" : "Running")} · updated {AgeLabel(age)} ago";
            return new(now, botInstanceId, runtime, mode, utc, age, label, tone);
        }
        catch (PostgresException ex) when (IsMissingSchema(ex))
        {
            return new(now, botInstanceId, "no-data", "unknown", null, null, "No cycle schema", "bad");
        }
    }

    public async Task<DashboardViewModel> GetDashboardAsync(string? botInstanceId, CancellationToken cancellationToken)
    {
        var shell = await BuildShellAsync(botInstanceId, cancellationToken);
        if (string.IsNullOrWhiteSpace(ConnectionString)) return new(shell, null, Array.Empty<PortfolioPositionViewModel>(), "TRADINGBOT_DATABASE_CONNECTION_STRING is not configured.");
        try
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            var summary = await ReadSummary(connection, shell.BotInstanceId, cancellationToken);
            var positions = await ReadPositions(connection, shell.BotInstanceId, cancellationToken);
            return new(shell, summary, positions, summary is null ? "portfolio_state is empty; wait for the worker to persist a cycle" : null);
        }
        catch (PostgresException ex) when (IsMissingSchema(ex))
        {
            return new(shell, null, Array.Empty<PortfolioPositionViewModel>(), "portfolio tables/views are not initialized yet.");
        }
    }

    public async Task<PageViewModel<CycleListItemViewModel>> GetCyclesAsync(string? botInstanceId, int limit, int offset, bool tradesOnly, CancellationToken cancellationToken)
    {
        var shell = await BuildShellAsync(botInstanceId, cancellationToken);
        if (string.IsNullOrWhiteSpace(ConnectionString)) return new(shell, Array.Empty<CycleListItemViewModel>(), limit, offset, null, "Database is not configured.");
        try
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            var items = new List<CycleListItemViewModel>();
            var actionFilter = tradesOnly ? "and cycle_json::text ~ 'WOULD_(BUY|SELL|OPEN|CLOSE)'" : string.Empty;
            await using var command = new NpgsqlCommand($$"""
                select cycle_id, bot_instance_id, utc, cycle_json, worker_version, change_set
                from dry_run_cycles
                where (@bot_instance_id is null or bot_instance_id = @bot_instance_id)
                {{actionFilter}}
                order by utc desc
                limit @limit offset @offset
                """, connection);
            command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = (object?)Clean(shell.BotInstanceId) ?? DBNull.Value;
            command.Parameters.Add("limit", NpgsqlDbType.Integer).Value = Math.Clamp(limit, 1, 200);
            command.Parameters.Add("offset", NpgsqlDbType.Integer).Value = Math.Max(0, offset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var json = reader.GetString(3);
                var parsed = ParseCycleJson(json);
                items.Add(new(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetFieldValue<DateTimeOffset>(2),
                    parsed.Action,
                    parsed.Pair,
                    parsed.Score,
                    parsed.TotalValue,
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
            return new(shell, items, limit, offset, items.Count == limit ? offset + limit : null);
        }
        catch (PostgresException ex) when (IsMissingSchema(ex))
        {
            return new(shell, Array.Empty<CycleListItemViewModel>(), limit, offset, null, "dry_run_cycles is not initialized yet.");
        }
    }

    public async Task<CycleDetailViewModel?> GetCycleDetailAsync(string id, string? botInstanceId, CancellationToken cancellationToken)
    {
        var shell = await BuildShellAsync(botInstanceId, cancellationToken);
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select cycle_id, bot_instance_id, utc, cycle_json from dry_run_cycles where cycle_id = @cycle_id limit 1", connection);
        command.Parameters.Add("cycle_id", NpgsqlDbType.Text).Value = id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var raw = reader.GetString(3);
        return new(shell, reader.GetString(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), PrettyJson(raw), ParseDecisions(raw));
    }

    public async Task<PageViewModel<MarketSnapshotViewModel>> GetMarketSnapshotsAsync(string? botInstanceId, string? pair, int limit, int offset, CancellationToken cancellationToken)
    {
        var shell = await BuildShellAsync(botInstanceId, cancellationToken);
        if (string.IsNullOrWhiteSpace(ConnectionString)) return new(shell, Array.Empty<MarketSnapshotViewModel>(), limit, offset, null, "Database is not configured.");
        try
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            var items = new List<MarketSnapshotViewModel>();
            await using var command = new NpgsqlCommand(
                """
                select bot_instance_id, pair, utc, last, bid, ask, change_percent, volume24h
                from market_snapshots
                where (@bot_instance_id is null or bot_instance_id = @bot_instance_id)
                  and (@pair is null or upper(pair) = upper(@pair))
                order by utc desc, cycle_id desc
                limit @limit offset @offset
                """, connection);
            command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = (object?)Clean(shell.BotInstanceId) ?? DBNull.Value;
            command.Parameters.Add("pair", NpgsqlDbType.Text).Value = (object?)Clean(pair) ?? DBNull.Value;
            command.Parameters.Add("limit", NpgsqlDbType.Integer).Value = Math.Clamp(limit, 1, 200);
            command.Parameters.Add("offset", NpgsqlDbType.Integer).Value = Math.Max(0, offset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new(
                    reader.GetString(1), reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(2),
                    GetDecimal(reader, 3), GetDecimal(reader, 4), GetDecimal(reader, 5), GetDecimal(reader, 6), GetDecimal(reader, 7)));
            }
            return new(shell, items, limit, offset, items.Count == limit ? offset + limit : null);
        }
        catch (PostgresException ex) when (IsMissingSchema(ex))
        {
            return new(shell, Array.Empty<MarketSnapshotViewModel>(), limit, offset, null, "market_snapshots is not initialized yet.");
        }
    }

    public async Task<SimulationViewModel> GetSimulationAsync(string? botInstanceId, SimulationInput input, CancellationToken cancellationToken)
    {
        var shell = await BuildShellAsync(botInstanceId, cancellationToken);
        if (string.IsNullOrWhiteSpace(ConnectionString)) return new(shell, input, null, "Database is not configured.");
        try
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                """
                select count(*)::int as candidates,
                       count(*) filter (where cycle_json::text ~ 'WOULD_(BUY|SELL|OPEN|CLOSE)')::int as trades
                from dry_run_cycles
                where (@bot_instance_id is null or bot_instance_id = @bot_instance_id)
                  and utc >= now() - (@hours::text || ' hours')::interval
                """, connection);
            command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = (object?)Clean(shell.BotInstanceId) ?? DBNull.Value;
            command.Parameters.Add("hours", NpgsqlDbType.Integer).Value = input.LastHours;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var result = await reader.ReadAsync(cancellationToken)
                ? new SimulationResultViewModel(reader.GetInt32(0), reader.GetInt32(1), 0, 0, 0, 0)
                : new SimulationResultViewModel(0, 0, 0, 0, 0, 0);
            return new(shell, input, result, "Preview SSR simulation summary; advanced what-if math can be moved from the legacy API next.");
        }
        catch (PostgresException ex) when (IsMissingSchema(ex))
        {
            return new(shell, input, null, "dry_run_cycles is not initialized yet.");
        }
    }

    private static async Task<PortfolioSummaryViewModel?> ReadSummary(NpgsqlConnection connection, string? botInstanceId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select updated_at,
                   (state_json ->> 'cashEur')::numeric,
                   coalesce((state_json ->> 'positionsValueEur')::numeric, 0),
                   coalesce((state_json ->> 'totalValueEur')::numeric, (state_json ->> 'cashEur')::numeric),
                   jsonb_array_length(coalesce(state_json -> 'positions', '[]'::jsonb)),
                   coalesce((state_json -> 'dailyRisk' ->> 'realizedPnlEur')::numeric, 0),
                   coalesce((state_json ->> 'externalPnlEur')::numeric, 0)
            from portfolio_state
            where (@bot_instance_id is null or bot_instance_id = @bot_instance_id)
            order by updated_at desc
            limit 1
            """, connection);
        command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = (object?)Clean(botInstanceId) ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetDateTime(0), reader.GetDecimal(1), reader.GetDecimal(2), reader.GetDecimal(3), reader.GetInt32(4), reader.GetDecimal(5), reader.GetDecimal(6))
            : null;
    }

    private static async Task<IReadOnlyList<PortfolioPositionViewModel>> ReadPositions(NpgsqlConnection connection, string? botInstanceId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select pos
            from portfolio_state ps,
                 jsonb_array_elements(coalesce(ps.state_json -> 'positions', '[]'::jsonb)) as pos
            where (@bot_instance_id is null or ps.bot_instance_id = @bot_instance_id)
            order by ps.updated_at desc
            limit 100
            """, connection);
        command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = (object?)Clean(botInstanceId) ?? DBNull.Value;
        var items = new List<PortfolioPositionViewModel>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            using var doc = JsonDocument.Parse(reader.GetString(0));
            var p = doc.RootElement;
            items.Add(new(
                GetString(p, "pair"), GetString(p, "side", "LONG"), GetDec(p, "quantity"), GetDec(p, "entryPrice"), GetDec(p, "lastPrice"),
                GetDec(p, "unrealizedPnlEur"), GetDec(p, "unrealizedPnlPercent"), GetNullableDec(p, "leverage"), GetDate(p, "openedAt") ?? GetDate(p, "entryUtc")));
        }
        return items;
    }

    public async Task WriteCyclesCsvAsync(Stream stream, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(stream, leaveOpen: true);
        await writer.WriteLineAsync("cycle_id,bot_instance_id,utc");
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select cycle_id, bot_instance_id, utc from dry_run_cycles order by utc desc limit 10000", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            await writer.WriteLineAsync($"{Csv(reader.GetString(0))},{Csv(reader.GetString(1))},{reader.GetFieldValue<DateTimeOffset>(2):O}");
        }
    }

    private static (string Action, string Pair, decimal? Score, decimal? TotalValue) ParseCycleJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var decisions = FindArray(root, "decisions");
            if (decisions.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in decisions.EnumerateArray())
                {
                    var action = GetString(d, "action", GetString(d, "executionAction", "NO_ORDER"));
                    if (action != "NO_ORDER") return (action, GetString(d, "pair"), GetNullableDec(d, "score"), FindDecimal(root, "totalValueEur"));
                }
            }
            return ("NO_ORDER", "—", null, FindDecimal(root, "totalValueEur"));
        }
        catch { return ("UNKNOWN", "—", null, null); }
    }

    private static IReadOnlyList<DecisionViewModel> ParseDecisions(string json)
    {
        var items = new List<DecisionViewModel>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var decisions = FindArray(doc.RootElement, "decisions");
            if (decisions.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in decisions.EnumerateArray())
                {
                    items.Add(new(GetString(d, "pair"), GetString(d, "desired", GetString(d, "desiredPosition")), GetString(d, "action", GetString(d, "executionAction")), GetNullableDec(d, "score"), GetString(d, "reason"), GetString(d, "riskReason")));
                }
            }
        }
        catch { }
        return items;
    }

    private static JsonElement FindArray(JsonElement root, string name)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in root.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.Array) return prop.Value;
                var nested = FindArray(prop.Value, name);
                if (nested.ValueKind == JsonValueKind.Array) return nested;
            }
        }
        return default;
    }

    private static decimal? FindDecimal(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) return GetNullableDec(root, prop.Name);
            var nested = FindDecimal(prop.Value, name);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static bool IsMissingSchema(PostgresException ex) => ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedObject or PostgresErrorCodes.UndefinedColumn;
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static decimal? GetDecimal(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
    private static string DataLabel(string mode) => mode switch { "kraken" => "Kraken data", "kraken-futures" => "Kraken Futures data", "database" => "Database data", "sample" => "Sample data", _ => "Data unknown" };
    private static string AgeLabel(double seconds) => seconds < 60 ? $"{Math.Round(seconds)}s" : $"{Math.Round(seconds / 60)}m";
    private static string Csv(string s) => $"\"{s.Replace("\"", "\"\"")}\"";
    private static string PrettyJson(string json) { try { return JsonSerializer.Serialize(JsonDocument.Parse(json), new JsonSerializerOptions { WriteIndented = true }); } catch { return json; } }
    private static string GetString(JsonElement e, string name, string fallback = "") => e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : fallback;
    private static decimal GetDec(JsonElement e, string name) => GetNullableDec(e, name) ?? 0m;
    private static decimal? GetNullableDec(JsonElement e, string name) => e.TryGetProperty(name, out var v) && decimal.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;
    private static DateTimeOffset? GetDate(JsonElement e, string name) => e.TryGetProperty(name, out var v) && DateTimeOffset.TryParse(v.ToString(), out var d) ? d : null;
}
