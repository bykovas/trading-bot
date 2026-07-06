using System.Globalization;
using System.Text.Json;
using System.Text;
using Npgsql;
using NpgsqlTypes;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

var app = builder.Build();

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    utc = DateTimeOffset.UtcNow
}));

// Distinct bot instances that have persisted at least one cycle, so the UI
// toggle can be data-driven instead of hardcoding instance ids.
app.MapGet("/api/bot-instances", async (CancellationToken cancellationToken) =>
{
    var connectionString = GetConnectionString(builder.Configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem("TRADINGBOT_DATABASE_CONNECTION_STRING is not configured.");
    }

    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "select distinct bot_instance_id from dry_run_cycles order by bot_instance_id",
            connection);
        var instances = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            instances.Add(reader.GetString(0));
        }

        return Results.Ok(new { utc = DateTimeOffset.UtcNow, instances });
    }
    catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedObject)
    {
        return Results.Ok(new { utc = DateTimeOffset.UtcNow, instances = Array.Empty<string>() });
    }
});

app.MapGet("/api/portfolio", async (string? botInstanceId, CancellationToken cancellationToken) =>
{
    var connectionString = GetConnectionString(builder.Configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem("TRADINGBOT_DATABASE_CONNECTION_STRING is not configured.");
    }

    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var bot = Clean(botInstanceId);
        var summary = await ReadSummary(connection, bot, cancellationToken);
        var positions = await ReadPositions(connection, bot, cancellationToken);

        return Results.Ok(new PortfolioResponse(
            DateTimeOffset.UtcNow,
            summary,
            positions,
            summary is null ? "portfolio_state is empty; wait for the worker to persist a cycle" : null));
    }
    catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedObject)
    {
        return Results.Ok(new PortfolioResponse(
            DateTimeOffset.UtcNow,
            null,
            Array.Empty<PortfolioPositionDto>(),
            "portfolio tables/views are not initialized yet; wait for the worker to start with database enabled"));
    }
});

app.MapGet("/api/positions", async (string? botInstanceId, CancellationToken cancellationToken) =>
{
    var connectionString = GetConnectionString(builder.Configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem("TRADINGBOT_DATABASE_CONNECTION_STRING is not configured.");
    }

    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    return Results.Ok(await ReadPositions(connection, Clean(botInstanceId), cancellationToken));
});

app.MapGet("/api/cycles", async (
    int? limit,
    int? offset,
    string? workerVersion,
    string? workerCommit,
    string? strategyVersion,
    string? changeSet,
    string? botInstanceId,
    bool? latestStrategy,
    CancellationToken cancellationToken) =>
{
    var connectionString = GetConnectionString(builder.Configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem("TRADINGBOT_DATABASE_CONNECTION_STRING is not configured.");
    }

    var page = PageRequest.Create(limit, offset);
    var filters = new CycleQueryFilters(
        Clean(workerVersion),
        Clean(workerCommit),
        Clean(strategyVersion),
        Clean(changeSet),
        Clean(botInstanceId),
        latestStrategy == true);

    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    await EnsureCycleMetadataColumns(connection, cancellationToken);

    var items = await ReadRawCycles(connection, page, filters, cancellationToken);
    return Results.Ok(new PageResponse<CycleRawDto>(
        items,
        page.Limit,
        page.Offset,
        items.Count == page.Limit ? page.Offset + page.Limit : null));
});

app.MapGet("/api/cycles/{cycleId}", async (string cycleId, CancellationToken cancellationToken) =>
{
    var connectionString = GetConnectionString(builder.Configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem("TRADINGBOT_DATABASE_CONNECTION_STRING is not configured.");
    }

    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    await EnsureCycleMetadataColumns(connection, cancellationToken);

    var cycle = await ReadCycleDetail(connection, cycleId, cancellationToken);
    return cycle is null ? Results.NotFound() : Results.Ok(cycle);
});

app.MapGet("/api/trade-cycles", async (int? limit, int? offset, string? botInstanceId, CancellationToken cancellationToken) =>
{
    var connectionString = GetConnectionString(builder.Configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem("TRADINGBOT_DATABASE_CONNECTION_STRING is not configured.");
    }

    var page = PageRequest.Create(limit, offset);
    var window = LocalYesterdayStartUtc();

    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    await EnsureCycleMetadataColumns(connection, cancellationToken);

    var items = await ReadTradeCycles(connection, window.UtcStart, Clean(botInstanceId), page, cancellationToken);
    return Results.Ok(new TradeCyclesResponse(
        items,
        page.Limit,
        page.Offset,
        items.Count == page.Limit ? page.Offset + page.Limit : null,
        window.LocalStartDate,
        window.LocalTimeZone));
});

