using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TradingBot.Core.Common;
using TradingBot.Core.Risk;

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
            "select distinct bot_instance_id from dry_run_cycle_facts order by bot_instance_id",
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

app.MapGet("/api/bot-status", async (string? botInstanceId, CancellationToken cancellationToken) =>
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
        await EnsureCycleMetadataColumns(connection, cancellationToken);

        var status = await ReadBotStatus(connection, Clean(botInstanceId), cancellationToken);
        return Results.Ok(status);
    }
    catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedObject)
    {
        var now = DateTimeOffset.UtcNow;
        var bot = Clean(botInstanceId);
        return Results.Ok(BotStatusDto.NoData(now, bot, BotEntryBlackout(bot, now)));
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

// Landing dashboard (public/index.html) reads this single endpoint every 10s.
// It bundles what the page needs — summary, positions with their entry context,
// worker health for every instance, the 30 closed-day equity series and today's
// executed trades — so the page never has to fan out across /api/portfolio,
// /api/bot-status and the megabyte-sized /api/trade-cycles payload.
app.MapGet("/api/dashboard", async (string? botInstanceId, CancellationToken cancellationToken) =>
{
    var connectionString = GetConnectionString(builder.Configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem("TRADINGBOT_DATABASE_CONNECTION_STRING is not configured.");
    }

    var bot = Clean(botInstanceId);

    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var summary = await ReadSummary(connection, bot, cancellationToken);
        var positions = await ReadPositions(connection, bot, cancellationToken);
        var entries = await ReadEntryContexts(connection, bot, positions, cancellationToken);
        var workers = await ReadWorkers(connection, cancellationToken);
        var equity = await ReadEquityDays(connection, bot, DashboardDefaults.EquityWindowDays, cancellationToken);
        var today = await ReadTodayTrades(connection, bot, cancellationToken);
        var rates = await CoinRates.ReadAsync(connectionString, connection, cancellationToken);

        return Results.Ok(new DashboardResponse(
            DateTimeOffset.UtcNow,
            bot,
            summary,
            positions,
            entries,
            workers,
            equity,
            today,
            rates,
            summary is null ? "portfolio_state is empty; wait for the worker to persist a cycle" : null));
    }
    catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedObject)
    {
        return Results.Ok(new DashboardResponse(
            DateTimeOffset.UtcNow,
            bot,
            null,
            Array.Empty<PortfolioPositionDto>(),
            new Dictionary<string, DashboardEntryDto>(),
            Array.Empty<DashboardWorkerDto>(),
            DashboardEquityDto.Empty(),
            DashboardTodayDto.Empty(),
            null,
            "portfolio tables/views are not initialized yet; wait for the worker to start with database enabled"));
    }
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
    bool? latestMeta,
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
        latestStrategy == true,
        latestMeta == true);

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

app.MapGet("/api/trade-cycles", async (int? limit, int? offset, string? botInstanceId, bool? latestMeta, CancellationToken cancellationToken) =>
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

    var items = await ReadTradeCycles(connection, window.UtcStart, Clean(botInstanceId), latestMeta == true, page, cancellationToken);
    return Results.Ok(new TradeCyclesResponse(
        items,
        page.Limit,
        page.Offset,
        items.Count == page.Limit ? page.Offset + page.Limit : null,
        window.LocalStartDate,
        window.LocalTimeZone));
});

app.MapGet("/api/decisions", async (string? cycleId, string? botInstanceId, bool? latestMeta, int? limit, int? offset, CancellationToken cancellationToken) =>
{
    var connectionString = GetConnectionString(builder.Configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem("TRADINGBOT_DATABASE_CONNECTION_STRING is not configured.");
    }

    var page = PageRequest.Create(limit, offset);
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    await EnsureCycleMetadataColumns(connection, cancellationToken);

    var items = await ReadDecisions(connection, Clean(cycleId), Clean(botInstanceId), latestMeta == true, page, cancellationToken);
    return Results.Ok(new PageResponse<DecisionSummaryDto>(
        items,
        page.Limit,
        page.Offset,
        items.Count == page.Limit ? page.Offset + page.Limit : null));
});

app.MapGet("/api/entry-diagnostics", async (string? cycleId, string? botInstanceId, bool? latestMeta, int? limit, int? offset, CancellationToken cancellationToken) =>
{
    var connectionString = GetConnectionString(builder.Configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem("TRADINGBOT_DATABASE_CONNECTION_STRING is not configured.");
    }

    var page = PageRequest.Create(limit, offset);
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    await EnsureCycleMetadataColumns(connection, cancellationToken);

    var items = await ReadEntryDiagnostics(connection, Clean(cycleId), Clean(botInstanceId), latestMeta == true, page, cancellationToken);
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
    bool? latestMeta,
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
    await EnsureCycleMetadataColumns(connection, cancellationToken);

    var items = await ReadMarketSnapshots(connection, Clean(cycleId), Clean(pair), Clean(botInstanceId), latestMeta == true, page, cancellationToken);
    return Results.Ok(new PageResponse<MarketSnapshotDto>(
        items,
        page.Limit,
        page.Offset,
        items.Count == page.Limit ? page.Offset + page.Limit : null));
});

app.MapGet("/api/simulate", async (
    string? botInstanceId,
    int? lastHours,
    double? spread,
    double? score,
    double? sl,
    double? tp,
    int? hourly,
    int? group,
    bool? btcFilter,
    double? notional,
    double? fee,
    string? exclude,
    bool? trades,
    CancellationToken cancellationToken) =>
{
    var connectionString = GetConnectionString(builder.Configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem("TRADINGBOT_DATABASE_CONNECTION_STRING is not configured.");
    }

    var sim = new SimulationParams(
        Clean(botInstanceId) ?? "spot-live",
        Math.Clamp(lastHours ?? 24, 1, 720),
        spread ?? 0.30,
        score ?? 0.9,
        sl ?? 2.5,
        tp ?? 6.0,
        hourly ?? 2,
        group ?? 1,
        btcFilter ?? false,
        notional ?? 10.0,
        fee ?? 0.35,
        (exclude ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToUpperInvariant()).ToHashSet(),
        trades ?? false);

    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureCycleMetadataColumns(connection, cancellationToken);

        var result = await RunSimulation(connection, sim, cancellationToken);
        return Results.Ok(result);
    }
    catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedObject)
    {
        return Results.Ok(new SimulationResult(
            sim, DateTimeOffset.UtcNow, 0, null, null,
            0, 0, 0, 0, 0, 0, 0,
            Array.Empty<SimTradeSummary>(),
            Array.Empty<SimPairPnl>(),
            Array.Empty<SimRegimePnl>(),
            "No cycle data available yet."));
    }
});

app.MapGet("/api/export/cycles-and-snapshots.csv", () =>
{
    return Results.Problem(
        "CSV export is disabled while reports use normalized database tables.",
        statusCode: StatusCodes.Status410Gone);
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
    await EnsurePortfolioSummaryDisplayColumns(connection, cancellationToken);

    // Positions value / total value are marked to LAST price (exchange parity) rather
    // than the worker's conservative liquidation value (bid - slippage - fee), so the
    // dashboard reconciles with what Kraken shows. Spot positions are valued at
    // quantity * lastPrice; futures positions (leverage set) keep their stored equity
    // value (initial margin + unrealized PnL). The worker's internal risk/exit logic is
    // unaffected — it recomputes conservative valuations independently.
    await using var command = new NpgsqlCommand(
        """
        select
            updated_at,
            round(cash_eur, 8) as cash_eur,
            round(cash_quote_value, 8) as cash_quote_value,
            cash_quote_currency,
            round(coalesce((
                select sum(
                    case
                        when position.leverage is null
                            then position.quantity * position.last_price
                        else position.market_value_eur
                    end)
                from portfolio_position_state position
                where position.bot_instance_id = summary.bot_instance_id
            ), 0), 8) as positions_value_eur,
            round(cash_eur + coalesce((
                select sum(
                    case
                        when position.leverage is null
                            then position.quantity * position.last_price
                        else position.market_value_eur
                    end)
                from portfolio_position_state position
                where position.bot_instance_id = summary.bot_instance_id
            ), 0), 8) as total_value_eur,
            open_positions,
            daily_risk_date_utc,
            round(daily_realized_pnl_eur, 8) as daily_realized_pnl_eur,
            round(external_pnl_eur, 8) as external_pnl_eur,
            round(positions_value_eur, 8) as net_positions_value_eur,
            round(total_value_eur, 8) as net_total_value_eur
        from portfolio_state_summary summary
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
    var dailyRiskDate = reader.IsDBNull(7) ? todayUtc : reader.GetString(7);
    var dailyRealizedPnl = dailyRiskDate == todayUtc && !reader.IsDBNull(8)
        ? reader.GetDecimal(8)
        : 0m;

    return new PortfolioSummaryDto(
        reader.GetDateTime(0),
        reader.GetDecimal(1),
        reader.IsDBNull(2) ? null : reader.GetDecimal(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetDecimal(4),
        reader.GetDecimal(5),
        reader.GetInt32(6),
        todayUtc,
        dailyRealizedPnl,
        reader.IsDBNull(9) ? 0m : reader.GetDecimal(9),
        reader.IsDBNull(10) ? 0m : reader.GetDecimal(10),
        reader.IsDBNull(11) ? 0m : reader.GetDecimal(11));
}

static async Task EnsurePortfolioSummaryDisplayColumns(NpgsqlConnection connection, CancellationToken cancellationToken)
{
    await using var command = new NpgsqlCommand(
        """
        alter table portfolio_state_summary
            add column if not exists cash_quote_value numeric,
            add column if not exists cash_quote_currency text
        """,
        connection);
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static async Task<IReadOnlyList<PortfolioPositionDto>> ReadPositions(NpgsqlConnection connection, string? botInstanceId, CancellationToken cancellationToken)
{
    await using var command = new NpgsqlCommand(
        """
        select
            pair,
            side,
            quantity,
            entry_price,
            entry_notional_eur,
            last_price,
            market_value_eur,
            unrealized_pnl_eur,
            unrealized_pnl_percent,
            opened_at_utc,
            last_action_at_utc,
            exit_mode,
            entry_atr,
            stop_loss_price,
            take_profit_price,
            leverage,
            initial_margin_eur,
            mark_price,
            liquidation_price,
            liquidation_distance_percent,
            funding_paid_eur,
            tp_order_state,
            sl_order_state,
            trailing_stop_state,
            trailing_stop_percent
        from portfolio_position_state position
        where (@bot_instance_id is null or position.bot_instance_id = @bot_instance_id)
        order by market_value_eur desc, pair
        """,
        connection);
    command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = (object?)botInstanceId ?? DBNull.Value;

    var positions = new List<PortfolioPositionDto>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        var quantity = reader.GetDecimal(2);
        var entryPrice = reader.GetDecimal(3);
        var lastPrice = reader.GetDecimal(5);
        var storedMarketValue = reader.GetDecimal(6);
        var storedPnlEur = reader.GetDecimal(7);
        var storedPnlPercent = reader.GetDecimal(8);

        // Spot positions (no leverage) are marked to LAST price with a fee-free cost
        // basis for exchange parity, matching what Kraken displays. Futures positions
        // keep their stored equity valuation.
        var isSpot = reader.IsDBNull(15) && reader.IsDBNull(16);
        var marketValue = isSpot ? quantity * lastPrice : storedMarketValue;
        var pnlEur = isSpot ? (lastPrice - entryPrice) * quantity : storedPnlEur;
        var pnlPercent = isSpot
            ? (entryPrice > 0m ? (lastPrice - entryPrice) / entryPrice * 100m : 0m)
            : storedPnlPercent;

        positions.Add(new PortfolioPositionDto(
            reader.GetString(0),
            reader.GetString(1),
            quantity,
            entryPrice,
            reader.GetDecimal(4),
            lastPrice,
            marketValue,
            pnlEur,
            pnlPercent,
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
            reader.IsDBNull(22) ? null : reader.GetString(22),
            reader.IsDBNull(23) ? null : reader.GetString(23),
            GetNullableDecimal(reader, 24),
            // Stored values are the worker's net-of-fees liquidation figures (spot).
            storedPnlEur,
            storedPnlPercent));
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
            bot_instance_name,
            market_data_mode,
            ai_provider,
            active_pairs_count,
            cash_before_eur,
            positions_value_before_eur,
            portfolio_value_before_eur,
            cash_after_eur,
            positions_value_after_eur,
            portfolio_value_after_eur
        from dry_run_cycle_facts cycle
        where (@worker_version is null or worker_version = @worker_version)
          and (@bot_instance_id is null or bot_instance_id = @bot_instance_id)
          and (@worker_commit is null or worker_commit = @worker_commit)
          and (@strategy_version is null or strategy_version = @strategy_version)
          and (@change_set is null or change_set = @change_set)
          and (
              @latest_strategy = false
              or strategy_version = (
                  select latest.strategy_version
                  from dry_run_cycle_facts latest
                  where latest.strategy_version is not null
                  order by latest.utc desc, latest.cycle_id desc
                  limit 1
              )
          )
          and (
              @latest_meta = false
              or (
                  strategy_version is not distinct from (
                      select latest.strategy_version
                      from dry_run_cycle_facts latest
                      where latest.bot_instance_id = cycle.bot_instance_id
                      order by latest.utc desc, latest.cycle_id desc
                      limit 1
                  )
                  and change_set is not distinct from (
                      select latest.change_set
                      from dry_run_cycle_facts latest
                      where latest.bot_instance_id = cycle.bot_instance_id
                      order by latest.utc desc, latest.cycle_id desc
                      limit 1
                  )
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
    command.Parameters.Add("latest_meta", NpgsqlDbType.Boolean).Value = filters.LatestMeta;

    var cycles = new List<CycleRawDto>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
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
            new CycleRecordDto(
                reader.GetString(0),
                reader.GetString(1),
                ReadNullableString(reader, 9) ?? reader.GetString(1),
                reader.GetDateTime(2),
                ReadNullableString(reader, 10) ?? string.Empty,
                ReadNullableString(reader, 11) ?? string.Empty,
                new CycleWorkerDto(
                    ReadNullableString(reader, 3),
                    ReadNullableString(reader, 4),
                    ReadNullableString(reader, 5),
                    ReadNullableString(reader, 6),
                    ReadNullableString(reader, 7),
                    ReadNullableString(reader, 8)),
                reader.GetInt32(12),
                new CyclePortfolioSnapshotDto(reader.GetDecimal(13), reader.GetDecimal(14), reader.GetDecimal(15)),
                new CyclePortfolioSnapshotDto(reader.GetDecimal(16), reader.GetDecimal(17), reader.GetDecimal(18)),
                Array.Empty<string>(),
                Array.Empty<CycleDecisionRecordDto>())));
    }

    await reader.DisposeAsync();
    await HydrateCycleRecords(connection, cycles, cancellationToken);
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
        create index if not exists ix_dry_run_cycles_bot_instance_utc_cycle on dry_run_cycles (bot_instance_id, utc desc, cycle_id desc);
        create index if not exists ix_market_snapshots_bot_instance_utc on market_snapshots (bot_instance_id, utc desc);
        create index if not exists ix_market_snapshots_bot_pair_utc on market_snapshots (bot_instance_id, pair, utc desc, cycle_id desc);
        create index if not exists ix_market_snapshots_cycle_pair on market_snapshots (cycle_id, pair);
        create index if not exists ix_dry_run_cycles_worker_commit on dry_run_cycles (worker_commit, utc desc);
        create index if not exists ix_dry_run_cycles_strategy_version on dry_run_cycles (strategy_version, utc desc);
        create index if not exists ix_dry_run_cycles_change_set on dry_run_cycles (change_set, utc desc);
        create index if not exists ix_dry_run_cycle_facts_bot_utc on dry_run_cycle_facts (bot_instance_id, utc desc, cycle_id desc);
        create index if not exists ix_dry_run_cycle_facts_strategy_utc on dry_run_cycle_facts (strategy_version, utc desc, cycle_id desc);
        create index if not exists ix_dry_run_cycle_facts_bot_meta_utc on dry_run_cycle_facts (bot_instance_id, strategy_version, change_set, utc desc, cycle_id desc);
        create index if not exists ix_dry_run_cycle_active_pairs_cycle_pair on dry_run_cycle_active_pairs (cycle_id, pair_index);
        create index if not exists ix_dry_run_decision_facts_bot_utc on dry_run_decision_facts (bot_instance_id, utc desc);
        create index if not exists ix_dry_run_decision_facts_pair on dry_run_decision_facts (bot_instance_id, pair, utc desc);
        create index if not exists ix_dry_run_decision_facts_cycle_pair on dry_run_decision_facts (cycle_id, pair);
        create index if not exists ix_dry_run_decision_facts_bot_cycle on dry_run_decision_facts (bot_instance_id, cycle_id);
        create index if not exists ix_dry_run_actions_action_cycle on dry_run_actions (action, cycle_id);
        create index if not exists ix_dry_run_excluded_pairs_cycle_pair on dry_run_excluded_pairs (cycle_id, pair);

        drop view if exists dry_run_cycle_records;
        alter table portfolio_state drop column if exists state_json;
        alter table dry_run_cycles drop column if exists record_json;
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
            bot_instance_id,
            bot_instance_name,
            market_data_mode,
            ai_provider,
            worker_version,
            worker_commit,
            worker_build_utc,
            worker_image_tag,
            strategy_version,
            change_set,
            active_pairs_count,
            cash_before_eur,
            positions_value_before_eur,
            portfolio_value_before_eur,
            cash_after_eur,
            positions_value_after_eur,
            portfolio_value_after_eur
        from dry_run_cycle_facts
        where cycle_id = @cycle_id
        """,
        connection);
    command.Parameters.AddWithValue("cycle_id", cycleId);

    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return null;
    }

    var detailCycleId = reader.GetString(0);
    var detailUtc = reader.GetDateTime(1);
    var cycle = new CycleRawDto(
        reader.GetString(0),
        reader.GetString(2),
        reader.GetDateTime(1),
        ReadNullableString(reader, 6),
        ReadNullableString(reader, 7),
        ReadNullableString(reader, 8),
        ReadNullableString(reader, 9),
        ReadNullableString(reader, 10),
        ReadNullableString(reader, 11),
        new CycleRecordDto(
        reader.GetString(0),
        reader.GetString(2),
        ReadNullableString(reader, 3) ?? reader.GetString(2),
        reader.GetDateTime(1),
        ReadNullableString(reader, 4) ?? string.Empty,
        ReadNullableString(reader, 5) ?? string.Empty,
        new CycleWorkerDto(
            ReadNullableString(reader, 6),
            ReadNullableString(reader, 7),
            ReadNullableString(reader, 8),
            ReadNullableString(reader, 9),
            ReadNullableString(reader, 10),
            ReadNullableString(reader, 11)),
        reader.GetInt32(12),
        new CyclePortfolioSnapshotDto(reader.GetDecimal(13), reader.GetDecimal(14), reader.GetDecimal(15)),
        new CyclePortfolioSnapshotDto(reader.GetDecimal(16), reader.GetDecimal(17), reader.GetDecimal(18)),
        Array.Empty<string>(),
        Array.Empty<CycleDecisionRecordDto>()));
    await reader.DisposeAsync();
    var hydrated = new List<CycleRawDto> { cycle };
    await HydrateCycleRecords(connection, hydrated, cancellationToken);
    return new CycleDetailDto(
        detailCycleId,
        detailUtc,
        hydrated[0].Record);
}

