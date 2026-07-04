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