app.MapGet("/api/decisions", async (string? cycleId, string? botInstanceId, int? limit, int? offset, CancellationToken cancellationToken) =>
{
    var connectionString = GetConnectionString(builder.Configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem("TRADINGBOT_DATABASE_CONNECTION_STRING is not configured.");
    }

    var page = PageRequest.Create(limit, offset);
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    var items = await ReadDecisions(connection, Clean(cycleId), Clean(botInstanceId), page, cancellationToken);
    return Results.Ok(new PageResponse<DecisionSummaryDto>(
        items,
        page.Limit,
        page.Offset,
        items.Count == page.Limit ? page.Offset + page.Limit : null));
});

app.MapGet("/api/entry-diagnostics", async (string? cycleId, string? botInstanceId, int? limit, int? offset, CancellationToken cancellationToken) =>
{
    var connectionString = GetConnectionString(builder.Configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem("TRADINGBOT_DATABASE_CONNECTION_STRING is not configured.");
    }

    var page = PageRequest.Create(limit, offset);
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    var items = await ReadEntryDiagnostics(connection, Clean(cycleId), Clean(botInstanceId), page, cancellationToken);
    return Results.Ok(new PageResponse<CycleEntryDiagnosticsDto>(
        items,
        page.Limit,
        page.Offset,
        items.Count == page.Limit ? page.Offset + page.Limit : null));
});

app.MapGet("/api/market-snapshots", async (
    string? cycleId,
    string? pair,
    string? botInstanceId,
    int? limit,
    int? offset,
    CancellationToken cancellationToken) =>
{
    var connectionString = GetConnectionString(builder.Configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem("TRADINGBOT_DATABASE_CONNECTION_STRING is not configured.");
    }

    var page = PageRequest.Create(limit, offset);
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    var items = await ReadMarketSnapshots(connection, Clean(cycleId), Clean(pair), Clean(botInstanceId), page, cancellationToken);
    return Results.Ok(new PageResponse<MarketSnapshotDto>(
        items,
        page.Limit,
        page.Offset,
        items.Count == page.Limit ? page.Offset + page.Limit : null));
});

app.MapGet("/api/export/cycles-and-snapshots.csv", (IConfiguration configuration, CancellationToken cancellationToken) =>
{
    var connectionString = GetConnectionString(configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem("TRADINGBOT_DATABASE_CONNECTION_STRING is not configured.");
    }

    return Results.Stream(
        stream => WriteCyclesAndSnapshotsCsv(stream, connectionString, cancellationToken),
        "text/csv; charset=utf-8",
        $"trading-bot-cycles-snapshots-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.csv");
});

app.Run();

static string GetConnectionString(IConfiguration configuration) =>
    Environment.GetEnvironmentVariable("TRADINGBOT_DATABASE_CONNECTION_STRING")
    ?? configuration.GetConnectionString("TradingBot")
    ?? string.Empty;

static TradeWindow LocalYesterdayStartUtc()
{
    const string timeZoneId = "Europe/Vilnius";
    var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
    var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
    var localStart = localNow.Date.AddDays(-1);
    var utcStart = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone), TimeSpan.Zero);
    return new TradeWindow(utcStart, localStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), timeZoneId);
}