static async Task<IReadOnlyList<CycleRawDto>> ReadTradeCycles(
    NpgsqlConnection connection,
    DateTimeOffset utcStart,
    string? botInstanceId,
    bool latestMeta,
    PageRequest page,
    CancellationToken cancellationToken)
{
    await using var command = new NpgsqlCommand(
        """
        with trade_cycles as (
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
                bot_instance_name,
                market_data_mode,
                ai_provider,
                active_pairs_count,
                cash_before_eur,
                positions_value_before_eur,
                portfolio_value_before_eur,
                cash_after_eur,
                positions_value_after_eur,
                portfolio_value_after_eur
            from dry_run_cycle_facts cycle
            where utc >= @utc_start
              and (@bot_instance_id is null or bot_instance_id = @bot_instance_id)
              and exists (
                  select 1
                  from dry_run_decisions decision
                  where decision.cycle_id = cycle.cycle_id
                    and decision.action in ('WOULD_BUY', 'WOULD_SELL', 'WOULD_OPEN_LONG', 'WOULD_OPEN_SHORT', 'WOULD_CLOSE')
              )
        ),
        latest_trade_meta as (
            select strategy_version, change_set
            from dry_run_cycle_facts
            where (@bot_instance_id is null or bot_instance_id = @bot_instance_id)
            order by utc desc, cycle_id desc
            limit 1
        )
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
            bot_instance_name,
            market_data_mode,
            ai_provider,
            active_pairs_count,
            cash_before_eur,
            positions_value_before_eur,
            portfolio_value_before_eur,
            cash_after_eur,
            positions_value_after_eur,
            portfolio_value_after_eur
        from trade_cycles
        where (
              @latest_meta = false
              or exists (
                  select 1
                  from latest_trade_meta latest
                  where trade_cycles.strategy_version is not distinct from latest.strategy_version
                    and trade_cycles.change_set is not distinct from latest.change_set
              )
          )
        order by utc desc, cycle_id desc
        limit @limit offset @offset
        """,
        connection);
    command.Parameters.Add("utc_start", NpgsqlDbType.TimestampTz).Value = utcStart;
    command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = (object?)botInstanceId ?? DBNull.Value;
    command.Parameters.Add("latest_meta", NpgsqlDbType.Boolean).Value = latestMeta;
    command.Parameters.AddWithValue("limit", page.Limit);
    command.Parameters.AddWithValue("offset", page.Offset);

    var cycles = new List<CycleRawDto>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
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
            new CycleRecordDto(
                reader.GetString(0),
                reader.GetString(1),
                ReadNullableString(reader, 9) ?? reader.GetString(1),
                reader.GetDateTime(2),
                ReadNullableString(reader, 10) ?? string.Empty,
                ReadNullableString(reader, 11) ?? string.Empty,
                new CycleWorkerDto(
                    ReadNullableString(reader, 3),
                    ReadNullableString(reader, 4),
                    ReadNullableString(reader, 5),
                    ReadNullableString(reader, 6),
                    ReadNullableString(reader, 7),
                    ReadNullableString(reader, 8)),
                reader.GetInt32(12),
                new CyclePortfolioSnapshotDto(reader.GetDecimal(13), reader.GetDecimal(14), reader.GetDecimal(15)),
                new CyclePortfolioSnapshotDto(reader.GetDecimal(16), reader.GetDecimal(17), reader.GetDecimal(18)),
                Array.Empty<string>(),
                Array.Empty<CycleDecisionRecordDto>())));
    }

    await reader.DisposeAsync();
    await HydrateCycleRecords(connection, cycles, cancellationToken);
    return cycles;
}

static async Task HydrateCycleRecords(
    NpgsqlConnection connection,
    IList<CycleRawDto> cycles,
    CancellationToken cancellationToken)
{
    if (cycles.Count == 0)
    {
        return;
    }

    var cycleIds = cycles.Select(cycle => cycle.CycleId).Distinct(StringComparer.Ordinal).ToArray();
    var activePairs = cycleIds.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
    await using (var command = new NpgsqlCommand(
        """
        select cycle_id, pair
        from dry_run_cycle_active_pairs
        where cycle_id = any(@cycle_ids)
        order by cycle_id, pair_index
        """,
        connection))
    {
        command.Parameters.Add("cycle_ids", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = cycleIds;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (activePairs.TryGetValue(reader.GetString(0), out var pairs))
            {
                pairs.Add(reader.GetString(1));
            }
        }
    }

    var riskReasons = new Dictionary<(string CycleId, int DecisionIndex), List<string>>();
    await using (var command = new NpgsqlCommand(
        """
        select cycle_id, decision_index, reason
        from dry_run_decision_risk_reasons
        where cycle_id = any(@cycle_ids)
        order by cycle_id, decision_index, reason_index
        """,
        connection))
    {
        command.Parameters.Add("cycle_ids", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = cycleIds;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = (reader.GetString(0), reader.GetInt32(1));
            if (!riskReasons.TryGetValue(key, out var reasons))
            {
                reasons = new List<string>();
                riskReasons[key] = reasons;
            }

            reasons.Add(reader.GetString(2));
        }
    }

    var contributions = new Dictionary<(string CycleId, int DecisionIndex), List<SignalContributionDto>>();
    await using (var command = new NpgsqlCommand(
        """
        select cycle_id, decision_index, name, value, reason
        from dry_run_signal_contributions
        where cycle_id = any(@cycle_ids)
        order by cycle_id, decision_index, contribution_index
        """,
        connection))
    {
        command.Parameters.Add("cycle_ids", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = cycleIds;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = (reader.GetString(0), reader.GetInt32(1));
            if (!contributions.TryGetValue(key, out var items))
            {
                items = new List<SignalContributionDto>();
                contributions[key] = items;
            }

            items.Add(new SignalContributionDto(reader.GetString(2), reader.GetDecimal(3), reader.GetString(4)));
        }
    }

    var decisions = cycleIds.ToDictionary(id => id, _ => new List<CycleDecisionRecordDto>(), StringComparer.Ordinal);
    await using (var command = new NpgsqlCommand(
        """
        select
            decision.cycle_id,
            decision.decision_index,
            decision.bot_instance_id,
            decision.utc,
            decision.pair,
            decision.price,
            decision.fast_ema,
            decision.slow_ema,
            decision.rsi,
            decision.desired_position,
            decision.score,
            decision.risk_approved,
            decision.broker,
            decision.entry_rejection_reason,
            decision.spread_percent,
            decision.price_action_direction,
            decision.price_action_trend_percent,
            decision.exploratory,
            decision.has_bullish_structure,
            decision.ema_fully_confirmed,
            decision.bullish_ema_gap_percent,
            decision.ema_gap_velocity_percent,
            decision.early_entry_eligible,
            decision.early_entry_reason,
            decision.early_entry_diagnostic_score,
            decision.early_entry_suggested_notional_eur,
            action.pair,
            action.action,
            action.reason,
            action.hold_reason_code,
            action.exit_reason_code,
            action.desired_position,
            action.target_notional_eur,
            action.quantity,
            action.entry_price,
            action.last_price,
            action.fill_price,
            action.fee_eur,
            action.gross_notional_eur,
            action.net_notional_eur,
            action.cash_before_eur,
            action.cash_after_eur,
            action.portfolio_value_before_eur,
            action.portfolio_value_after_eur,
            action.fill_source,
            action.side,
            action.reduce_only,
            action.leverage,
            action.exit_trigger_source,
            action.entry_channel,
            action.exchange_order_id,
            action.exchange_fill_timestamp,
            action.modeled_fill_price,
            action.modeled_fee_eur,
            action.stop_distance_pct,
            action.take_profit_distance_pct,
            action.open_risk_eur,
            action.funding_state,
            action.requested_margin_eur,
            action.requested_leverage,
            action.sized_notional_eur,
            action.required_margin_eur,
            action.effective_leverage,
            -- Appended last and uniquely aliased so they can be read by name without
            -- shifting any of the positional ordinals above.
            decision.short_score as d_short_score,
            decision.long_score_threshold as d_long_score_threshold,
            decision.short_score_threshold as d_short_score_threshold,
            decision.has_bearish_structure as d_has_bearish_structure,
            decision.allows_short as d_allows_short
        from dry_run_decision_facts decision
        join dry_run_actions action on action.cycle_id = decision.cycle_id and action.decision_index = decision.decision_index
        where decision.cycle_id = any(@cycle_ids)
        order by decision.cycle_id, decision.decision_index
        """,
        connection))
    {
        command.Parameters.Add("cycle_ids", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = cycleIds;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var cycleId = reader.GetString(0);
            var decisionIndex = reader.GetInt32(1);
            var key = (cycleId, decisionIndex);
            if (!decisions.TryGetValue(cycleId, out var cycleDecisions))
            {
                continue;
            }

            cycleDecisions.Add(new CycleDecisionRecordDto(
                decisionIndex,
                reader.GetString(4),
                reader.GetDecimal(5),
                GetNullableDecimal(reader, 6),
                GetNullableDecimal(reader, 7),
                GetNullableDecimal(reader, 8),
                reader.GetString(9),
                reader.GetDecimal(10),
                reader.GetBoolean(11),
                riskReasons.TryGetValue(key, out var reasons) ? reasons : Array.Empty<string>(),
                contributions.TryGetValue(key, out var contributionItems) ? contributionItems : Array.Empty<SignalContributionDto>(),
                ReadNullableString(reader, 12),
                ReadNullableString(reader, 13),
                reader.GetDecimal(14),
                ReadNullableString(reader, 15),
                GetNullableDecimal(reader, 16),
                reader.GetBoolean(17),
                reader.GetBoolean(18),
                reader.GetBoolean(19),
                GetNullableDecimal(reader, 20),
                GetNullableDecimal(reader, 21),
                reader.GetBoolean(22),
                ReadNullableString(reader, 23),
                reader.GetDecimal(24),
                reader.GetDecimal(25),
                new CycleActionRecordDto(
                    reader.GetString(26),
                    reader.GetString(27),
                    reader.GetString(28),
                    ReadNullableString(reader, 29),
                    ReadNullableString(reader, 30),
                    reader.GetString(31),
                    GetNullableDecimal(reader, 32),
                    GetNullableDecimal(reader, 33),
                    GetNullableDecimal(reader, 34),
                    GetNullableDecimal(reader, 35),
                    GetNullableDecimal(reader, 36),
                    GetNullableDecimal(reader, 37),
                    GetNullableDecimal(reader, 38),
                    GetNullableDecimal(reader, 39),
                    GetNullableDecimal(reader, 40),
                    GetNullableDecimal(reader, 41),
                    GetNullableDecimal(reader, 42),
                    GetNullableDecimal(reader, 43),
                    ReadNullableString(reader, 44),
                    ReadNullableString(reader, 45),
                    reader.IsDBNull(46) ? null : reader.GetBoolean(46),
                    GetNullableDecimal(reader, 47),
                    ReadNullableString(reader, 48),
                    ReadNullableString(reader, 49),
                    ReadNullableString(reader, 50),
                    reader.IsDBNull(51) ? null : reader.GetFieldValue<DateTimeOffset>(51),
                    GetNullableDecimal(reader, 52),
                    GetNullableDecimal(reader, 53),
                    GetNullableDecimal(reader, 54),
                    GetNullableDecimal(reader, 55),
                    GetNullableDecimal(reader, 56),
                    ReadNullableString(reader, 57),
                    GetNullableDecimal(reader, 58),
                    GetNullableDecimal(reader, 59),
                    GetNullableDecimal(reader, 60),
                    GetNullableDecimal(reader, 61),
                    GetNullableDecimal(reader, 62)),
                GetNullableDecimal(reader, reader.GetOrdinal("d_short_score")),
                GetNullableDecimal(reader, reader.GetOrdinal("d_long_score_threshold")),
                GetNullableDecimal(reader, reader.GetOrdinal("d_short_score_threshold")),
                reader.GetBoolean(reader.GetOrdinal("d_has_bearish_structure")),
                reader.GetBoolean(reader.GetOrdinal("d_allows_short"))));
        }
    }

    for (var i = 0; i < cycles.Count; i++)
    {
        var cycle = cycles[i];
        cycles[i] = cycle with
        {
            Record = cycle.Record with
            {
                ActivePairs = activePairs.TryGetValue(cycle.CycleId, out var pairs) ? pairs : Array.Empty<string>(),
                Decisions = decisions.TryGetValue(cycle.CycleId, out var cycleDecisions) ? cycleDecisions : Array.Empty<CycleDecisionRecordDto>()
            }
        };
    }
}

