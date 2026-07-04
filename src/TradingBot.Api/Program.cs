using System.Text.Json;
using Npgsql;

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

app.MapGet("/api/cycles", async (int? limit, int? offset, CancellationToken cancellationToken) =>
{
    var connectionString = GetConnectionString(builder.Configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem("TRADINGBOT_DATABASE_CONNECTION_STRING is not configured.");
    }

    var page = PageRequest.Create(limit, offset);
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    var items = await ReadRawCycles(connection, page, cancellationToken);
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

    var cycle = await ReadCycleDetail(connection, cycleId, cancellationToken);
    return cycle is null ? Results.NotFound() : Results.Ok(cycle);
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

app.Run();

static string GetConnectionString(IConfiguration configuration) =>
    Environment.GetEnvironmentVariable("TRADINGBOT_DATABASE_CONNECTION_STRING")
    ?? configuration.GetConnectionString("TradingBot")
    ?? string.Empty;

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
    CancellationToken cancellationToken)
{
    await using var command = new NpgsqlCommand(
        """
        select
            cycle_id,
            utc,
            record_json::text
        from dry_run_cycles
        order by utc desc, cycle_id desc
        limit @limit offset @offset
        """,
        connection);
    command.Parameters.AddWithValue("limit", page.Limit);
    command.Parameters.AddWithValue("offset", page.Offset);

    var cycles = new List<CycleRawDto>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        using var document = JsonDocument.Parse(reader.GetString(2));
        cycles.Add(new CycleRawDto(
            reader.GetString(0),
            reader.GetDateTime(1),
            document.RootElement.Clone()));
    }

    return cycles;
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
            exit_reason_code
        from dry_run_decisions
        where (@cycle_id is null or cycle_id = @cycle_id)
        order by utc desc, cycle_id desc, pair
        limit @limit offset @offset
        """,
        connection);
    command.Parameters.AddWithValue("cycle_id", (object?)cycleId ?? DBNull.Value);
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
            reader.IsDBNull(19) ? null : reader.GetString(19)));
    }

    return decisions;
}

static decimal? GetNullableDecimal(NpgsqlDataReader reader, int ordinal) =>
    reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);

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
    command.Parameters.AddWithValue("cycle_id", (object?)cycleId ?? DBNull.Value);
    command.Parameters.AddWithValue("pair", (object?)NormalizePairFilter(pair) ?? DBNull.Value);
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

internal sealed record CycleRawDto(
    string CycleId,
    DateTime Utc,
    JsonElement Record);

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
    string? ExitReasonCode);

internal sealed record MarketSnapshotDto(
    string CycleId,
    DateTime Utc,
    string Pair,
    decimal Bid,
    decimal Ask,
    decimal Last,
    decimal Volume24h,
    decimal ChangePercent);