static async Task<PortfolioSummaryDto?> ReadSummary(NpgsqlConnection connection, string? botInstanceId, CancellationToken cancellationToken)
{
    await using var command = new NpgsqlCommand(
        """
        select
            updated_at,
            (state_json ->> 'cashEur')::numeric as cash_eur,
            (state_json ->> 'positionsValueEur')::numeric as positions_value_eur,
            (state_json ->> 'totalValueEur')::numeric as total_value_eur,
            jsonb_array_length(coalesce(state_json -> 'positions', '[]'::jsonb)) as open_positions,
            state_json -> 'dailyRisk' ->> 'dateUtc' as daily_risk_date_utc,
            (state_json -> 'dailyRisk' ->> 'realizedPnlEur')::numeric as daily_realized_pnl_eur
        from portfolio_state
        where (@bot_instance_id is null or bot_instance_id = @bot_instance_id)
        order by updated_at desc
        limit 1
        """,
        connection);
    command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = (object?)botInstanceId ?? DBNull.Value;

    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return null;
    }

    var todayUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    var dailyRiskDate = reader.IsDBNull(5) ? todayUtc : reader.GetString(5);
    var dailyRealizedPnl = dailyRiskDate == todayUtc && !reader.IsDBNull(6)
        ? reader.GetDecimal(6)
        : 0m;

    return new PortfolioSummaryDto(
        reader.GetDateTime(0),
        reader.GetDecimal(1),
        reader.GetDecimal(2),
        reader.GetDecimal(3),
        reader.GetInt32(4),
        todayUtc,
        dailyRealizedPnl);
}

static async Task<IReadOnlyList<PortfolioPositionDto>> ReadPositions(NpgsqlConnection connection, string? botInstanceId, CancellationToken cancellationToken)
{
    await using var command = new NpgsqlCommand(
        """
        select
            position ->> 'pair' as pair,
            position ->> 'side' as side,
            (position ->> 'quantity')::numeric as quantity,
            (position ->> 'entryPrice')::numeric as entry_price,
            (position ->> 'entryNotionalEur')::numeric as entry_notional_eur,
            (position ->> 'lastPrice')::numeric as last_price,
            (position ->> 'marketValueEur')::numeric as market_value_eur,
            (position ->> 'unrealizedPnlEur')::numeric as unrealized_pnl_eur,
            (position ->> 'unrealizedPnlPercent')::numeric as unrealized_pnl_percent,
            (position ->> 'openedAtUtc')::timestamptz as opened_at_utc,
            (position ->> 'lastActionAtUtc')::timestamptz as last_action_at_utc,
            position ->> 'exitMode' as exit_mode,
            (position ->> 'entryAtr')::numeric as entry_atr,
            (position ->> 'stopLossPrice')::numeric as stop_loss_price,
            (position ->> 'takeProfitPrice')::numeric as take_profit_price,
            (position ->> 'leverage')::numeric as leverage,
            (position ->> 'initialMarginEur')::numeric as initial_margin_eur,
            (position ->> 'markPrice')::numeric as mark_price,
            (position ->> 'liquidationPrice')::numeric as liquidation_price,
            (position ->> 'liquidationDistancePercent')::numeric as liquidation_distance_percent,
            (position ->> 'fundingPaidEur')::numeric as funding_paid_eur,
            position ->> 'tpOrderState' as tp_order_state,
            position ->> 'slOrderState' as sl_order_state
        from portfolio_state state
        cross join lateral jsonb_array_elements(coalesce(state.state_json -> 'positions', '[]'::jsonb)) as position
        where (@bot_instance_id is null or state.bot_instance_id = @bot_instance_id)
        order by market_value_eur desc, pair
        """,
        connection);
    command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = (object?)botInstanceId ?? DBNull.Value;

    var positions = new List<PortfolioPositionDto>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        positions.Add(new PortfolioPositionDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetDecimal(2),
            reader.GetDecimal(3),
            reader.GetDecimal(4),
            reader.GetDecimal(5),
            reader.GetDecimal(6),
            reader.GetDecimal(7),
            reader.GetDecimal(8),
            reader.IsDBNull(9) ? null : reader.GetDateTime(9),
            reader.IsDBNull(10) ? null : reader.GetDateTime(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            GetNullableDecimal(reader, 12),
            GetNullableDecimal(reader, 13),
            GetNullableDecimal(reader, 14),
            GetNullableDecimal(reader, 15),
            GetNullableDecimal(reader, 16),
            GetNullableDecimal(reader, 17),
            GetNullableDecimal(reader, 18),
            GetNullableDecimal(reader, 19),
            GetNullableDecimal(reader, 20),
            reader.IsDBNull(21) ? null : reader.GetString(21),
            reader.IsDBNull(22) ? null : reader.GetString(22)));
    }

    return positions;
}

