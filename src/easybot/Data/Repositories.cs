using Dapper;
using EasyBot.Trading;

namespace EasyBot.Data;

public sealed record TradeRecord(
    long Id,
    DateTime OpenedAt,
    DateTime? ClosedAt,
    string Pair,
    string Side,
    decimal Size,
    decimal EntryPrice,
    decimal? ExitPrice,
    decimal StopPrice,
    decimal? Pnl,
    decimal? Fee,
    string? CloseReason);

public sealed record CandleRecord(string Pair, string Timeframe, DateTime OpenTime, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

public sealed record BotEventRecord(long Id, DateTime Ts, string Level, string Message, string? Data);

public interface ITradeRepository
{
    Task<long> OpenTradeAsync(DateTime openedAt, string pair, PositionSide side, decimal size, decimal entryPrice, decimal stopPrice, CancellationToken ct);
    Task CloseTradeAsync(long tradeId, DateTime closedAt, decimal exitPrice, decimal? pnl, decimal? fee, string closeReason, CancellationToken ct);
    Task<IReadOnlyList<TradeRecord>> GetRecentAsync(int count, CancellationToken ct);
}

public sealed class TradeRepository : ITradeRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public TradeRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<long> OpenTradeAsync(DateTime openedAt, string pair, PositionSide side, decimal size, decimal entryPrice, decimal stopPrice, CancellationToken ct)
    {
        await using var connection = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(
            """
            INSERT INTO trades (opened_at, pair, side, size, entry_price, stop_price)
            VALUES (@openedAt, @pair, @side, @size, @entryPrice, @stopPrice)
            RETURNING id
            """,
            new { openedAt, pair, side = side.ToString(), size, entryPrice, stopPrice },
            cancellationToken: ct);
        return await connection.ExecuteScalarAsync<long>(command);
    }

    public async Task CloseTradeAsync(long tradeId, DateTime closedAt, decimal exitPrice, decimal? pnl, decimal? fee, string closeReason, CancellationToken ct)
    {
        await using var connection = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(
            """
            UPDATE trades
            SET closed_at = @closedAt, exit_price = @exitPrice, pnl = @pnl, fee = @fee, close_reason = @closeReason
            WHERE id = @tradeId
            """,
            new { tradeId, closedAt, exitPrice, pnl, fee, closeReason },
            cancellationToken: ct);
        await connection.ExecuteAsync(command);
    }

    public async Task<IReadOnlyList<TradeRecord>> GetRecentAsync(int count, CancellationToken ct)
    {
        await using var connection = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(
            """
            SELECT id AS Id, opened_at AS OpenedAt, closed_at AS ClosedAt, pair AS Pair, side AS Side,
                   size AS Size, entry_price AS EntryPrice, exit_price AS ExitPrice, stop_price AS StopPrice,
                   pnl AS Pnl, fee AS Fee, close_reason AS CloseReason
            FROM trades
            ORDER BY opened_at DESC
            LIMIT @count
            """,
            new { count },
            cancellationToken: ct);
        var rows = await connection.QueryAsync<TradeRecord>(command);
        return rows.AsList();
    }
}

public interface ICandleRepository
{
    Task UpsertAsync(string pair, string timeframe, ExchangeCandle candle, CancellationToken ct);
    Task<IReadOnlyList<CandleRecord>> GetRecentAsync(string pair, string timeframe, int count, CancellationToken ct);
}

public sealed class CandleRepository : ICandleRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public CandleRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task UpsertAsync(string pair, string timeframe, ExchangeCandle candle, CancellationToken ct)
    {
        await using var connection = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(
            """
            INSERT INTO candles (pair, timeframe, open_time, o, h, l, c, volume)
            VALUES (@pair, @timeframe, @openTime, @o, @h, @l, @c, @volume)
            ON CONFLICT (pair, timeframe, open_time)
            DO UPDATE SET o = EXCLUDED.o, h = EXCLUDED.h, l = EXCLUDED.l, c = EXCLUDED.c, volume = EXCLUDED.volume
            """,
            new { pair, timeframe, openTime = candle.OpenTime, o = candle.Open, h = candle.High, l = candle.Low, c = candle.Close, volume = candle.Volume },
            cancellationToken: ct);
        await connection.ExecuteAsync(command);
    }

    public async Task<IReadOnlyList<CandleRecord>> GetRecentAsync(string pair, string timeframe, int count, CancellationToken ct)
    {
        await using var connection = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(
            """
            SELECT pair AS Pair, timeframe AS Timeframe, open_time AS OpenTime,
                   o AS Open, h AS High, l AS Low, c AS Close, volume AS Volume
            FROM candles
            WHERE pair = @pair AND timeframe = @timeframe
            ORDER BY open_time DESC
            LIMIT @count
            """,
            new { pair, timeframe, count },
            cancellationToken: ct);
        var rows = await connection.QueryAsync<CandleRecord>(command);
        return rows.OrderBy(r => r.OpenTime).ToList();
    }
}

public interface IBotEventRepository
{
    Task LogAsync(string level, string message, string? dataJson = null, CancellationToken ct = default);
    Task<IReadOnlyList<BotEventRecord>> GetRecentAsync(int count, CancellationToken ct);
}

public sealed class BotEventRepository : IBotEventRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public BotEventRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task LogAsync(string level, string message, string? dataJson = null, CancellationToken ct = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(
            "INSERT INTO bot_events (level, message, data) VALUES (@level, @message, @dataJson::jsonb)",
            new { level, message, dataJson },
            cancellationToken: ct);
        await connection.ExecuteAsync(command);
    }

    public async Task<IReadOnlyList<BotEventRecord>> GetRecentAsync(int count, CancellationToken ct)
    {
        await using var connection = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(
            """
            SELECT id AS Id, ts AS Ts, level AS Level, message AS Message, data::text AS Data
            FROM bot_events
            ORDER BY ts DESC
            LIMIT @count
            """,
            new { count },
            cancellationToken: ct);
        var rows = await connection.QueryAsync<BotEventRecord>(command);
        return rows.AsList();
    }
}

public interface IAppStateRepository
{
    Task<string?> GetAsync(string key, CancellationToken ct);
    Task SetAsync(string key, string value, CancellationToken ct);
}

public sealed class AppStateRepository : IAppStateRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public AppStateRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        await using var connection = _connectionFactory.CreateConnection();
        var command = new CommandDefinition("SELECT value FROM app_state WHERE key = @key", new { key }, cancellationToken: ct);
        return await connection.ExecuteScalarAsync<string?>(command);
    }

    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        await using var connection = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(
            """
            INSERT INTO app_state (key, value) VALUES (@key, @value)
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value
            """,
            new { key, value },
            cancellationToken: ct);
        await connection.ExecuteAsync(command);
    }
}