static async Task<IReadOnlyList<DecisionSummaryDto>> ReadDecisions(
    NpgsqlConnection connection,
    string? cycleId,
    string? botInstanceId,
    bool latestMeta,
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
          and (
              @latest_meta = false
              or exists (
                  select 1
                  from dry_run_cycle_facts cycle
                  where cycle.cycle_id = dry_run_decisions.cycle_id
                    and cycle.strategy_version is not distinct from (
                        select latest.strategy_version
                        from dry_run_cycle_facts latest
                        where latest.bot_instance_id = cycle.bot_instance_id
                        order by latest.utc desc, latest.cycle_id desc
                        limit 1
                    )
                    and cycle.change_set is not distinct from (
                        select latest.change_set
                        from dry_run_cycle_facts latest
                        where latest.bot_instance_id = cycle.bot_instance_id
                        order by latest.utc desc, latest.cycle_id desc
                        limit 1
                    )
              )
          )
        order by utc desc, cycle_id desc, pair
        limit @limit offset @offset
        """,
        connection);
    command.Parameters.Add("cycle_id", NpgsqlDbType.Text).Value = (object?)cycleId ?? DBNull.Value;
    command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = (object?)botInstanceId ?? DBNull.Value;
    command.Parameters.Add("latest_meta", NpgsqlDbType.Boolean).Value = latestMeta;
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
    bool latestMeta,
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
            price_action_ready_count
        from dry_run_cycle_entry_diagnostic_facts
        where (@cycle_id is null or cycle_id = @cycle_id)
          and (@bot_instance_id is null or bot_instance_id = @bot_instance_id)
          and (
              @latest_meta = false
              or exists (
                  select 1
                  from dry_run_cycle_facts cycle
                  where cycle.cycle_id = dry_run_cycle_entry_diagnostic_facts.cycle_id
                    and cycle.strategy_version is not distinct from (
                        select latest.strategy_version
                        from dry_run_cycle_facts latest
                        where latest.bot_instance_id = cycle.bot_instance_id
                        order by latest.utc desc, latest.cycle_id desc
                        limit 1
                    )
                    and cycle.change_set is not distinct from (
                        select latest.change_set
                        from dry_run_cycle_facts latest
                        where latest.bot_instance_id = cycle.bot_instance_id
                        order by latest.utc desc, latest.cycle_id desc
                        limit 1
                    )
              )
          )
        order by utc desc, cycle_id desc
        limit @limit offset @offset
        """,
        connection);
    command.Parameters.Add("cycle_id", NpgsqlDbType.Text).Value = (object?)cycleId ?? DBNull.Value;
    command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = (object?)botInstanceId ?? DBNull.Value;
    command.Parameters.Add("latest_meta", NpgsqlDbType.Boolean).Value = latestMeta;
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
            new Dictionary<string, int>(),
            Array.Empty<CycleTopCandidateDto>(),
            Array.Empty<CycleExcludedPairDto>(),
            GetNullableInt(reader, 14)));
    }

    return items;
}

static int? GetNullableInt(NpgsqlDataReader reader, int ordinal) =>
    reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