static async Task<IReadOnlyList<CycleRawDto>> ReadRawCycles(
    NpgsqlConnection connection,
    PageRequest page,
    CycleQueryFilters filters,
    CancellationToken cancellationToken)
{
    await using var command = new NpgsqlCommand(
        """
        select
            cycle_id,
            bot_instance_id,
            utc,
            worker_version,
            worker_commit,
            worker_build_utc,
            worker_image_tag,
            strategy_version,
            change_set,
            record_json::text
        from dry_run_cycles
        where (@worker_version is null or worker_version = @worker_version)
          and (@bot_instance_id is null or bot_instance_id = @bot_instance_id)
          and (@worker_commit is null or worker_commit = @worker_commit)
          and (@strategy_version is null or strategy_version = @strategy_version)
          and (@change_set is null or change_set = @change_set)
          and (
              @latest_strategy = false
              or strategy_version = (
                  select latest.strategy_version
                  from dry_run_cycles latest
                  where latest.strategy_version is not null
                  order by latest.utc desc, latest.cycle_id desc
                  limit 1
              )
          )
        order by utc desc, cycle_id desc
        limit @limit offset @offset
        """,
        connection);
    command.Parameters.AddWithValue("limit", page.Limit);
    command.Parameters.AddWithValue("offset", page.Offset);
    command.Parameters.Add("worker_version", NpgsqlDbType.Text).Value = (object?)filters.WorkerVersion ?? DBNull.Value;
    command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = (object?)filters.BotInstanceId ?? DBNull.Value;
    command.Parameters.Add("worker_commit", NpgsqlDbType.Text).Value = (object?)filters.WorkerCommit ?? DBNull.Value;
    command.Parameters.Add("strategy_version", NpgsqlDbType.Text).Value = (object?)filters.StrategyVersion ?? DBNull.Value;
    command.Parameters.Add("change_set", NpgsqlDbType.Text).Value = (object?)filters.ChangeSet ?? DBNull.Value;
    command.Parameters.Add("latest_strategy", NpgsqlDbType.Boolean).Value = filters.LatestStrategy;

    var cycles = new List<CycleRawDto>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        using var document = JsonDocument.Parse(reader.GetString(9));
        cycles.Add(new CycleRawDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetDateTime(2),
            ReadNullableString(reader, 3),
            ReadNullableString(reader, 4),
            ReadNullableString(reader, 5),
            ReadNullableString(reader, 6),
            ReadNullableString(reader, 7),
            ReadNullableString(reader, 8),
            document.RootElement.Clone()));
    }

    return cycles;
}

static async Task EnsureCycleMetadataColumns(NpgsqlConnection connection, CancellationToken cancellationToken)
{
    await using var command = new NpgsqlCommand(
        """
        alter table dry_run_cycles
            add column if not exists bot_instance_id text not null default 'default',
            add column if not exists worker_version text,
            add column if not exists worker_commit text,
            add column if not exists worker_build_utc text,
            add column if not exists worker_image_tag text,
            add column if not exists strategy_version text,
            add column if not exists change_set text;

        alter table portfolio_state
            add column if not exists bot_instance_id text not null default 'default';

        alter table market_snapshots
            add column if not exists bot_instance_id text not null default 'default';

        create index if not exists ix_dry_run_cycles_bot_instance_utc on dry_run_cycles (bot_instance_id, utc desc);
        create index if not exists ix_market_snapshots_bot_instance_utc on market_snapshots (bot_instance_id, utc desc);
        create index if not exists ix_dry_run_cycles_worker_commit on dry_run_cycles (worker_commit, utc desc);
        create index if not exists ix_dry_run_cycles_strategy_version on dry_run_cycles (strategy_version, utc desc);
        create index if not exists ix_dry_run_cycles_change_set on dry_run_cycles (change_set, utc desc);
        """,
        connection);
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static async Task<CycleDetailDto?> ReadCycleDetail(
    NpgsqlConnection connection,
    string cycleId,
    CancellationToken cancellationToken)
{
    await using var command = new NpgsqlCommand(
        """
        select
            cycle_id,
            utc,
            record_json::text
        from dry_run_cycles
        where cycle_id = @cycle_id
        """,
        connection);
    command.Parameters.AddWithValue("cycle_id", cycleId);

    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return null;
    }

    using var document = JsonDocument.Parse(reader.GetString(2));
    return new CycleDetailDto(
        reader.GetString(0),
        reader.GetDateTime(1),
        document.RootElement.Clone());
}

