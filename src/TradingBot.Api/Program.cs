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

app.MapGet("/api/portfolio", async (CancellationToken cancellationToken) =>
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

        var summary = await ReadSummary(connection, cancellationToken);
        var positions = await ReadPositions(connection, cancellationToken);

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

app.MapGet("/api/positions", async (CancellationToken cancellationToken) =>
{
    var connectionString = GetConnectionString(builder.Configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem("TRADINGBOT_DATABASE_CONNECTION_STRING is not configured.");
    }

    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    return Results.Ok(await ReadPositions(connection, cancellationToken));
});

app.MapGet("/api/cycles", async (
    int? limit,
    int? offset,
    string? workerVersion,
    string? workerCommit,
    string? strategyVersion,
    string? changeSet,
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

app.MapGet("/api/trade-cycles", async (int? limit, int? offset, CancellationToken cancellationToken) =>
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

    var items = await ReadTradeCycles(connection, window.UtcStart, page, cancellationToken);
    return Results.Ok(new TradeCyclesResponse(
        items,
        page.Limit,
        page.Offset,
        items.Count == page.Limit ? page.Offset + page.Limit : null,
        window.LocalStartDate,
        window.LocalTimeZone));
});

app.MapGet("/api/decisions", async (string? cycleId, int? limit, int? offset, CancellationToken cancellationToken) =>
{
    var connectionString = GetConnectionString(builder.Configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem("TRADINGBOT_DATABASE_CONNECTION_STRING is not configured.");
    }

    var page = PageRequest.Create(limit, offset);
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    var items = await ReadDecisions(connection, cycleId, page, cancellationToken);
    return Results.Ok(new PageResponse<DecisionSummaryDto>(
        items,
        page.Limit,
        page.Offset,
        items.Count == page.Limit ? page.Offset + page.Limit : null));
});

app.MapGet("/api/entry-diagnostics", async (string? cycleId, int? limit, int? offset, CancellationToken cancellationToken) =>
{
    var connectionString = GetConnectionString(builder.Configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem("TRADINGBOT_DATABASE_CONNECTION_STRING is not configured.");
    }

    var page = PageRequest.Create(limit, offset);
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    var items = await ReadEntryDiagnostics(connection, cycleId, page, cancellationToken);
    return Results.Ok(new PageResponse<CycleEntryDiagnosticsDto>(
        items,
        page.Limit,
        page.Offset,
        items.Count == page.Limit ? page.Offset + page.Limit : null));
});

app.MapGet("/api/market-snapshots", async (
    string? cycleId,
    string? pair,
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

    var items = await ReadMarketSnapshots(connection, cycleId, pair, page, cancellationToken);
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
    var utcStart = TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone);
    return new TradeWindow(utcStart, localStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), timeZoneId);
}

static async Task<PortfolioSummaryDto?> ReadSummary(NpgsqlConnection connection, CancellationToken cancellationToken)
{
    await using var command = new NpgsqlCommand(
        """
        select
            updated_at,
            cash_eur,
            positions_value_eur,
            total_value_eur,
            open_positions,
            daily_risk_date_utc,
            daily_realized_pnl_eur
        from portfolio_summary
        order by updated_at desc
        limit 1
        """,
        connection);

    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return null;
    }

    return new PortfolioSummaryDto(
        reader.GetDateTime(0),
        reader.GetDecimal(1),
        reader.GetDecimal(2),
        reader.GetDecimal(3),
        reader.GetInt32(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetDecimal(6));
}

static async Task<IReadOnlyList<PortfolioPositionDto>> ReadPositions(NpgsqlConnection connection, CancellationToken cancellationToken)
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
            last_action_at_utc
        from portfolio_positions
        order by market_value_eur desc, pair
        """,
        connection);

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
            reader.IsDBNull(10) ? null : reader.GetDateTime(10)));
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
    command.Parameters.Add("worker_commit", NpgsqlDbType.Text).Value = (object?)filters.WorkerCommit ?? DBNull.Value;
    command.Parameters.Add("strategy_version", NpgsqlDbType.Text).Value = (object?)filters.StrategyVersion ?? DBNull.Value;
    command.Parameters.Add("change_set", NpgsqlDbType.Text).Value = (object?)filters.ChangeSet ?? DBNull.Value;
    command.Parameters.Add("latest_strategy", NpgsqlDbType.Boolean).Value = filters.LatestStrategy;

    var cycles = new List<CycleRawDto>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        using var document = JsonDocument.Parse(reader.GetString(8));
        cycles.Add(new CycleRawDto(
            reader.GetString(0),
            reader.GetDateTime(1),
            ReadNullableString(reader, 2),
            ReadNullableString(reader, 3),
            ReadNullableString(reader, 4),
            ReadNullableString(reader, 5),
            ReadNullableString(reader, 6),
            ReadNullableString(reader, 7),
            document.RootElement.Clone()));
    }

    return cycles;
}

static async Task EnsureCycleMetadataColumns(NpgsqlConnection connection, CancellationToken cancellationToken)
{
    await using var command = new NpgsqlCommand(
        """
        alter table dry_run_cycles
            add column if not exists worker_version text,
            add column if not exists worker_commit text,
            add column if not exists worker_build_utc text,
            add column if not exists worker_image_tag text,
            add column if not exists strategy_version text,
            add column if not exists change_set text;

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
    DateTime utcStart,
    PageRequest page,
    CancellationToken cancellationToken)
{
    await using var command = new NpgsqlCommand(
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
        where utc >= @utc_start
          and (
              record_json::text like '%WOULD_BUY%'
              or record_json::text like '%WOULD_SELL%'
          )
        order by utc desc, cycle_id desc
        limit @limit offset @offset
        """,
        connection);
    command.Parameters.Add("utc_start", NpgsqlDbType.TimestampTz).Value = utcStart;
    command.Parameters.AddWithValue("limit", page.Limit);
    command.Parameters.AddWithValue("offset", page.Offset);

    var cycles = new List<CycleRawDto>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        using var document = JsonDocument.Parse(reader.GetString(8));
        cycles.Add(new CycleRawDto(
            reader.GetString(0),
            reader.GetDateTime(1),
            ReadNullableString(reader, 2),
            ReadNullableString(reader, 3),
            ReadNullableString(reader, 4),
            ReadNullableString(reader, 5),
            ReadNullableString(reader, 6),
            ReadNullableString(reader, 7),
            document.RootElement.Clone()));
    }

    return cycles;
}

static async Task<IReadOnlyList<DecisionSummaryDto>> ReadDecisions(
    NpgsqlConnection connection,
    string? cycleId,
    PageRequest page,
    CancellationToken cancellationToken)
{
    await using var command = new NpgsqlCommand(
        """
        select
            cycle_id,
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
            early_entry_suggested_notional_eur
        from dry_run_decisions
        where (@cycle_id is null or cycle_id = @cycle_id)
        order by utc desc, cycle_id desc, pair
        limit @limit offset @offset
        """,
        connection);
    command.Parameters.Add("cycle_id", NpgsqlDbType.Text).Value = (object?)cycleId ?? DBNull.Value;
    command.Parameters.AddWithValue("limit", page.Limit);
    command.Parameters.AddWithValue("offset", page.Offset);

    var decisions = new List<DecisionSummaryDto>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        decisions.Add(new DecisionSummaryDto(
            reader.GetString(0),
            reader.GetDateTime(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetDecimal(5),
            reader.GetDecimal(6),
            reader.GetBoolean(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            GetNullableDecimal(reader, 9),
            GetNullableDecimal(reader, 10),
            GetNullableDecimal(reader, 11),
            GetNullableDecimal(reader, 12),
            GetNullableDecimal(reader, 13),
            GetNullableDecimal(reader, 14),
            GetNullableDecimal(reader, 15),
            GetNullableDecimal(reader, 16),
            reader.IsDBNull(17) ? string.Empty : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.IsDBNull(19) ? null : reader.GetString(19),
            reader.IsDBNull(20) ? null : reader.GetString(20),
            GetNullableDecimal(reader, 21),
            reader.IsDBNull(22) ? null : reader.GetString(22),
            GetNullableDecimal(reader, 23),
            reader.IsDBNull(24) ? null : reader.GetBoolean(24),
            reader.IsDBNull(25) ? null : reader.GetBoolean(25),
            reader.IsDBNull(26) ? null : reader.GetBoolean(26),
            GetNullableDecimal(reader, 27),
            GetNullableDecimal(reader, 28),
            reader.IsDBNull(29) ? null : reader.GetBoolean(29),
            reader.IsDBNull(30) ? null : reader.GetString(30),
            GetNullableDecimal(reader, 31),
            GetNullableDecimal(reader, 32)));
    }

    return decisions;
}

static decimal? GetNullableDecimal(NpgsqlDataReader reader, int ordinal) =>
    reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);

static async Task<IReadOnlyList<CycleEntryDiagnosticsDto>> ReadEntryDiagnostics(
    NpgsqlConnection connection,
    string? cycleId,
    PageRequest page,
    CancellationToken cancellationToken)
{
    await using var command = new NpgsqlCommand(
        """
        select
            cycle_id,
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
        order by utc desc, cycle_id desc
        limit @limit offset @offset
        """,
        connection);
    command.Parameters.Add("cycle_id", NpgsqlDbType.Text).Value = (object?)cycleId ?? DBNull.Value;
    command.Parameters.AddWithValue("limit", page.Limit);
    command.Parameters.AddWithValue("offset", page.Offset);

    var items = new List<CycleEntryDiagnosticsDto>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        items.Add(new CycleEntryDiagnosticsDto(
            reader.GetString(0),
            reader.GetDateTime(1),
            GetNullableInt(reader, 2),
            GetNullableInt(reader, 3),
            GetNullableInt(reader, 4),
            GetNullableInt(reader, 5),
            GetNullableInt(reader, 6),
            GetNullableInt(reader, 7),
            GetNullableInt(reader, 8),
            GetNullableInt(reader, 9),
            GetNullableInt(reader, 10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            ParseJsonOrNull(reader, 13),
            ParseJsonOrNull(reader, 14),
            ParseJsonOrNull(reader, 15),
            GetNullableInt(reader, 16)));
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
    PageRequest page,
    CancellationToken cancellationToken)
{
    await using var command = new NpgsqlCommand(
        """
        select
            cycle_id,
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
        order by utc desc, cycle_id desc, pair
        limit @limit offset @offset
        """,
        connection);
    command.Parameters.Add("cycle_id", NpgsqlDbType.Text).Value = (object?)cycleId ?? DBNull.Value;
    command.Parameters.Add("pair", NpgsqlDbType.Text).Value = (object?)NormalizePairFilter(pair) ?? DBNull.Value;
    command.Parameters.AddWithValue("limit", page.Limit);
    command.Parameters.AddWithValue("offset", page.Offset);

    var snapshots = new List<MarketSnapshotDto>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        snapshots.Add(new MarketSnapshotDto(
            reader.GetString(0),
            reader.GetDateTime(1),
            reader.GetString(2),
            reader.GetDecimal(3),
            reader.GetDecimal(4),
            reader.GetDecimal(5),
            reader.GetDecimal(6),
            reader.GetDecimal(7)));
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
    DateTime? LastActionAtUtc);

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
    DateTime UtcStart,
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
    bool LatestStrategy);

internal sealed record CycleDetailDto(
    string CycleId,
    DateTime Utc,
    JsonElement Record);

internal sealed record DecisionSummaryDto(
    string CycleId,
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
    decimal? EarlyEntrySuggestedNotionalEur);

internal sealed record CycleEntryDiagnosticsDto(
    string CycleId,
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
    DateTime Utc,
    string Pair,
    decimal Bid,
    decimal Ask,
    decimal Last,
    decimal Volume24h,
    decimal ChangePercent);