static async Task<IReadOnlyList<MarketSnapshotDto>> ReadMarketSnapshots(
    NpgsqlConnection connection,
    string? cycleId,
    string? pair,
    string? botInstanceId,
    bool latestMeta,
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
          and (
              @latest_meta = false
              or exists (
                  select 1
                  from dry_run_cycle_facts cycle
                  where cycle.cycle_id = market_snapshots.cycle_id
                    and cycle.strategy_version is not distinct from (
                        select latest.strategy_version
                        from dry_run_cycle_facts latest
                        where latest.bot_instance_id = cycle.bot_instance_id
                        order by latest.utc desc, latest.cycle_id desc
                        limit 1
                    )
                    and cycle.change_set is not distinct from (
                        select latest.change_set
                        from dry_run_cycle_facts latest
                        where latest.bot_instance_id = cycle.bot_instance_id
                        order by latest.utc desc, latest.cycle_id desc
                        limit 1
                    )
              )
          )
        order by utc desc, cycle_id desc, pair
        limit @limit offset @offset
        """,
        connection);
    command.Parameters.Add("cycle_id", NpgsqlDbType.Text).Value = (object?)cycleId ?? DBNull.Value;
    command.Parameters.Add("pair", NpgsqlDbType.Text).Value = (object?)NormalizePairFilter(pair) ?? DBNull.Value;
    command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = (object?)botInstanceId ?? DBNull.Value;
    command.Parameters.Add("latest_meta", NpgsqlDbType.Boolean).Value = latestMeta;
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

static string? Clean(string? value) =>
    string.IsNullOrWhiteSpace(value) ? null : value.Trim();

static string? ReadNullableString(NpgsqlDataReader reader, int ordinal) =>
    reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

static async Task<BotStatusDto> ReadBotStatus(NpgsqlConnection connection, string? botInstanceId, CancellationToken cancellationToken)
{
    var now = DateTimeOffset.UtcNow;
    await using var command = new NpgsqlCommand(
        """
        select
            cycle_id,
            bot_instance_id,
            utc,
            market_data_mode
        from dry_run_cycle_facts
        where (@bot_instance_id is null or bot_instance_id = @bot_instance_id)
        order by utc desc
        limit 1
        """,
        connection);
    command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = (object?)botInstanceId ?? DBNull.Value;

    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return BotStatusDto.NoData(now, botInstanceId, BotEntryBlackout(botInstanceId, now));
    }

    var cycleUtc = DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc);
    var age = now - new DateTimeOffset(cycleUtc);
    var resolvedBotInstanceId = reader.GetString(1);
    var blackout = BotEntryBlackout(resolvedBotInstanceId, now);
    var isStale = age > TimeSpan.FromMinutes(10);
    var runtimeState = isStale
        ? "stale"
        : blackout.IsActive
            ? "night-window"
            : "running";

    return new BotStatusDto(
        now,
        resolvedBotInstanceId,
        reader.GetString(0),
        cycleUtc,
        (int)Math.Max(0, age.TotalSeconds),
        reader.IsDBNull(3) ? "unknown" : reader.GetString(3),
        runtimeState,
        isStale,
        blackout);
}

static EntryBlackoutStatus SpotEntryBlackout(DateTimeOffset nowUtc)
{
    const int fromUtcHour = 22;
    const int minutes = 360;
    var utc = nowUtc.UtcDateTime;
    var startUtc = utc.Date.AddHours(fromUtcHour);
    var endUtc = startUtc.AddMinutes(minutes);
    var isActive = utc >= startUtc && utc < endUtc;
    var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vilnius");

    return new EntryBlackoutStatus(
        true,
        isActive,
        fromUtcHour,
        minutes,
        TimeZoneInfo.ConvertTimeFromUtc(startUtc, zone).ToString("HH:mm", CultureInfo.InvariantCulture),
        TimeZoneInfo.ConvertTimeFromUtc(endUtc, zone).ToString("HH:mm", CultureInfo.InvariantCulture),
        "Europe/Vilnius");
}

static EntryBlackoutStatus BotEntryBlackout(string? botInstanceId, DateTimeOffset nowUtc) =>
    botInstanceId?.StartsWith("spot-", StringComparison.OrdinalIgnoreCase) == true
        ? SpotEntryBlackout(nowUtc)
        : EntryBlackoutStatus.NotConfigured();

static async Task<IReadOnlyDictionary<string, DashboardEntryDto>> ReadEntryContexts(
    NpgsqlConnection connection,
    string? botInstanceId,
    IReadOnlyList<PortfolioPositionDto> positions,
    CancellationToken cancellationToken)
{
    var result = new Dictionary<string, DashboardEntryDto>(StringComparer.OrdinalIgnoreCase);

    // The opening action sits within a cycle or two of the position's open time, so
    // the search is a fifteen-minute window per pair rather than a scan over months
    // of decisions on that pair. On the live database that is the difference between
    // ~240ms and ~1ms.
    var wanted = positions
        .Where(position => position.OpenedAtUtc.HasValue)
        .GroupBy(position => position.Pair, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .ToList();

    if (wanted.Count == 0 || string.IsNullOrWhiteSpace(botInstanceId))
    {
        return result;
    }

    await using var command = new NpgsqlCommand(
        """
        select
            target.pair,
            entry.cycle_id,
            entry.decision_index,
            entry.utc,
            entry.action,
            entry.side,
            entry.leverage,
            entry.score,
            entry.entry_channel,
            entry.reason,
            entry.fee_eur,
            entry.spread_percent,
            entry.price_action_direction,
            entry.price_action_trend_percent,
            entry.bullish_ema_gap_percent,
            entry.fill_source,
            entry.exploratory,
            entry.score_threshold
        from unnest(@pairs, @opened_at) as target(pair, opened_at)
        join lateral (
            select
                decision.cycle_id,
                decision.decision_index,
                decision.utc,
                action.action,
                action.side,
                action.leverage,
                decision.score,
                action.entry_channel,
                action.reason,
                action.fee_eur,
                decision.spread_percent,
                decision.price_action_direction,
                decision.price_action_trend_percent,
                decision.bullish_ema_gap_percent,
                action.fill_source,
                decision.exploratory,
                case when action.side = 'SHORT' then decision.short_score_threshold
                     else decision.long_score_threshold end as score_threshold
            from dry_run_decision_facts decision
            join dry_run_actions action
                on action.cycle_id = decision.cycle_id and action.decision_index = decision.decision_index
            where decision.bot_instance_id = @bot_instance_id
              and decision.pair = target.pair
              and decision.utc >= target.opened_at - interval '15 minutes'
              and decision.utc <= target.opened_at + interval '15 minutes'
              and action.action in ('WOULD_BUY', 'WOULD_OPEN_LONG', 'WOULD_OPEN_SHORT')
            order by decision.utc desc
            limit 1
        ) entry on true
        """,
        connection);
    command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = botInstanceId;
    command.Parameters.Add("pairs", NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
        wanted.Select(position => position.Pair).ToArray();
    command.Parameters.Add("opened_at", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz).Value =
        wanted.Select(position => DateTime.SpecifyKind(position.OpenedAtUtc!.Value, DateTimeKind.Utc)).ToArray();

    var keys = new List<DecisionKey>();
    var drafts = new List<(DecisionKey Key, DashboardEntryDto Entry)>();

    await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
    {
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = new DecisionKey(reader.GetString(1), reader.GetInt32(2));
            keys.Add(key);
            drafts.Add((key, new DashboardEntryDto(
                reader.GetString(0),
                DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
                reader.GetString(4),
                ReadNullableString(reader, 5),
                reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                reader.GetDecimal(7),
                ReadNullableString(reader, 8),
                ReadNullableString(reader, 9),
                reader.IsDBNull(10) ? 0m : reader.GetDecimal(10),
                Array.Empty<DashboardSignalDto>(),
                Array.Empty<string>(),
                reader.IsDBNull(11) ? 0m : reader.GetDecimal(11),
                ReadNullableString(reader, 12),
                reader.IsDBNull(13) ? null : reader.GetDecimal(13),
                reader.IsDBNull(14) ? null : reader.GetDecimal(14),
                ReadNullableString(reader, 15),
                !reader.IsDBNull(16) && reader.GetBoolean(16),
                reader.IsDBNull(17) ? null : reader.GetDecimal(17))));
        }
    }

    var signals = await ReadSignalContributions(connection, keys, cancellationToken);
    var riskReasons = await ReadRiskReasons(connection, keys, cancellationToken);

    foreach (var (key, entry) in drafts)
    {
        result[entry.Pair] = entry with
        {
            Signals = signals.TryGetValue(key, out var contributions) ? contributions : Array.Empty<DashboardSignalDto>(),
            RiskReasons = riskReasons.TryGetValue(key, out var reasons) ? reasons : Array.Empty<string>()
        };
    }

    return result;
}

// Both lookups over-select on the cross product of cycle ids and decision indexes and
// then filter in memory: the exact tuple filter is awkward to parameterise and the row
// counts here are tiny (open positions, or today's trades).
static async Task<Dictionary<DecisionKey, IReadOnlyList<DashboardSignalDto>>> ReadSignalContributions(
    NpgsqlConnection connection,
    IReadOnlyList<DecisionKey> keys,
    CancellationToken cancellationToken)
{
    var result = new Dictionary<DecisionKey, IReadOnlyList<DashboardSignalDto>>();
    if (keys.Count == 0)
    {
        return result;
    }

    var wanted = keys.ToHashSet();
    await using var command = new NpgsqlCommand(
        """
        select cycle_id, decision_index, name, value, reason
        from dry_run_signal_contributions
        where cycle_id = any(@cycle_ids) and decision_index = any(@decision_indexes)
        order by cycle_id, decision_index, contribution_index
        """,
        connection);
    command.Parameters.Add("cycle_ids", NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
        keys.Select(key => key.CycleId).Distinct().ToArray();
    command.Parameters.Add("decision_indexes", NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
        keys.Select(key => key.DecisionIndex).Distinct().ToArray();

    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        var key = new DecisionKey(reader.GetString(0), reader.GetInt32(1));
        if (!wanted.Contains(key))
        {
            continue;
        }

        if (!result.TryGetValue(key, out var list))
        {
            list = new List<DashboardSignalDto>();
            result[key] = list;
        }

        ((List<DashboardSignalDto>)list).Add(new DashboardSignalDto(
            reader.GetString(2),
            reader.GetDecimal(3),
            reader.GetString(4)));
    }

    return result;
}

static async Task<Dictionary<DecisionKey, IReadOnlyList<string>>> ReadRiskReasons(
    NpgsqlConnection connection,
    IReadOnlyList<DecisionKey> keys,
    CancellationToken cancellationToken)
{
    var result = new Dictionary<DecisionKey, IReadOnlyList<string>>();
    if (keys.Count == 0)
    {
        return result;
    }

    var wanted = keys.ToHashSet();
    await using var command = new NpgsqlCommand(
        """
        select cycle_id, decision_index, reason
        from dry_run_decision_risk_reasons
        where cycle_id = any(@cycle_ids) and decision_index = any(@decision_indexes)
        order by cycle_id, decision_index, reason_index
        """,
        connection);
    command.Parameters.Add("cycle_ids", NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
        keys.Select(key => key.CycleId).Distinct().ToArray();
    command.Parameters.Add("decision_indexes", NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
        keys.Select(key => key.DecisionIndex).Distinct().ToArray();

    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        var key = new DecisionKey(reader.GetString(0), reader.GetInt32(1));
        if (!wanted.Contains(key))
        {
            continue;
        }

        if (!result.TryGetValue(key, out var list))
        {
            list = new List<string>();
            result[key] = list;
        }

        ((List<string>)list).Add(reader.GetString(2));
    }

    return result;
}

static async Task<IReadOnlyList<DashboardWorkerDto>> ReadWorkers(
    NpgsqlConnection connection,
    CancellationToken cancellationToken)
{
    var now = DateTimeOffset.UtcNow;
    await using var command = new NpgsqlCommand(
        """
        select
            summary.bot_instance_id,
            latest.bot_instance_name,
            latest.utc,
            latest.market_data_mode,
            latest.active_pairs_count
        from portfolio_state_summary summary
        join lateral (
            select bot_instance_name, utc, market_data_mode, active_pairs_count
            from dry_run_cycle_facts cycle
            where cycle.bot_instance_id = summary.bot_instance_id
            order by cycle.utc desc, cycle.cycle_id desc
            limit 1
        ) latest on true
        order by summary.bot_instance_id
        """,
        connection);

    var workers = new List<DashboardWorkerDto>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        var cycleUtc = DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc);
        var age = now - new DateTimeOffset(cycleUtc);
        var isStale = age > TimeSpan.FromMinutes(10);
        workers.Add(new DashboardWorkerDto(
            reader.GetString(0),
            ReadNullableString(reader, 1),
            cycleUtc,
            (int)Math.Max(0, age.TotalSeconds),
            isStale ? "stale" : "running",
            isStale,
            reader.IsDBNull(3) ? "unknown" : reader.GetString(3),
            reader.IsDBNull(4) ? 0 : reader.GetInt32(4)));
    }

    return workers;
}

// Closed days never change, so they are rolled up once into portfolio_daily_equity
// and read back from there. That keeps the landing page off a 300ms sequential scan
// of dry_run_cycle_facts every 10 seconds, and — more importantly — preserves the
// equity curve even if the per-cycle facts are ever pruned.
static async Task EnsureDailyEquityTable(NpgsqlConnection connection, CancellationToken cancellationToken)
{
    await using var command = new NpgsqlCommand(
        """
        create table if not exists portfolio_daily_equity (
            bot_instance_id text not null,
            local_date date not null,
            time_zone text not null,
            open_value_eur numeric not null,
            high_value_eur numeric not null,
            low_value_eur numeric not null,
            close_value_eur numeric not null,
            cycle_count integer not null,
            recorded_at timestamptz not null default now(),
            primary key (bot_instance_id, local_date)
        )
        """,
        connection);
    await command.ExecuteNonQueryAsync(cancellationToken);

    await using var revision = new NpgsqlCommand(
        """
        alter table portfolio_daily_equity
            add column if not exists revision integer,
            add column if not exists first_utc timestamptz,
            add column if not exists last_utc timestamptz
        """,
        connection);
    await revision.ExecuteNonQueryAsync(cancellationToken);

    // Owned by the workers, declared here too because either side can deploy first
    // and the equity queries below read it.
    await using var unsettled = new NpgsqlCommand(
        """
        do $$
        begin
            if to_regclass('public.dry_run_cycle_facts') is not null then
                alter table dry_run_cycle_facts
                    add column if not exists valuation_unsettled boolean not null default false;
            end if;
        end $$;
        """,
        connection);
    await unsettled.ExecuteNonQueryAsync(cancellationToken);
}

// How much money the bot actually had at work on each local day, and how many
// trades it closed there.
//
// The percentage a day earned is meaningless against the portfolio: an account
// holding 60 USD that only ever committed 15 to a position did not make 30% on its
// capital, it made 124% on the part it used. The figure that belongs under the day
// is the peak of the margin held at once - three positions of 15 alive together are
// 45, the same three one after another are 15.
//
// Positions outlive days, so this walks the whole action history in order and
// carries the open set across midnight rather than resetting it.
static async Task<Dictionary<string, (decimal PeakMarginEur, int ClosedTrades)>> ReadDailyTradingLoad(
    NpgsqlConnection connection,
    string? botInstanceId,
    string timeZoneId,
    CancellationToken cancellationToken)
{
    // The journal alone cannot carry this. Positions the exchange closed before the
    // worker learned to record such closures were never journalled, so their margin
    // is never released and the running sum climbs without bound: it put
    // futures-live at a peak of 79.93 on an account of about 50, with a cap of three
    // positions at 15. Two corrections, both from observed state rather than guesses:
    // a second open on a pair replaces the first, because an unobserved close must
    // have happened in between; and a cycle that reports no open position value at
    // all resets the held set, because the exchange saying the account is flat is
    // proof that nothing is held.
    await using var command = new NpgsqlCommand(
        """
        select kind, local_date, pair, action, margin from (
            select
                'ACTION' as kind,
                cycle.utc as utc,
                cycle.bot_instance_id as bot_instance_id,
                (cycle.utc at time zone @time_zone)::date as local_date,
                action.pair as pair,
                action.action as action,
                coalesce(action.actual_initial_margin_eur, action.requested_margin_eur, 0) as margin,
                action.decision_index as decision_index
            from dry_run_actions action
            join dry_run_cycle_facts cycle on cycle.cycle_id = action.cycle_id
            where action.action in ('WOULD_BUY', 'WOULD_OPEN_LONG', 'WOULD_OPEN_SHORT',
                                    'WOULD_SELL', 'WOULD_CLOSE')
            union all
            select 'FLAT', utc, bot_instance_id,
                   (utc at time zone @time_zone)::date, '', '', 0, 0
            from dry_run_cycle_facts
            where positions_value_after_eur = 0
              and not valuation_unsettled
              and cycle_id not like '%-backfill'
        ) event
        where (@bot_instance_id is null or event.bot_instance_id = @bot_instance_id)
        order by event.utc, event.decision_index
        """,
        connection);
    command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = (object?)botInstanceId ?? DBNull.Value;
    command.Parameters.Add("time_zone", NpgsqlDbType.Text).Value = timeZoneId;

    var byDate = new Dictionary<string, (decimal Peak, int Closed)>();
    var openMargin = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    var held = 0m;
    string? currentDate = null;

    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        var kind = reader.GetString(0);
        var date = reader.GetFieldValue<DateTime>(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var pair = reader.GetString(2);
        var action = reader.GetString(3);
        var margin = reader.GetDecimal(4);

        if (date != currentDate)
        {
            currentDate = date;
            // A day starts owing whatever was still open at midnight.
            if (!byDate.ContainsKey(date))
            {
                byDate[date] = (held, 0);
            }
        }

        if (kind == "FLAT")
        {
            openMargin.Clear();
            held = 0m;
        }
        else if (action is "WOULD_BUY" or "WOULD_OPEN_LONG" or "WOULD_OPEN_SHORT")
        {
            openMargin[pair] = margin;
            held = openMargin.Values.Sum();
        }
        else
        {
            openMargin.Remove(pair);
            held = openMargin.Values.Sum();

            var counted = byDate[date];
            byDate[date] = (counted.Peak, counted.Closed + 1);
        }

        var current = byDate[date];
        if (held > current.Peak)
        {
            byDate[date] = (held, current.Closed);
        }
    }

    return byDate.ToDictionary(entry => entry.Key, entry => (entry.Value.Peak, entry.Value.Closed));
}

// Written by the workers from the exchange ledger; declared here too so the
// dashboard keeps working on a database where no worker has synced yet.
static async Task EnsureCashEventsTable(NpgsqlConnection connection, CancellationToken cancellationToken)
{
    await using var command = new NpgsqlCommand(
        """
        create table if not exists portfolio_cash_events (
            bot_instance_id text not null,
            event_id text not null,
            occurred_at timestamptz not null,
            event_type text not null,
            amount numeric not null,
            asset text,
            source text not null,
            recorded_at timestamptz not null default now(),
            primary key (bot_instance_id, event_id)
        )
        """,
        connection);
    await command.ExecuteNonQueryAsync(cancellationToken);
}

// Net money movement per local day. Several movements in one day collapse to one
// figure on the chart — the individual entries stay in portfolio_cash_events.
//
// A movement counts for a day only if it landed inside that day's observed window.
// Anything earlier is already contained in the day's opening value, and counting
// it again is not a rounding error: futures-lukas-live was funded with 60 USD at
// 08:21 and the worker's first cycle at 09:11 opened at exactly 60, so treating
// that transfer as same-day inflow turned an +18.54 day into a reported -41.46.
static async Task<(Dictionary<string, decimal> InWindow, Dictionary<string, decimal> BeforeWindow)> ReadDailyCashMovement(
    NpgsqlConnection connection,
    string botInstanceId,
    string timeZoneId,
    CancellationToken cancellationToken)
{
    await using var command = new NpgsqlCommand(
        """
        select
            day.local_date,
            coalesce(sum(event.amount) filter (
                where event.occurred_at >= day.first_utc
                  and event.occurred_at <= day.last_utc), 0) as net_amount,
            -- Money that arrived on the same local day but before the bot's first
            -- cycle. It is already inside that day's opening value, so it must not
            -- count as same-day inflow - but on the first day of a series it is the
            -- capital the account was started with, and the chart draws it as such.
            coalesce(sum(event.amount) filter (
                where event.occurred_at < day.first_utc
                  and (event.occurred_at at time zone @time_zone)::date = day.local_date), 0) as before_window
        from portfolio_daily_equity day
        left join portfolio_cash_events event
            on event.bot_instance_id = day.bot_instance_id
        where day.bot_instance_id = @bot_instance_id
          and day.first_utc is not null
        group by day.local_date
        """,
        connection);
    command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = botInstanceId;
    command.Parameters.Add("time_zone", NpgsqlDbType.Text).Value = timeZoneId;

    var byDate = new Dictionary<string, decimal>();
    var beforeWindow = new Dictionary<string, decimal>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        var date = reader.GetFieldValue<DateTime>(0).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        byDate[date] = reader.GetDecimal(1);
        beforeWindow[date] = reader.GetDecimal(2);
    }

    return (byDate, beforeWindow);
}

static async Task BackfillDailyEquity(
    NpgsqlConnection connection,
    string botInstanceId,
    string timeZoneId,
    CancellationToken cancellationToken)
{
    // A change to how a day is computed invalidates every stored day, so rows from an
    // older revision are dropped and rebuilt. Closed days are otherwise write-once.
    await using (var reset = new NpgsqlCommand(
        """
        delete from portfolio_daily_equity
        where bot_instance_id = @bot_instance_id
          and revision is distinct from @revision
        """,
        connection))
    {
        reset.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = botInstanceId;
        reset.Parameters.Add("revision", NpgsqlDbType.Integer).Value = DashboardDefaults.RollupRevision;
        await reset.ExecuteNonQueryAsync(cancellationToken);
    }

    // Only days after the last stored one are aggregated, so the expensive full
    // scan happens once and every later call touches a single day.
    //
    // Individual cycles sometimes record a nonsense portfolio value — futures-live
    // has a 704 USD reading on a day that opened and closed near 52, a 166 on a day
    // around 32, and zeros from the cycles before the worker first reconciled with
    // Kraken. One bad cycle would otherwise set the day's high or low and flatten the
    // whole chart, so values are kept only within a third and triple of the day's
    // median. Real intraday moves survive that; single-cycle spikes do not.
    await using var command = new NpgsqlCommand(
        """
        with day_values as (
            select
                (utc at time zone @time_zone)::date as local_date,
                utc,
                cycle_id,
                portfolio_value_after_eur as value
            from dry_run_cycle_facts
            where bot_instance_id = @bot_instance_id
              and portfolio_value_after_eur > 0
              -- A cycle in which a position left the account: it is already gone from
              -- the position read while its proceeds have not yet landed in the wallet
              -- read, so the total is understated by roughly the whole position. Lukas
              -- read 74.39 at 12:03 on 2026-08-21 and 92.91 two minutes later with no
              -- trade in between. The account never held 74.39, so it is not drawn.
              and not valuation_unsettled
              -- Rows the closure repair wrote are not observations. It stamps the
              -- fill's own time on a cycle carrying the portfolio as it stood when
              -- the repair ran, so 13 rows landed across three past days all reading
              -- 49.49 on futures-live and 94.70 on lukas. Back-dated into the series
              -- they draw values the account never held.
              and cycle_id not like '%-backfill'
              and utc >= coalesce(
                    (select max(local_date) at time zone @time_zone
                     from portfolio_daily_equity
                     where bot_instance_id = @bot_instance_id),
                    '-infinity'::timestamptz)
        ),
        day_median as (
            select local_date, percentile_cont(0.5) within group (order by value) as median
            from day_values
            group by local_date
        ),
        -- The typical day, used to throw away whole days that are not a funded
        -- account: futures-live's first day sits at 3e-6 USD across 329 cycles,
        -- before the worker had reconciled with Kraken. Left in, it made the
        -- 30-day change read "+1 637 909,7 %".
        series as (
            select percentile_cont(0.5) within group (order by median) as median
            from day_median
        ),
        clean as (
            select value.local_date, value.utc, value.cycle_id, value.value
            from day_values value
            join day_median median on median.local_date = value.local_date
            cross join series
            where median.median > 0
              and series.median > 0
              and median.median >= series.median / 10.0
              and value.value between median.median / 3.0 and median.median * 3.0
        )
        insert into portfolio_daily_equity
            (bot_instance_id, local_date, time_zone, open_value_eur, high_value_eur,
             low_value_eur, close_value_eur, cycle_count, revision, first_utc, last_utc)
        select
            @bot_instance_id,
            local_date,
            @time_zone,
            (array_agg(value order by utc asc, cycle_id asc))[1],
            max(value),
            min(value),
            (array_agg(value order by utc desc, cycle_id desc))[1],
            count(*),
            @revision,
            min(utc),
            max(utc)
        from clean
        group by local_date
        having local_date < (now() at time zone @time_zone)::date
        on conflict (bot_instance_id, local_date) do nothing
        """,
        connection);
    command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = botInstanceId;
    command.Parameters.Add("time_zone", NpgsqlDbType.Text).Value = timeZoneId;
    command.Parameters.Add("revision", NpgsqlDbType.Integer).Value = DashboardDefaults.RollupRevision;
    await command.ExecuteNonQueryAsync(cancellationToken);
}

// True peak-to-trough over the ordered per-cycle series. Close-to-close, which is
// what the page used to show, cannot see an intraday fall at all and reported a
// structural 0.0% on an account with one closed day. The daily rollup cannot fix
// it either: it stores a day's high and low but not which came first.
//
// The window slides, so this is not incrementally maintainable; it is one ordered
// scan, cached per instance. ~320ms once every ten minutes is affordable where the
// same scan on every 10s poll was not.
static async Task<decimal> ReadMaxDrawdownPercent(
    NpgsqlConnection connection,
    string botInstanceId,
    int days,
    CancellationToken cancellationToken)
{
    var now = DateTimeOffset.UtcNow;

    lock (DrawdownCache.Values)
    {
        if (DrawdownCache.Values.TryGetValue(botInstanceId, out var cached)
            && now - cached.At < TimeSpan.FromMinutes(10))
        {
            return cached.Percent;
        }
    }

    await DrawdownCache.Gate.WaitAsync(cancellationToken);
    try
    {
        lock (DrawdownCache.Values)
        {
            if (DrawdownCache.Values.TryGetValue(botInstanceId, out var cached)
                && now - cached.At < TimeSpan.FromMinutes(10))
            {
                return cached.Percent;
            }
        }

        await using var command = new NpgsqlCommand(
            """
            with day_values as (
                select
                    (utc at time zone @time_zone)::date as local_date,
                    utc,
                    cycle_id,
                    portfolio_value_after_eur as value
                from dry_run_cycle_facts
                where bot_instance_id = @bot_instance_id
                  and portfolio_value_after_eur > 0
                  -- Same reasons as the rollup. This one mattered most here: the
                  -- single unsettled reading was reported as a -22.6% max drawdown.
                  and not valuation_unsettled
                  -- Repair rows are not observations either.
                  and cycle_id not like '%-backfill'
                  and utc >= @utc_start
                  and (utc at time zone @time_zone)::date >= @launch_date::date
            ),
            day_median as (
                select local_date, percentile_cont(0.5) within group (order by value) as median
                from day_values group by local_date
            ),
            series as (
                select percentile_cont(0.5) within group (order by median) as median from day_median
            ),
            clean as (
                select value.utc, value.cycle_id, value.value
                from day_values value
                join day_median median on median.local_date = value.local_date
                cross join series
                where median.median > 0
                  and series.median > 0
                  and median.median >= series.median / 10.0
                  and value.value between median.median / 3.0 and median.median * 3.0
            ),
            running as (
                select value,
                       max(value) over (order by utc, cycle_id
                                        rows between unbounded preceding and current row) as peak
                from clean
            )
            select coalesce(min((value - peak) / nullif(peak, 0)) * 100, 0)
            from running
            """,
            connection);
        command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = botInstanceId;
        command.Parameters.Add("time_zone", NpgsqlDbType.Text).Value = "Europe/Vilnius";
        command.Parameters.Add("utc_start", NpgsqlDbType.TimestampTz).Value = now.AddDays(-days);
        command.Parameters.Add("launch_date", NpgsqlDbType.Text).Value = DashboardDefaults.LaunchLocalDate;

        var result = await command.ExecuteScalarAsync(cancellationToken);
        var percent = result is decimal value ? value : 0m;

        lock (DrawdownCache.Values)
        {
            DrawdownCache.Values[botInstanceId] = (percent, now);
        }

        return percent;
    }
    finally
    {
        DrawdownCache.Gate.Release();
    }
}

static async Task<DashboardEquityDto> ReadEquityDays(
    NpgsqlConnection connection,
    string? botInstanceId,
    int days,
    CancellationToken cancellationToken)
{
    const string timeZoneId = "Europe/Vilnius";
    var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
    var todayLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone)
        .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    if (string.IsNullOrWhiteSpace(botInstanceId))
    {
        // The curve is per account; without one there is nothing meaningful to plot.
        return new DashboardEquityDto(
            timeZoneId, todayLocal, Array.Empty<DashboardEquityDayDto>(), 0m, false, null, null, 0m);
    }

    // "create table if not exists" is cheap but not free: it is a DDL round trip on
    // every poll, against a database five workers are writing to continuously. Once
    // per process is enough — the tables cannot vanish underneath us.
    if (!DashboardSchema.Ready)
    {
        await EnsureDailyEquityTable(connection, cancellationToken);
        await EnsureCashEventsTable(connection, cancellationToken);
        DashboardSchema.Ready = true;
    }

    await BackfillDailyEquity(connection, botInstanceId, timeZoneId, cancellationToken);
    var (movements, beforeWindow) = await ReadDailyCashMovement(connection, botInstanceId, timeZoneId, cancellationToken);

    await using var command = new NpgsqlCommand(
        """
        select
            local_date,
            open_value_eur,
            high_value_eur,
            low_value_eur,
            close_value_eur,
            cycle_count,
            gap_minutes
        from (
            select
                local_date,
                open_value_eur,
                high_value_eur,
                low_value_eur,
                close_value_eur,
                cycle_count,
                -- Minutes between the previous day's last observed cycle and this
                -- day's first one. Normally a couple of minutes; a long silence means
                -- the worker was down and nobody saw what the account did, which is
                -- not the same as the bot doing nothing.
                extract(epoch from (first_utc - lag(last_utc) over (order by local_date))) / 60
                    as gap_minutes
            from portfolio_daily_equity
            where bot_instance_id = @bot_instance_id
        ) day
        order by local_date desc
        limit @days
        """,
        connection);
    command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = botInstanceId;
    command.Parameters.AddWithValue("days", days);

    var closed = new List<DashboardEquityDayDto>();
    await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
    {
        while (await reader.ReadAsync(cancellationToken))
        {
            closed.Add(new DashboardEquityDayDto(
                reader.GetFieldValue<DateTime>(0).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.GetDecimal(1),
                reader.GetDecimal(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                movements.TryGetValue(
                    reader.GetFieldValue<DateTime>(0).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    out var movement) ? movement : 0m,
                reader.GetInt32(5),
                beforeWindow.TryGetValue(
                    reader.GetFieldValue<DateTime>(0).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    out var opening) ? opening : 0m,
                await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetDouble(6)));
        }
    }

    closed.Reverse();

    var load = await ReadDailyTradingLoad(connection, botInstanceId, timeZoneId, cancellationToken);

    // What the bot did, with money you moved taken out: a day that received a
    // deposit is not a day the bot earned it.
    DashboardDayResultDto? Result(DashboardEquityDayDto? day)
    {
        if (day is null || day.Open <= 0m) return null;
        var bot = day.Close - day.Open - day.ManualAdjustmentEur;
        load.TryGetValue(day.Date, out var used);
        var peak = used.PeakMarginEur > 0m ? used.PeakMarginEur : (decimal?)null;
        return new DashboardDayResultDto(
            day.Date, day.Open, day.Open + bot, day.ManualAdjustmentEur, bot, bot / day.Open * 100m,
            peak, peak is null ? null : bot / peak.Value * 100m, used.ClosedTrades);
    }

    var drawdown = await ReadMaxDrawdownPercent(connection, botInstanceId, days, cancellationToken);
    var yesterday = Result(closed.Count > 0 ? closed[^1] : null);
    var best = closed
        .Select(Result)
        .Where(result => result is not null)
        .OrderByDescending(result => result!.BotPercent)
        .FirstOrDefault();

    // Deposits and withdrawals come from the exchange ledger, which the workers sync
    // into portfolio_cash_events. They are never inferred from cash moving between
    // cycles: on the live accounts that also happens when the exchange releases or
    // re-commits margin, and the two are indistinguishable from balances alone.
    return new DashboardEquityDto(
        timeZoneId,
        todayLocal,
        closed,
        closed.Sum(day => day.ManualAdjustmentEur),
        true,
        yesterday,
        best,
        drawdown);
}

static async Task<DashboardTodayDto> ReadTodayTrades(
    NpgsqlConnection connection,
    string? botInstanceId,
    CancellationToken cancellationToken)
{
    const string timeZoneId = "Europe/Vilnius";
    var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
    var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
    var localStart = localNow.Date;
    var utcStart = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone), TimeSpan.Zero);

    await using var command = new NpgsqlCommand(
        """
        with today_cycles as materialized (
            select cycle_id, utc
            from dry_run_cycle_facts
            where (@bot_instance_id is null or bot_instance_id = @bot_instance_id)
              and utc >= @utc_start
        ),
        traded as materialized (
            select action.*
            from dry_run_actions action
            join today_cycles cycle on cycle.cycle_id = action.cycle_id
            where action.action in ('WOULD_BUY', 'WOULD_SELL', 'WOULD_OPEN_LONG', 'WOULD_OPEN_SHORT', 'WOULD_CLOSE')
        )
        select
            action.cycle_id,
            action.decision_index,
            cycle.utc,
            decision.pair,
            action.action,
            action.side,
            action.leverage,
            action.fill_price,
            action.quantity,
            action.fee_eur,
            decision.score,
            action.target_notional_eur,
            action.portfolio_value_before_eur,
            action.portfolio_value_after_eur,
            action.exit_reason_code,
            action.exit_trigger_source,
            action.entry_channel,
            action.exchange_order_id,
            action.reason,
            action.reduce_only,
            decision.spread_percent,
            decision.price_action_direction,
            decision.price_action_trend_percent,
            decision.bullish_ema_gap_percent,
            action.fill_source,
            decision.exploratory,
            case when action.side = 'SHORT' then decision.short_score_threshold
                 else decision.long_score_threshold end as score_threshold
        from traded action
        join today_cycles cycle on cycle.cycle_id = action.cycle_id
        join dry_run_decision_facts decision
            on decision.cycle_id = action.cycle_id and decision.decision_index = action.decision_index
        order by cycle.utc desc, action.decision_index desc
        limit 60
        """,
        connection);
    command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = (object?)botInstanceId ?? DBNull.Value;
    command.Parameters.Add("utc_start", NpgsqlDbType.TimestampTz).Value = utcStart;

    var keys = new List<DecisionKey>();
    var drafts = new List<(DecisionKey Key, DashboardTradeDto Trade)>();

    await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
    {
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = new DecisionKey(reader.GetString(0), reader.GetInt32(1));
            var action = reader.GetString(4);
            var log = reader.GetString(18);
            var valueBefore = reader.GetDecimal(12);
            var valueAfter = reader.GetDecimal(13);
            var isExit = action is "WOULD_CLOSE" or "WOULD_SELL";

            keys.Add(key);
            drafts.Add((key, new DashboardTradeDto(
                DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc),
                reader.GetString(3),
                action,
                ReadNullableString(reader, 5),
                reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                reader.GetDecimal(7),
                reader.GetDecimal(8),
                reader.GetDecimal(9),
                reader.GetDecimal(10),
                reader.GetDecimal(11),
                isExit ? valueAfter - valueBefore : null,
                isExit ? ParseRealizedPercent(log) : null,
                ReadNullableString(reader, 14),
                ReadNullableString(reader, 15),
                ReadNullableString(reader, 16),
                ReadNullableString(reader, 17),
                log,
                !reader.IsDBNull(19) && reader.GetBoolean(19),
                Array.Empty<DashboardSignalDto>(),
                Array.Empty<string>(),
                reader.IsDBNull(20) ? 0m : reader.GetDecimal(20),
                ReadNullableString(reader, 21),
                reader.IsDBNull(22) ? null : reader.GetDecimal(22),
                reader.IsDBNull(23) ? null : reader.GetDecimal(23),
                ReadNullableString(reader, 24),
                !reader.IsDBNull(25) && reader.GetBoolean(25),
                reader.IsDBNull(26) ? null : reader.GetDecimal(26))));
        }
    }

    var signals = await ReadSignalContributions(connection, keys, cancellationToken);
    var riskReasons = await ReadRiskReasons(connection, keys, cancellationToken);

    var trades = drafts
        .Select(draft => draft.Trade with
        {
            Signals = signals.TryGetValue(draft.Key, out var contributions) ? contributions : Array.Empty<DashboardSignalDto>(),
            RiskReasons = riskReasons.TryGetValue(draft.Key, out var reasons) ? reasons : Array.Empty<string>()
        })
        .ToList();

    return new DashboardTodayDto(
        localStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        timeZoneId,
        trades,
        trades.Count(trade => trade.Action is "WOULD_BUY" or "WOULD_OPEN_LONG" or "WOULD_OPEN_SHORT"),
        trades.Count(trade => trade.Action is "WOULD_CLOSE" or "WOULD_SELL"),
        trades.Count(trade => IsStopLossExit(trade)),
        trades.Count(trade => IsTakeProfitExit(trade)),
        trades.Where(trade => trade.RealizedPnlEur.HasValue).Sum(trade => trade.RealizedPnlEur!.Value));
}

static bool IsStopLossExit(DashboardTradeDto trade) =>
    (trade.ExitReasonCode?.Contains("STOP", StringComparison.OrdinalIgnoreCase) ?? false)
    || (trade.ExitTriggerSource?.Contains("STOP", StringComparison.OrdinalIgnoreCase) ?? false)
    || trade.Log.Contains("STOP_LOSS", StringComparison.OrdinalIgnoreCase);

static bool IsTakeProfitExit(DashboardTradeDto trade) =>
    (trade.ExitReasonCode?.Contains("TAKE_PROFIT", StringComparison.OrdinalIgnoreCase) ?? false)
    || (trade.ExitTriggerSource?.Contains("TAKE_PROFIT", StringComparison.OrdinalIgnoreCase) ?? false)
    || trade.Log.Contains("TAKE_PROFIT", StringComparison.OrdinalIgnoreCase);

// Exit logs carry the exchange's own realised percentage, e.g.
// "realized PnL USD -3.0708 (-2.0488 %)". The absolute figure is taken from the
// portfolio delta instead; only the percentage is read back out of the log.
static decimal? ParseRealizedPercent(string log)
{
    var match = System.Text.RegularExpressions.Regex.Match(
        log,
        @"realized\s+PnL[^()]*\(\s*(?<value>[-+−]?\d+(?:[.,]\d+)?)\s*%",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    if (!match.Success)
    {
        return null;
    }

    var raw = match.Groups["value"].Value.Replace('−', '-').Replace(',', '.');
    return decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}

internal sealed record PortfolioResponse(
    DateTimeOffset Utc,
    PortfolioSummaryDto? Summary,
    IReadOnlyList<PortfolioPositionDto> Positions,
    string? Warning);

internal sealed record BotStatusDto(
    DateTimeOffset Utc,
    string? BotInstanceId,
    string? LatestCycleId,
    DateTime? LatestCycleUtc,
    int? LatestCycleAgeSeconds,
    string MarketDataMode,
    string RuntimeState,
    bool IsStale,
    EntryBlackoutStatus EntryBlackout)
{
    public static BotStatusDto NoData(DateTimeOffset utc, string? botInstanceId, EntryBlackoutStatus entryBlackout) =>
        new(
            utc,
            botInstanceId,
            null,
            null,
            null,
            "unknown",
            "no-data",
            true,
            entryBlackout);
}

internal sealed record EntryBlackoutStatus(
    bool Configured,
    bool IsActive,
    int? FromUtcHour,
    int? Minutes,
    string? LocalStart,
    string? LocalEnd,
    string? LocalTimeZone)
{
    public static EntryBlackoutStatus NotConfigured() =>
        new(false, false, null, null, null, null, null);
}

internal sealed record PortfolioSummaryDto(
    DateTime UpdatedAt,
    decimal CashEur,
    decimal? CashQuoteValue,
    string? CashQuoteCurrency,
    decimal PositionsValueEur,
    decimal TotalValueEur,
    int OpenPositions,
    string? DailyRiskDateUtc,
    decimal? DailyRealizedPnlEur,
    decimal ExternalPnlEur,
    // Net-of-fees valuation (worker's conservative liquidation value): what the
    // portfolio would actually realize if liquidated now. PositionsValueEur/
    // TotalValueEur above are the gross market value shown for Kraken parity.
    decimal NetPositionsValueEur,
    decimal NetTotalValueEur);

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
    string? SlOrderState,
    // Which protection is actually live on the exchange. Once the position is far
    // enough toward its target the worker arms a trailing stop and CANCELS the take
    // profit and stop loss, so a page still showing "SL / TP" at that moment is
    // describing orders that no longer exist.
    string? TrailingStopState,
    decimal? TrailingStopPercent,
    // Net-of-fees unrealized PnL (worker's conservative liquidation basis). The
    // MarketValueEur/UnrealizedPnl* fields above are gross, at last price, for Kraken
    // parity; these show what the position would net after round-trip costs.
    decimal NetUnrealizedPnlEur,
    decimal NetUnrealizedPnlPercent);

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
    CycleRecordDto Record);

internal sealed record CycleRecordDto(
    string CycleId,
    string BotInstanceId,
    string BotInstanceName,
    DateTime Utc,
    string MarketDataMode,
    string AiProvider,
    CycleWorkerDto Worker,
    int ActivePairsCount,
    CyclePortfolioSnapshotDto PortfolioBefore,
    CyclePortfolioSnapshotDto PortfolioAfter,
    IReadOnlyList<string> ActivePairs,
    IReadOnlyList<CycleDecisionRecordDto> Decisions);

internal sealed record CycleWorkerDto(
    string? Version,
    string? Commit,
    string? BuildUtc,
    string? ImageTag,
    string? StrategyVersion,
    string? ChangeSet);

internal sealed record CyclePortfolioSnapshotDto(
    decimal CashEur,
    decimal PositionsValueEur,
    decimal TotalValueEur);

internal sealed record CycleDecisionRecordDto(
    int DecisionIndex,
    string Pair,
    decimal Price,
    decimal? FastEma,
    decimal? SlowEma,
    decimal? Rsi,
    string DesiredPosition,
    decimal Score,
    bool RiskApproved,
    IReadOnlyList<string> RiskReasons,
    IReadOnlyList<SignalContributionDto> Contributions,
    string? Broker,
    string? EntryRejectionReason,
    decimal SpreadPercent,
    string? PriceActionDirection,
    decimal? PriceActionTrendPercent,
    bool Exploratory,
    bool HasBullishStructure,
    bool EmaFullyConfirmed,
    decimal? BullishEmaGapPercent,
    decimal? EmaGapVelocityPercent,
    bool EarlyEntryEligible,
    string? EarlyEntryReason,
    decimal EarlyEntryDiagnosticScore,
    decimal EarlyEntrySuggestedNotionalEur,
    CycleActionRecordDto DryRunAction,
    // Both-direction context. Without these the dashboard had to guess the SHORT side
    // by parsing contribution text and could not show either entry threshold, which is
    // why cards claimed "SHORT score 0.70 below threshold 0.00" and never explained LONG.
    decimal? ShortScore = null,
    decimal? LongScoreThreshold = null,
    decimal? ShortScoreThreshold = null,
    bool HasBearishStructure = false,
    bool AllowsShort = false);

internal sealed record SignalContributionDto(
    string Name,
    decimal Value,
    string Reason);

internal sealed record CycleActionRecordDto(
    string Pair,
    string Action,
    string Reason,
    string? HoldReasonCode,
    string? ExitReasonCode,
    string DesiredPosition,
    decimal? TargetNotionalEur,
    decimal? Quantity,
    decimal? EntryPrice,
    decimal? LastPrice,
    decimal? FillPrice,
    decimal? FeeEur,
    decimal? GrossNotionalEur,
    decimal? NetNotionalEur,
    decimal? CashBeforeEur,
    decimal? CashAfterEur,
    decimal? PortfolioValueBeforeEur,
    decimal? PortfolioValueAfterEur,
    string? FillSource,
    string? Side,
    bool? ReduceOnly,
    decimal? Leverage,
    string? ExitTriggerSource,
    string? EntryChannel,
    string? ExchangeOrderId,
    DateTimeOffset? ExchangeFillTimestamp,
    decimal? ModeledFillPrice,
    decimal? ModeledFeeEur,
    decimal? StopDistancePct,
    decimal? TakeProfitDistancePct,
    decimal? OpenRiskEur,
    string? FundingState,
    decimal? RequestedMarginEur,
    decimal? RequestedLeverage,
    decimal? SizedNotionalEur,
    decimal? RequiredMarginEur,
    decimal? EffectiveLeverage);

internal sealed record CycleQueryFilters(
    string? WorkerVersion,
    string? WorkerCommit,
    string? StrategyVersion,
    string? ChangeSet,
    string? BotInstanceId,
    bool LatestStrategy,
    bool LatestMeta);

internal sealed record CycleDetailDto(
    string CycleId,
    DateTime Utc,
    CycleRecordDto Record);

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
    IReadOnlyDictionary<string, int> RejectionCounts,
    IReadOnlyList<CycleTopCandidateDto> TopCandidates,
    IReadOnlyList<CycleExcludedPairDto> ExcludedPairs,
    int? PriceActionReadyCount);

internal sealed record CycleTopCandidateDto(
    string Pair,
    decimal Score,
    string DesiredPosition,
    decimal SpreadPercent,
    decimal Price,
    string? RejectionReason);

internal sealed record CycleExcludedPairDto(
    string Pair,
    string Reason,
    decimal Last,
    decimal ChangePercent,
    int? VolumeRank,
    decimal? Est24hVolumeEur,
    decimal? SpreadPercent,
    int? AdvisorRank);

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

// ── Simulation engine ──

internal sealed record SimulationParams(
    string BotInstanceId,
    int LastHours,
    double Spread,
    double Score,
    double Sl,
    double Tp,
    int Hourly,
    int Group,
    bool BtcFilter,
    double Notional,
    double Fee,
    HashSet<string> Exclude,
    bool ShowTrades);

internal sealed record SimulationResult(
    SimulationParams Config,
    DateTimeOffset Utc,
    int CyclesProcessed,
    string? WindowStart,
    string? WindowEnd,
    int TradeCount,
    int Wins,
    int Losses,
    double WinRate,
    double ProfitFactor,
    double TotalPnl,
    double AvgPerTrade,
    IReadOnlyList<SimTradeSummary> Trades,
    IReadOnlyList<SimPairPnl> PnlByPair,
    IReadOnlyList<SimRegimePnl> PnlByRegime,
    string? Warning);

internal sealed record SimTradeSummary(
    string Pair,
    string Exit,
    double Pct,
    double Eur,
    string EntryTime,
    string ExitTime,
    string Regime,
    double Score,
    double EntryPrice,
    double ExitPrice);

internal sealed record SimPairPnl(string Pair, double Eur, int Trades, int Wins);
internal sealed record SimRegimePnl(string Regime, double Eur, int Trades, int Wins);

partial class Program
{
    private sealed class SimCycle
    {
        public string Utc { get; init; } = "";
        public long TimestampMs { get; init; }
        public string Regime { get; init; } = "UNKNOWN";
        public List<SimDecision> Decisions { get; init; } = new();
        public Dictionary<string, double> Prices { get; init; } = new();
    }

    private sealed class SimDecision
    {
        public string Pair { get; init; } = "";
        public double Price { get; init; }
        public double Score { get; init; }
        public double SpreadPercent { get; init; }
        public string? EntryRejectionReason { get; init; }
        public string? Action { get; init; }
        public string? Reason { get; init; }
        public string? CorrelationGroup { get; init; }
    }

    private sealed class SimPosition
    {
        public double Entry { get; init; }
        public string Group { get; init; } = "OTHER";
        public string Regime { get; init; } = "UNKNOWN";
        public string EntryTime { get; init; } = "";
        public long EntryTimestampMs { get; init; }
        public double Score { get; init; }
        public string Side { get; init; } = "LONG";
        public decimal PeakPnlPercent { get; set; }
        public int ConsecutiveLowScoreCycles { get; set; }
        public decimal? StopLossPrice { get; init; }
        public decimal? TakeProfitPrice { get; init; }
    }

    private static string ParseRegimeState(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "UNKNOWN";
        var match = System.Text.RegularExpressions.Regex.Match(raw, @"state=(\w+)");
        return match.Success ? match.Groups[1].Value : "UNKNOWN";
    }

    static async Task<SimulationResult> RunSimulation(
        NpgsqlConnection connection,
        SimulationParams sim,
        CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-sim.LastHours);

        // Load cycles with decisions.
        var cycles = new List<SimCycle>();
        var cycleIdMap = new Dictionary<string, int>();
        await using (var cmd = new NpgsqlCommand(
            """
            select cycle.cycle_id, cycle.utc, coalesce(diagnostics.btc_regime_state, '')
            from dry_run_cycle_facts cycle
            left join dry_run_cycle_entry_diagnostic_facts diagnostics on diagnostics.cycle_id = cycle.cycle_id
            where cycle.bot_instance_id = @bot
              and utc >= @cutoff
            order by utc asc
            """, connection))
        {
            cmd.Parameters.AddWithValue("bot", sim.BotInstanceId);
            cmd.Parameters.Add("cutoff", NpgsqlDbType.TimestampTz).Value = cutoff;

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var cycleId = reader.GetString(0);
                var utc = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);
                var utcStr = utc.ToString("O", CultureInfo.InvariantCulture);
                var timestampMs = new DateTimeOffset(utc).ToUnixTimeMilliseconds();
                cycleIdMap[cycleId] = cycles.Count;
                cycles.Add(new SimCycle
                {
                    Utc = utcStr, TimestampMs = timestampMs,
                    Regime = ParseRegimeState(reader.IsDBNull(2) ? null : reader.GetString(2)),
                    Decisions = new List<SimDecision>(),
                    Prices = new Dictionary<string, double>()
                });
            }
        }

        // Load normalized decisions/actions for those cycles.
        {
            await using var cmd = new NpgsqlCommand(
                """
                select
                    cycle.cycle_id,
                    decision.pair,
                    decision.price,
                    decision.score,
                    decision.spread_percent,
                    decision.entry_rejection_reason,
                    action.action,
                    action.reason,
                    action.correlation_group
                from dry_run_cycle_facts cycle
                join dry_run_decision_facts decision on decision.cycle_id = cycle.cycle_id
                join dry_run_actions action on action.cycle_id = decision.cycle_id and action.decision_index = decision.decision_index
                where cycle.bot_instance_id = @bot
                  and cycle.utc >= @cutoff
                order by cycle.utc asc, decision.decision_index asc
                """, connection);
            cmd.Parameters.AddWithValue("bot", sim.BotInstanceId);
            cmd.Parameters.Add("cutoff", NpgsqlDbType.TimestampTz).Value = cutoff;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!cycleIdMap.TryGetValue(reader.GetString(0), out var ci) || ci >= cycles.Count)
                {
                    continue;
                }

                var pair = reader.GetString(1);
                var price = (double)reader.GetDecimal(2);
                if (price > 0)
                {
                    cycles[ci].Prices[pair] = price;
                }

                cycles[ci].Decisions.Add(new SimDecision
                {
                    Pair = pair,
                    Price = price,
                    Score = (double)reader.GetDecimal(3),
                    SpreadPercent = reader.IsDBNull(4) ? 99 : (double)reader.GetDecimal(4),
                    EntryRejectionReason = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Action = reader.IsDBNull(6) ? null : reader.GetString(6),
                    Reason = reader.IsDBNull(7) ? null : reader.GetString(7),
                    CorrelationGroup = reader.IsDBNull(8) ? null : reader.GetString(8)
                });
            }
        }

        // Also keep prices for pairs that were excluded before detailed decisions.
        {
            await using var cmd = new NpgsqlCommand(
                """
                select excluded.cycle_id, excluded.pair, excluded.last
                from dry_run_excluded_pairs excluded
                join dry_run_cycle_facts cycle on cycle.cycle_id = excluded.cycle_id
                where cycle.bot_instance_id = @bot
                  and cycle.utc >= @cutoff
                """, connection);
            cmd.Parameters.AddWithValue("bot", sim.BotInstanceId);
            cmd.Parameters.Add("cutoff", NpgsqlDbType.TimestampTz).Value = cutoff;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (cycleIdMap.TryGetValue(reader.GetString(0), out var ci) && ci < cycles.Count)
                {
                    var pair = reader.GetString(1);
                    var last = (double)reader.GetDecimal(2);
                    if (last > 0 && !cycles[ci].Prices.ContainsKey(pair))
                    {
                        cycles[ci].Prices[pair] = last;
                    }
                }
            }
        }

        // Load snapshot prices per cycle
        {
            await using var cmd = new NpgsqlCommand(
                """
                select cycle_id, pair, last
                from market_snapshots
                where bot_instance_id = @bot
                  and utc >= @cutoff
                """, connection);
            cmd.Parameters.AddWithValue("bot", sim.BotInstanceId);
            cmd.Parameters.Add("cutoff", NpgsqlDbType.TimestampTz).Value = cutoff;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var cid = reader.GetString(0);
                if (cycleIdMap.TryGetValue(cid, out var ci) && ci < cycles.Count)
                {
                    var pair = reader.GetString(1);
                    var last = (double)reader.GetDecimal(2);
                    if (!cycles[ci].Prices.ContainsKey(pair))
                        cycles[ci].Prices[pair] = last;
                }
            }
        }

        if (cycles.Count == 0)
        {
            return new SimulationResult(sim, DateTimeOffset.UtcNow, 0, null, null,
                0, 0, 0, 0, 0, 0, 0,
                Array.Empty<SimTradeSummary>(), Array.Empty<SimPairPnl>(),
                Array.Empty<SimRegimePnl>(), "No cycles found in the selected time window.");
        }

        // Build correlation group map from all decisions
        var groupMap = new Dictionary<string, string>();
        foreach (var c in cycles)
            foreach (var d in c.Decisions)
                if (!string.IsNullOrEmpty(d.CorrelationGroup))
                    groupMap[d.Pair] = d.CorrelationGroup;

        var isFutures = sim.BotInstanceId.Contains("futures", StringComparison.OrdinalIgnoreCase);
        var feeFrac = sim.Fee / 100.0;

        var exitOptions = new PositionExitOptions
        {
            FixedStopLossPercent = (decimal)sim.Sl,
            FixedTakeProfitPercent = (decimal)sim.Tp,
        };
        var execPolicy = new ExecutionPolicyOptions();

        var open = new Dictionary<string, SimPosition>();
        var entryStamps = new List<long>();
        var tradeResults = new List<SimTradeSummary>();

        for (int ci = 0; ci < cycles.Count; ci++)
        {
            var cycle = cycles[ci];

            // Build per-pair score lookup for this cycle
            var cycleScores = new Dictionary<string, double>();
            var cycleDesiredLong = new HashSet<string>();
            foreach (var d in cycle.Decisions)
            {
                cycleScores[d.Pair] = d.Score;
                if (d.Action is "WOULD_BUY" or "WOULD_OPEN_LONG" or "WOULD_BUY_BLOCKED" or "HOLD")
                    cycleDesiredLong.Add(d.Pair);
            }

            // Check exits using real PositionExitPolicy
            foreach (var pair in open.Keys.ToList())
            {
                if (!cycle.Prices.TryGetValue(pair, out var p)) continue;
                var pos = open[pair];
                var currentPrice = (decimal)p;
                var entryPrice = (decimal)pos.Entry;

                var pnlPct = (currentPrice / entryPrice - 1m) * 100m;
                if (pnlPct > pos.PeakPnlPercent)
                    pos.PeakPnlPercent = pnlPct;

                var positionAgeSeconds = (cycle.TimestampMs - pos.EntryTimestampMs) / 1000.0;
                var currentScore = cycleScores.GetValueOrDefault(pair, pos.Score);
                var desiredLong = cycleDesiredLong.Contains(pair);
                var scoreConfirmsEntry = currentScore >= sim.Score;

                if (isFutures)
                {
                    // Futures: frozen SL/TP levels set at entry, checked each cycle
                    string? exitType = null;
                    if (pos.StopLossPrice is { } sl && currentPrice <= sl) exitType = "SL";
                    else if (pos.TakeProfitPrice is { } tp && currentPrice >= tp) exitType = "TP";
                    if (exitType != null)
                    {
                        var pct = (double)((currentPrice / entryPrice - 1m) * 100m) - feeFrac * 100;
                        var eur = sim.Notional * pct / 100;
                        tradeResults.Add(new SimTradeSummary(pair, exitType, Math.Round(pct, 2), Math.Round(eur, 4),
                            pos.EntryTime, cycle.Utc, pos.Regime, pos.Score, pos.Entry, p));
                        open.Remove(pair);
                    }
                }
                else
                {
                    // Spot: full tiered exit policy from real bot
                    if ((decimal)currentScore <= exitOptions.ScoreDecayDefensiveScore)
                        pos.ConsecutiveLowScoreCycles++;
                    else
                        pos.ConsecutiveLowScoreCycles = 0;

                    var scoreDecay = new ScoreDecaySnapshot(
                        EntryScore: (decimal)pos.Score,
                        CurrentScore: (decimal)currentScore,
                        ConsecutiveLowScoreCycles: pos.ConsecutiveLowScoreCycles,
                        ScoreConfirmsEntry: scoreConfirmsEntry);

                    var exitLevels = new PositionExitLevelsSnapshot(
                        pos.Side,
                        pos.StopLossPrice,
                        pos.TakeProfitPrice,
                        currentPrice);

                    var evaluation = PositionExitPolicy.EvaluateHeldPosition(
                        desiredLong: desiredLong,
                        positionAgeSeconds: positionAgeSeconds,
                        conservativeUnrealizedPnlPercent: pnlPct,
                        canValuePosition: true,
                        killSwitchActive: false,
                        executionPolicy: execPolicy,
                        positionExit: exitOptions,
                        peakPnlPercent: pos.PeakPnlPercent,
                        exitHysteresisEnabled: false,
                        scoreDecay: scoreDecay,
                        recentPriceActionNegative: pnlPct < -0.5m,
                        exitLevels: exitLevels);

                    if (evaluation.ShouldSell)
                    {
                        var exitCode = evaluation.ExitReason is { } er
                            ? PositionExitPolicy.ExitReasonCode(er)
                            : "SELL_SIGNAL_FLIP";
                        var pct = (double)pnlPct - feeFrac * 100;
                        var eur = sim.Notional * pct / 100;
                        tradeResults.Add(new SimTradeSummary(pair, exitCode, Math.Round(pct, 2), Math.Round(eur, 4),
                            pos.EntryTime, cycle.Utc, pos.Regime, pos.Score, pos.Entry, p));
                        open.Remove(pair);
                    }
                }
            }

            // BTC regime gate
            if (sim.BtcFilter && cycle.Regime == "DOWNTREND") continue;

            // Entry candidates
            var candidates = new Dictionary<string, SimDecision>();
            foreach (var d in cycle.Decisions)
            {
                if (sim.Exclude.Contains(d.Pair.ToUpperInvariant())) continue;

                var want = false;
                if (d.EntryRejectionReason == "REJECT_SPREAD_TOO_WIDE" && d.SpreadPercent <= sim.Spread)
                    want = true;
                if (d.Action == "WOULD_BUY" || d.Action == "WOULD_OPEN_LONG")
                    want = true;
                if (d.Action == "WOULD_BUY_BLOCKED")
                {
                    var r = d.Reason ?? "";
                    if (r.Contains("early-entry", StringComparison.OrdinalIgnoreCase)) want = false;
                    else if (r.Contains("btc-regime", StringComparison.OrdinalIgnoreCase)) want = false;
                    else want = true;
                }

                if (want && d.Score >= sim.Score)
                {
                    if (!candidates.ContainsKey(d.Pair) || d.Score > candidates[d.Pair].Score)
                        candidates[d.Pair] = d;
                }
            }

            var sorted = candidates.Values.OrderByDescending(c => c.Score);
            foreach (var c in sorted)
            {
                if (open.ContainsKey(c.Pair)) continue;

                var grp = groupMap.GetValueOrDefault(c.Pair, $"UNGROUPED:{c.Pair}");
                var grpCount = open.Values.Count(p => p.Group == grp);
                if (grpCount >= sim.Group) continue;

                var recentEntries = entryStamps.Count(t => cycle.TimestampMs - t < 3_600_000);
                if (recentEntries >= sim.Hourly) continue;

                if (!cycle.Prices.TryGetValue(c.Pair, out var price) || price <= 0) continue;

                var entryDecimal = (decimal)price;
                var slPrice = entryDecimal * (1m - (decimal)sim.Sl / 100m);
                var tpPrice = entryDecimal * (1m + (decimal)sim.Tp / 100m);

                open[c.Pair] = new SimPosition
                {
                    Entry = price,
                    Group = grp,
                    Regime = cycle.Regime,
                    EntryTime = cycle.Utc,
                    EntryTimestampMs = cycle.TimestampMs,
                    Score = c.Score,
                    Side = "LONG",
                    StopLossPrice = slPrice,
                    TakeProfitPrice = tpPrice,
                };
                entryStamps.Add(cycle.TimestampMs);
            }
        }

        // Close remaining at last prices
        var lastPrices = cycles[^1].Prices;
        foreach (var (pair, pos) in open)
        {
            if (!lastPrices.TryGetValue(pair, out var p)) continue;
            var pct = (p / pos.Entry - 1 - feeFrac) * 100;
            var eur = sim.Notional * pct / 100;
            tradeResults.Add(new SimTradeSummary(pair, "END", Math.Round(pct, 2), Math.Round(eur, 4),
                pos.EntryTime, cycles[^1].Utc, pos.Regime, pos.Score, pos.Entry, p));
        }

        // Aggregations
        var pnlByPair = tradeResults
            .GroupBy(t => t.Pair)
            .Select(g => new SimPairPnl(g.Key, Math.Round(g.Sum(t => t.Eur), 4), g.Count(), g.Count(t => t.Eur >= 0)))
            .OrderByDescending(p => p.Eur)
            .ToList();

        var pnlByRegime = tradeResults
            .GroupBy(t => t.Regime)
            .Select(g => new SimRegimePnl(g.Key, Math.Round(g.Sum(t => t.Eur), 4), g.Count(), g.Count(t => t.Eur >= 0)))
            .OrderByDescending(p => p.Eur)
            .ToList();

        var returnedTrades = sim.ShowTrades ? tradeResults : Array.Empty<SimTradeSummary>().ToList();

        var totalCount = tradeResults.Count;
        var wins = tradeResults.Count(t => t.Eur >= 0);
        var losses = totalCount - wins;
        var grossProfit = tradeResults.Where(t => t.Eur >= 0).Sum(t => t.Eur);
        var grossLoss = tradeResults.Where(t => t.Eur < 0).Sum(t => -t.Eur);
        var totalPnl = tradeResults.Sum(t => t.Eur);

        return new SimulationResult(
            sim, DateTimeOffset.UtcNow, cycles.Count,
            cycles[0].Utc, cycles[^1].Utc,
            totalCount, wins, losses,
            totalCount > 0 ? (double)wins / totalCount * 100 : 0,
            grossLoss > 0 ? grossProfit / grossLoss : grossProfit > 0 ? 9999.0 : 0,
            Math.Round(totalPnl, 4),
            totalCount > 0 ? Math.Round(totalPnl / totalCount, 4) : 0,
            returnedTrades, pnlByPair, pnlByRegime, null);
    }
}

// Per-instance cache for the drawdown scan; top-level statements cannot hold
// static state, so it lives here next to the schema flag.
internal static class DrawdownCache
{
    public static readonly Dictionary<string, (decimal Percent, DateTimeOffset At)> Values = new();
    public static readonly SemaphoreSlim Gate = new(1, 1);
}

internal static class DashboardSchema
{
    // Set once the dashboard's own tables have been declared in this process.
    // A racing second request just repeats an idempotent statement.
    public static volatile bool Ready;
}

internal static class DashboardDefaults
{
    public const int EquityWindowDays = 30;

    // Nothing on the page reads earlier than this. The chart is trimmed to it in the
    // browser, but max drawdown arrives as one finished number, so it was still being
    // measured from peaks that predate the launch: futures-live reported -65.1% off a
    // peak the page never draws. Measured from the same start, the figure means what
    // the reader thinks it means.
    public const string LaunchLocalDate = "2026-08-19";

    // Bump when the daily rollup's computation changes: stored days from an older
    // revision are dropped and rebuilt on the next read.
    public const int RollupRevision = 5;
}

internal readonly record struct DecisionKey(string CycleId, int DecisionIndex);

internal sealed record DashboardResponse(
    DateTimeOffset Utc,
    string? BotInstanceId,
    PortfolioSummaryDto? Summary,
    IReadOnlyList<PortfolioPositionDto> Positions,
    IReadOnlyDictionary<string, DashboardEntryDto> EntryContexts,
    IReadOnlyList<DashboardWorkerDto> Workers,
    DashboardEquityDto Equity,
    DashboardTodayDto Today,
    DashboardRatesDto? Rates,
    string? Warning);

internal sealed record DashboardSignalDto(string Name, decimal Value, string Reason);

internal sealed record DashboardEntryDto(
    string Pair,
    DateTime Utc,
    string Action,
    string? Side,
    decimal? Leverage,
    decimal Score,
    string? EntryChannel,
    string? Log,
    decimal FeeEur,
    IReadOnlyList<DashboardSignalDto> Signals,
    IReadOnlyList<string> RiskReasons,
    decimal SpreadPercent,
    string? PriceActionDirection,
    decimal? PriceActionTrendPercent,
    decimal? EmaGapPercent,
    string? FillSource,
    bool Exploratory,
    decimal? ScoreThreshold);

internal sealed record DashboardWorkerDto(
    string BotInstanceId,
    string? BotInstanceName,
    DateTime LatestCycleUtc,
    int LatestCycleAgeSeconds,
    string RuntimeState,
    bool IsStale,
    string MarketDataMode,
    int ActivePairsCount);

internal sealed record DashboardEquityDayDto(
    string Date,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal ManualAdjustmentEur,
    long Cycles,
    // Transfers made on this day but before the bot's first cycle. Zero on almost
    // every day; on the first day of a series it is the capital the account began
    // with, which the chart must not attribute to the bot.
    decimal PreWindowEur,
    // How long the account went unobserved before this day's first cycle. Null on the
    // first day of the series, which has nothing before it.
    double? GapMinutes);

internal sealed record DashboardEquityDto(
    string TimeZone,
    string TodayLocalDate,
    IReadOnlyList<DashboardEquityDayDto> Days,
    decimal ManualAdjustmentEur,
    bool ManualAdjustmentsTracked,
    DashboardDayResultDto? Yesterday,
    DashboardDayResultDto? BestDay,
    decimal MaxDrawdownPercent)
{
    public static DashboardEquityDto Empty() =>
        new("Europe/Vilnius", string.Empty, Array.Empty<DashboardEquityDayDto>(), 0m, false, null, null, 0m);
}

// One day's result with money movement stripped out: Close is where the day would
// have ended on the bot's own trading alone.
internal sealed record DashboardDayResultDto(
    string Date,
    decimal Open,
    decimal Close,
    decimal ManualAdjustmentEur,
    decimal BotEur,
    decimal BotPercent,
    // The most margin the bot held at once that day, and what the day's result is
    // against it. Percent of the portfolio flatters a day the bot barely traded:
    // the same +18.54 is 31% of a 60 USD account and 124% of the 15 it actually
    // committed. Null when nothing was open, which leaves the page on the portfolio
    // figure rather than dividing by zero.
    decimal? PeakMarginEur,
    decimal? MarginPercent,
    int ClosedTrades);

internal sealed record DashboardTradeDto(
    DateTime Utc,
    string Pair,
    string Action,
    string? Side,
    decimal? Leverage,
    decimal FillPrice,
    decimal Quantity,
    decimal FeeEur,
    decimal Score,
    decimal TargetNotionalEur,
    decimal? RealizedPnlEur,
    decimal? RealizedPnlPercent,
    string? ExitReasonCode,
    string? ExitTriggerSource,
    string? EntryChannel,
    string? ExchangeOrderId,
    string Log,
    bool ReduceOnly,
    IReadOnlyList<DashboardSignalDto> Signals,
    IReadOnlyList<string> RiskReasons,
    decimal SpreadPercent,
    string? PriceActionDirection,
    decimal? PriceActionTrendPercent,
    decimal? EmaGapPercent,
    string? FillSource,
    bool Exploratory,
    decimal? ScoreThreshold);

internal sealed record DashboardRatesDto(
    decimal BykoUsd,
    decimal LukoUsd,
    decimal LukoInByko,
    decimal BykoInLuko,
    long BlockNumber,
    DateTime ObservedAt,
    bool Stale);

internal sealed record DashboardTodayDto(
    string LocalDate,
    string TimeZone,
    IReadOnlyList<DashboardTradeDto> Trades,
    int Opened,
    int Closed,
    int StopLoss,
    int TakeProfit,
    decimal RealizedPnlEur)
{
    public static DashboardTodayDto Empty() =>
        new(string.Empty, "Europe/Vilnius", Array.Empty<DashboardTradeDto>(), 0, 0, 0, 0, 0m);
}

// Coin cross rate for the hero. The pools live on Base, and the public RPCs
// rate-limit hard — seven calls from one address already drew a 429 — so this is
// read server-side, at most once every few minutes, and cached in Postgres.
// The stored row is also the fallback: when the chain cannot be reached the last
// observation is served and flagged stale, rather than the line going blank.
internal static class CoinRates
{
    public const string Usdc = "0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913";

    // Two pools, no direct LUKO/BYKO market: the cross rate is the ratio of the
    // two dollar prices.
    public static readonly (string Symbol, string Token, string Pool)[] Markets =
    {
        ("BYKO", "0x078bB16e24c8931Fc007928c370422e5e38F4372", "0x02dd4285ad38ea93d021ca854016a839b0b2a6ca"),
        ("LUKO", "0x4a9DA2831A691E7C4aca594CaFd58c35e0131fD1", "0x2222a01b83db8c533b062aeb6de4f61d6ae792f2")
    };

    // Rotated on failure: one endpoint throttling must not take the rate down.
    // A keyed endpoint (dRPC, Alchemy, whatever) goes first when
    // TRADINGBOT_BASE_RPC_URL is set — the key stays in the environment, next to
    // the database credentials, and never in the repository.
    public static readonly string[] Endpoints = BuildEndpoints();

    private static string[] BuildEndpoints()
    {
        var free = new[]
        {
            "https://base-rpc.publicnode.com",
            "https://mainnet.base.org",
            "https://base.drpc.org",
            "https://1rpc.io/base"
        };

        var configured = Environment.GetEnvironmentVariable("TRADINGBOT_BASE_RPC_URL");
        return string.IsNullOrWhiteSpace(configured)
            ? free
            : new[] { configured.Trim() }.Concat(free).ToArray();
    }

    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    // Beyond this the figure is still shown, but marked stale for the page.
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(6);

    // A single long-lived client; the default one closes sockets too eagerly for
    // a process that makes a handful of calls every few minutes.
    public static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        // mainnet.base.org answers 403 to a bare programmatic agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BlynAI-dashboard/1.0");
        return client;
    }

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task EnsureTableAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            create table if not exists token_rate_state (
                symbol text primary key,
                price_usdc numeric not null,
                reserve_token numeric not null,
                reserve_usdc numeric not null,
                pool_address text not null,
                block_number bigint not null,
                observed_at timestamptz not null,
                updated_at timestamptz not null default now()
            )
            """,
            connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<DashboardRatesDto?> ReadAsync(
        string connectionString,
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await EnsureTableAsync(connection, cancellationToken);

        var stored = await LoadAsync(connection, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        if (stored is null || now - stored.UpdatedAt > RefreshInterval)
        {
            // One refresh at a time; concurrent pollers wait for the same result
            // rather than each hitting the rate limit.
            if (await Gate.WaitAsync(0, cancellationToken))
            {
                try
                {
                    var fresh = await FetchAsync(cancellationToken);
                    if (fresh is not null)
                    {
                        await SaveAsync(connection, fresh, cancellationToken);
                        stored = await LoadAsync(connection, cancellationToken);
                    }
                    else if (stored is not null)
                    {
                        // Touch nothing: keeping the old observed_at is what makes
                        // the staleness visible on the page.
                        Console.WriteLine("coin-rates: refresh failed, serving last known");
                    }
                }
                finally
                {
                    Gate.Release();
                }
            }
        }

        if (stored is null)
        {
            return null;
        }

        var byko = stored.Prices.TryGetValue("BYKO", out var b) ? b : 0m;
        var luko = stored.Prices.TryGetValue("LUKO", out var l) ? l : 0m;
        if (byko <= 0m || luko <= 0m)
        {
            return null;
        }

        return new DashboardRatesDto(
            byko,
            luko,
            luko / byko,
            byko / luko,
            stored.BlockNumber,
            stored.ObservedAt.UtcDateTime,
            now - stored.ObservedAt > StaleAfter);
    }

    private sealed record StoredRates(
        Dictionary<string, decimal> Prices,
        long BlockNumber,
        DateTimeOffset ObservedAt,
        DateTimeOffset UpdatedAt);

    private static async Task<StoredRates?> LoadAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select symbol, price_usdc, block_number, observed_at, updated_at from token_rate_state",
            connection);

        var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        long block = 0;
        DateTimeOffset observed = default, updated = default;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            prices[reader.GetString(0)] = reader.GetDecimal(1);
            block = Math.Max(block, reader.GetInt64(2));
            var rowObserved = reader.GetFieldValue<DateTimeOffset>(3);
            var rowUpdated = reader.GetFieldValue<DateTimeOffset>(4);
            if (rowObserved > observed) observed = rowObserved;
            if (rowUpdated > updated) updated = rowUpdated;
        }

        return prices.Count == 0 ? null : new StoredRates(prices, block, observed, updated);
    }

    private static async Task SaveAsync(
        NpgsqlConnection connection,
        IReadOnlyList<(string Symbol, decimal Price, decimal Token, decimal UsdcSide, string Pool, long Block, DateTimeOffset At)> rows,
        CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            await using var command = new NpgsqlCommand(
                """
                insert into token_rate_state
                    (symbol, price_usdc, reserve_token, reserve_usdc, pool_address, block_number, observed_at, updated_at)
                values (@symbol, @price, @reserve_token, @reserve_usdc, @pool, @block, @observed_at, now())
                on conflict (symbol) do update set
                    price_usdc = excluded.price_usdc,
                    reserve_token = excluded.reserve_token,
                    reserve_usdc = excluded.reserve_usdc,
                    pool_address = excluded.pool_address,
                    block_number = excluded.block_number,
                    observed_at = excluded.observed_at,
                    updated_at = now()
                """,
                connection);
            command.Parameters.Add("symbol", NpgsqlDbType.Text).Value = row.Symbol;
            command.Parameters.Add("price", NpgsqlDbType.Numeric).Value = row.Price;
            command.Parameters.Add("reserve_token", NpgsqlDbType.Numeric).Value = row.Token;
            command.Parameters.Add("reserve_usdc", NpgsqlDbType.Numeric).Value = row.UsdcSide;
            command.Parameters.Add("pool", NpgsqlDbType.Text).Value = row.Pool;
            command.Parameters.Add("block", NpgsqlDbType.Bigint).Value = row.Block;
            command.Parameters.Add("observed_at", NpgsqlDbType.TimestampTz).Value = row.At;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<List<(string, decimal, decimal, decimal, string, long, DateTimeOffset)>?> FetchAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var block = await RpcAsync("eth_getBlockByNumber", "[\"latest\", false]", cancellationToken);
            if (block is null) return null;

            var blockNumber = FromHex(block.Value.GetProperty("number").GetString());
            var observedAt = DateTimeOffset.FromUnixTimeSeconds(
                (long)FromHex(block.Value.GetProperty("timestamp").GetString()));

            var rows = new List<(string, decimal, decimal, decimal, string, long, DateTimeOffset)>();
            foreach (var (symbol, _, pool) in Markets)
            {
                // token0() tells which side of the pair is USDC.
                var token0 = await CallAsync(pool, "0x0dfe1681", cancellationToken);
                var reserves = await CallAsync(pool, "0x0902f1ac", cancellationToken);
                if (token0 is null || reserves is null) return null;

                var usdcIsFirst = token0.EndsWith(Usdc[2..], StringComparison.OrdinalIgnoreCase);
                var first = Word(reserves, 0);
                var second = Word(reserves, 1);
                var usdcRaw = usdcIsFirst ? first : second;
                var tokenRaw = usdcIsFirst ? second : first;

                // USDC carries 6 decimals, both coins 18.
                var usdc = (decimal)usdcRaw / 1_000_000m;
                var token = (decimal)tokenRaw / 1_000_000_000_000_000_000m;
                if (usdc <= 0m || token <= 0m) return null;

                rows.Add((symbol, usdc / token, token, usdc, pool, (long)blockNumber, observedAt));
            }

            return rows;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"coin-rates: fetch failed ({ex.Message})");
            return null;
        }
    }

    private static async Task<string?> CallAsync(string to, string data, CancellationToken cancellationToken)
    {
        var result = await RpcAsync(
            "eth_call",
            $"[{{\"to\":\"{to}\",\"data\":\"{data}\"}}, \"latest\"]",
            cancellationToken);
        return result?.GetString();
    }

    private static async Task<JsonElement?> RpcAsync(string method, string paramsJson, CancellationToken cancellationToken)
    {
        foreach (var endpoint in Endpoints)
        {
            try
            {
                var payload = $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"{method}\",\"params\":{paramsJson}}}";
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await Http.PostAsync(endpoint, content, cancellationToken);
                if (!response.IsSuccessStatusCode) continue;

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out _)) continue;
                if (!doc.RootElement.TryGetProperty("result", out var result)) continue;

                return result.Clone();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // Try the next endpoint.
            }
        }

        return null;
    }

    private static ulong FromHex(string? value) =>
        string.IsNullOrWhiteSpace(value) ? 0UL : Convert.ToUInt64(value[2..], 16);

    private static System.Numerics.BigInteger Word(string data, int index)
    {
        var slice = data.AsSpan(2 + index * 64, 64);
        return System.Numerics.BigInteger.Parse("0" + slice.ToString(), NumberStyles.HexNumber);
    }
}