static async Task<IReadOnlyList<CycleRawDto>> ReadTradeCycles(
    NpgsqlConnection connection,
    DateTimeOffset utcStart,
    string? botInstanceId,
    PageRequest page,
    CancellationToken cancellationToken)
{
    await using var command = new NpgsqlCommand(
        """
        select
            cycle_id,
            bot_instance_id,
            utc,
            worker_version,
            worker_commit,
            worker_build_utc,
            worker_image_tag,
            strategy_version,
            change_set,
            record_json::text
        from dry_run_cycles
        where utc >= @utc_start
          and (@bot_instance_id is null or bot_instance_id = @bot_instance_id)
          and exists (
              select 1
              from dry_run_decisions decision
              where decision.cycle_id = dry_run_cycles.cycle_id
                and decision.action in ('WOULD_BUY', 'WOULD_SELL')
          )
        order by utc desc, cycle_id desc
        limit @limit offset @offset
        """,
        connection);
    command.Parameters.Add("utc_start", NpgsqlDbType.TimestampTz).Value = utcStart;
    command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = (object?)botInstanceId ?? DBNull.Value;
    command.Parameters.AddWithValue("limit", page.Limit);
    command.Parameters.AddWithValue("offset", page.Offset);

    var cycles = new List<CycleRawDto>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        using var document = JsonDocument.Parse(reader.GetString(9));
        cycles.Add(new CycleRawDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetDateTime(2),
            ReadNullableString(reader, 3),
            ReadNullableString(reader, 4),
            ReadNullableString(reader, 5),
            ReadNullableString(reader, 6),
            ReadNullableString(reader, 7),
            ReadNullableString(reader, 8),
            document.RootElement.Clone()));
    }

    return cycles;
}

static async Task<IReadOnlyList<DecisionSummaryDto>> ReadDecisions(
    NpgsqlConnection connection,
    string? cycleId,
    string? botInstanceId,
    PageRequest page,
    CancellationToken cancellationToken)
{
    await using var command = new NpgsqlCommand(
        """
        select
            cycle_id,
            bot_instance_id,
            utc,
            pair,
            action,
            desired_position,
            price,
            score,
            risk_approved,
            broker,
            target_notional_eur,
            quantity,
            fill_price,
            fee_eur,
            cash_before_eur,
            cash_after_eur,
            portfolio_value_before_eur,
            portfolio_value_after_eur,
            reason,
            hold_reason_code,
            exit_reason_code,
            entry_rejection_reason,
            spread_percent,
            price_action_direction,
            price_action_trend_percent,
            exploratory,
            has_bullish_structure,
            ema_fully_confirmed,
            bullish_ema_gap_percent,
            ema_gap_velocity_percent,
            early_entry_eligible,
            early_entry_reason,
            early_entry_diagnostic_score,
            early_entry_suggested_notional_eur,
            side,
            reduce_only,
            leverage,
            exit_trigger_source
        from dry_run_decisions
        where (@cycle_id is null or cycle_id = @cycle_id)
          and (@bot_instance_id is null or bot_instance_id = @bot_instance_id)
        order by utc desc, cycle_id desc, pair
        limit @limit offset @offset
        """,
        connection);
    command.Parameters.Add("cycle_id", NpgsqlDbType.Text).Value = (object?)cycleId ?? DBNull.Value;
    command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = (object?)botInstanceId ?? DBNull.Value;
    command.Parameters.AddWithValue("limit", page.Limit);
    command.Parameters.AddWithValue("offset", page.Offset);

    var decisions = new List<DecisionSummaryDto>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        decisions.Add(new DecisionSummaryDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetDateTime(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetDecimal(6),
            reader.GetDecimal(7),
            reader.GetBoolean(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            GetNullableDecimal(reader, 10),
            GetNullableDecimal(reader, 11),
            GetNullableDecimal(reader, 12),
            GetNullableDecimal(reader, 13),
            GetNullableDecimal(reader, 14),
            GetNullableDecimal(reader, 15),
            GetNullableDecimal(reader, 16),
            GetNullableDecimal(reader, 17),
            reader.IsDBNull(18) ? string.Empty : reader.GetString(18),
            reader.IsDBNull(19) ? null : reader.GetString(19),
            reader.IsDBNull(20) ? null : reader.GetString(20),
            reader.IsDBNull(21) ? null : reader.GetString(21),
            GetNullableDecimal(reader, 22),
            reader.IsDBNull(23) ? null : reader.GetString(23),
            GetNullableDecimal(reader, 24),
            reader.IsDBNull(25) ? null : reader.GetBoolean(25),
            reader.IsDBNull(26) ? null : reader.GetBoolean(26),
            reader.IsDBNull(27) ? null : reader.GetBoolean(27),
            GetNullableDecimal(reader, 28),
            GetNullableDecimal(reader, 29),
            reader.IsDBNull(30) ? null : reader.GetBoolean(30),
            reader.IsDBNull(31) ? null : reader.GetString(31),
            GetNullableDecimal(reader, 32),
            GetNullableDecimal(reader, 33),
            reader.IsDBNull(34) ? null : reader.GetString(34),
            reader.IsDBNull(35) ? null : reader.GetBoolean(35),
            GetNullableDecimal(reader, 36),
            reader.IsDBNull(37) ? null : reader.GetString(37)));
    }

    return decisions;
}

static decimal? GetNullableDecimal(NpgsqlDataReader reader, int ordinal) =>
    reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);

static async Task<IReadOnlyList<CycleEntryDiagnosticsDto>> ReadEntryDiagnostics(
    NpgsqlConnection connection,
    string? cycleId,
    string? botInstanceId,
    PageRequest page,
    CancellationToken cancellationToken)
{
    await using var command = new NpgsqlCommand(
        """
        select
            cycle_id,
            bot_instance_id,
            utc,
            snapshot_pairs_available,
            active_pairs_evaluated,
            entry_pairs_evaluated,
            score_at_least_075,
            score_at_least_080,
            score_at_least_085,
            score_at_least_090,
            hard_filter_pass_count,
            eligible_entry_candidates,
            chosen_pair,
            no_trade_reason,
            rejection_counts::text,
            top_candidates::text,
            excluded_pairs::text,
            price_action_ready_count
        from dry_run_cycle_entry_diagnostics
        where (@cycle_id is null or cycle_id = @cycle_id)
          and (@bot_instance_id is null or bot_instance_id = @bot_instance_id)
        order by utc desc, cycle_id desc
        limit @limit offset @offset
        """,
        connection);
    command.Parameters.Add("cycle_id", NpgsqlDbType.Text).Value = (object?)cycleId ?? DBNull.Value;
    command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = (object?)botInstanceId ?? DBNull.Value;
    command.Parameters.AddWithValue("limit", page.Limit);
    command.Parameters.AddWithValue("offset", page.Offset);

    var items = new List<CycleEntryDiagnosticsDto>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        items.Add(new CycleEntryDiagnosticsDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetDateTime(2),
            GetNullableInt(reader, 3),
            GetNullableInt(reader, 4),
            GetNullableInt(reader, 5),
            GetNullableInt(reader, 6),
            GetNullableInt(reader, 7),
            GetNullableInt(reader, 8),
            GetNullableInt(reader, 9),
            GetNullableInt(reader, 10),
            GetNullableInt(reader, 11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            ParseJsonOrNull(reader, 14),
            ParseJsonOrNull(reader, 15),
            ParseJsonOrNull(reader, 16),
            GetNullableInt(reader, 17)));
    }

    return items;
}

static int? GetNullableInt(NpgsqlDataReader reader, int ordinal) =>
    reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

static JsonElement? ParseJsonOrNull(NpgsqlDataReader reader, int ordinal) =>
    reader.IsDBNull(ordinal) ? null : JsonDocument.Parse(reader.GetString(ordinal)).RootElement.Clone();

static async Task<IReadOnlyList<MarketSnapshotDto>> ReadMarketSnapshots(
    NpgsqlConnection connection,
    string? cycleId,
    string? pair,
    string? botInstanceId,
    PageRequest page,
    CancellationToken cancellationToken)
{
    await using var command = new NpgsqlCommand(
        """
        select
            cycle_id,
            bot_instance_id,
            utc,
            pair,
            bid,
            ask,
            last,
            volume24h,
            change_percent
        from market_snapshots
        where (@cycle_id is null or cycle_id = @cycle_id)
          and (@pair is null or pair = @pair)
          and (@bot_instance_id is null or bot_instance_id = @bot_instance_id)
        order by utc desc, cycle_id desc, pair
        limit @limit offset @offset
        """,
        connection);
    command.Parameters.Add("cycle_id", NpgsqlDbType.Text).Value = (object?)cycleId ?? DBNull.Value;
    command.Parameters.Add("pair", NpgsqlDbType.Text).Value = (object?)NormalizePairFilter(pair) ?? DBNull.Value;
    command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = (object?)botInstanceId ?? DBNull.Value;
    command.Parameters.AddWithValue("limit", page.Limit);
    command.Parameters.AddWithValue("offset", page.Offset);

    var snapshots = new List<MarketSnapshotDto>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        snapshots.Add(new MarketSnapshotDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetDateTime(2),
            reader.GetString(3),
            reader.GetDecimal(4),
            reader.GetDecimal(5),
            reader.GetDecimal(6),
            reader.GetDecimal(7),
            reader.GetDecimal(8)));
    }

    return snapshots;
}

static string? NormalizePairFilter(string? pair) =>
    string.IsNullOrWhiteSpace(pair) ? null : pair.Trim().ToUpperInvariant();

static async Task WriteCyclesAndSnapshotsCsv(
    Stream stream,
    string connectionString,
    CancellationToken cancellationToken)
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    await EnsureCycleMetadataColumns(connection, cancellationToken);

    await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
    await writer.WriteLineAsync("record_type,cycle_id,utc,worker_version,worker_commit,worker_build_utc,worker_image_tag,strategy_version,change_set,pair,bid,ask,last,volume24h,change_percent,record_json");

    await using (var command = new NpgsqlCommand(
        """
        select
            cycle_id,
            utc,
            worker_version,
            worker_commit,
            worker_build_utc,
            worker_image_tag,
            strategy_version,
            change_set,
            record_json::text
        from dry_run_cycles
        order by utc asc, cycle_id asc
        """,
        connection))
    await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
    {
        while (await reader.ReadAsync(cancellationToken))
        {
            await writer.WriteLineAsync(string.Join(',', new[]
            {
                CsvText("cycle"),
                CsvText(reader.GetString(0)),
                CsvText(FormatUtc(reader.GetDateTime(1))),
                CsvText(ReadNullableString(reader, 2)),
                CsvText(ReadNullableString(reader, 3)),
                CsvText(ReadNullableString(reader, 4)),
                CsvText(ReadNullableString(reader, 5)),
                CsvText(ReadNullableString(reader, 6)),
                CsvText(ReadNullableString(reader, 7)),
                "",
                "",
                "",
                "",
                "",
                "",
                CsvText(reader.GetString(8))
            }));
        }
    }

    await using (var command = new NpgsqlCommand(
        """
        select cycle_id, utc, pair, bid, ask, last, volume24h, change_percent
        from market_snapshots
        order by utc asc, cycle_id asc, pair asc
        """,
        connection))
    await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
    {
        while (await reader.ReadAsync(cancellationToken))
        {
            await writer.WriteLineAsync(string.Join(',', new[]
            {
                CsvText("market_snapshot"),
                CsvText(reader.GetString(0)),
                CsvText(FormatUtc(reader.GetDateTime(1))),
                "",
                "",
                "",
                "",
                "",
                "",
                CsvText(reader.GetString(2)),
                CsvDecimal(reader.GetDecimal(3)),
                CsvDecimal(reader.GetDecimal(4)),
                CsvDecimal(reader.GetDecimal(5)),
                CsvDecimal(reader.GetDecimal(6)),
                CsvDecimal(reader.GetDecimal(7)),
                ""
            }));
        }
    }
}

static string FormatUtc(DateTime value) =>
    DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

static string? Clean(string? value) =>
    string.IsNullOrWhiteSpace(value) ? null : value.Trim();

static string? ReadNullableString(NpgsqlDataReader reader, int ordinal) =>
    reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

static string CsvDecimal(decimal value) => CsvText(value.ToString(CultureInfo.InvariantCulture));

static string CsvText(string? value)
{
    value ??= string.Empty;
    return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

internal sealed record PortfolioResponse(
    DateTimeOffset Utc,
    PortfolioSummaryDto? Summary,
    IReadOnlyList<PortfolioPositionDto> Positions,
    string? Warning);

internal sealed record PortfolioSummaryDto(
    DateTime UpdatedAt,
    decimal CashEur,
    decimal PositionsValueEur,
    decimal TotalValueEur,
    int OpenPositions,
    string? DailyRiskDateUtc,
    decimal? DailyRealizedPnlEur);

internal sealed record PortfolioPositionDto(
    string Pair,
    string Side,
    decimal Quantity,
    decimal EntryPrice,
    decimal EntryNotionalEur,
    decimal LastPrice,
    decimal MarketValueEur,
    decimal UnrealizedPnlEur,
    decimal UnrealizedPnlPercent,
    DateTime? OpenedAtUtc,
    DateTime? LastActionAtUtc,
    string? ExitMode,
    decimal? EntryAtr,
    decimal? StopLossPrice,
    decimal? TakeProfitPrice,
    decimal? Leverage,
    decimal? InitialMarginEur,
    decimal? MarkPrice,
    decimal? LiquidationPrice,
    decimal? LiquidationDistancePercent,
    decimal? FundingPaidEur,
    string? TpOrderState,
    string? SlOrderState);

internal sealed record PageRequest(int Limit, int Offset)
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public static PageRequest Create(int? limit, int? offset) =>
        new(
            Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit),
            Math.Max(offset ?? 0, 0));
}

internal sealed record PageResponse<T>(
    IReadOnlyList<T> Items,
    int Limit,
    int Offset,
    int? NextOffset);

internal sealed record TradeWindow(
    DateTimeOffset UtcStart,
    string LocalStartDate,
    string LocalTimeZone);

internal sealed record TradeCyclesResponse(
    IReadOnlyList<CycleRawDto> Items,
    int Limit,
    int Offset,
    int? NextOffset,
    string SinceLocalDate,
    string TimeZone);

internal sealed record CycleRawDto(
    string CycleId,
    string BotInstanceId,
    DateTime Utc,
    string? WorkerVersion,
    string? WorkerCommit,
    string? WorkerBuildUtc,
    string? WorkerImageTag,
    string? StrategyVersion,
    string? ChangeSet,
    JsonElement Record);

internal sealed record CycleQueryFilters(
    string? WorkerVersion,
    string? WorkerCommit,
    string? StrategyVersion,
    string? ChangeSet,
    string? BotInstanceId,
    bool LatestStrategy);

internal sealed record CycleDetailDto(
    string CycleId,
    DateTime Utc,
    JsonElement Record);

internal sealed record DecisionSummaryDto(
    string CycleId,
    string BotInstanceId,
    DateTime Utc,
    string Pair,
    string Action,
    string DesiredPosition,
    decimal Price,
    decimal Score,
    bool RiskApproved,
    string? Broker,
    decimal? TargetNotionalEur,
    decimal? Quantity,
    decimal? FillPrice,
    decimal? FeeEur,
    decimal? CashBeforeEur,
    decimal? CashAfterEur,
    decimal? PortfolioValueBeforeEur,
    decimal? PortfolioValueAfterEur,
    string Reason,
    string? HoldReasonCode,
    string? ExitReasonCode,
    string? EntryRejectionReason,
    decimal? SpreadPercent,
    string? PriceActionDirection,
    decimal? PriceActionTrendPercent,
    bool? Exploratory,
    bool? HasBullishStructure,
    bool? EmaFullyConfirmed,
    decimal? BullishEmaGapPercent,
    decimal? EmaGapVelocityPercent,
    bool? EarlyEntryEligible,
    string? EarlyEntryReason,
    decimal? EarlyEntryDiagnosticScore,
    decimal? EarlyEntrySuggestedNotionalEur,
    string? Side,
    bool? ReduceOnly,
    decimal? Leverage,
    string? ExitTriggerSource);

internal sealed record CycleEntryDiagnosticsDto(
    string CycleId,
    string BotInstanceId,
    DateTime Utc,
    int? SnapshotPairsAvailable,
    int? ActivePairsEvaluated,
    int? EntryPairsEvaluated,
    int? ScoreAtLeast075,
    int? ScoreAtLeast080,
    int? ScoreAtLeast085,
    int? ScoreAtLeast090,
    int? HardFilterPassCount,
    int? EligibleEntryCandidates,
    string? ChosenPair,
    string? NoTradeReason,
    JsonElement? RejectionCounts,
    JsonElement? TopCandidates,
    JsonElement? ExcludedPairs,
    int? PriceActionReadyCount);

internal sealed record MarketSnapshotDto(
    string CycleId,
    string BotInstanceId,
    DateTime Utc,
    string Pair,
    decimal Bid,
    decimal Ask,
    decimal Last,
    decimal Volume24h,
    decimal ChangePercent);
