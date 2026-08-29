using CryptoExchange.Net.Objects;
using Kraken.Net.Enums;
using Kraken.Net.Interfaces.Clients;

namespace EasyBot.Trading;

public sealed record ExchangeCandle(DateTime OpenTime, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

public sealed record ExchangePosition(string Symbol, PositionSide Side, decimal Quantity, decimal EntryPrice, DateTime FillTime, decimal? UnrealizedPnl);

public sealed record ExchangeOrder(
    string OrderId,
    string? ClientOrderId,
    string Symbol,
    OrderSide Side,
    FuturesOrderType Type,
    decimal Quantity,
    decimal? Price,
    decimal? StopPrice,
    bool ReduceOnly);

public interface IExchangeClient
{
    Task<IReadOnlyList<ExchangeCandle>> GetCandlesAsync(string pair, string timeframe, int limit, CancellationToken ct);
    Task<ExchangePosition?> GetOpenPositionAsync(string pair, CancellationToken ct);
    Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string pair, CancellationToken ct);
    Task<decimal> GetEquityAsync(CancellationToken ct);
    Task<string> PlaceMarketOrderAsync(string pair, OrderSide side, decimal quantity, bool reduceOnly, CancellationToken ct);
    Task<string> PlaceStopOrderAsync(string pair, OrderSide side, decimal quantity, decimal stopPrice, bool reduceOnly, CancellationToken ct);
    Task UpdateStopOrderAsync(string orderId, decimal stopPrice, CancellationToken ct);
    Task CancelOrderAsync(string orderId, CancellationToken ct);
}

/// <summary>
/// Thin wrapper over KrakenExchange.Net's FuturesApi. Every exchange call is retried up to
/// 3 times with exponential backoff (1s, 2s, 4s); if all retries fail the original exception
/// is thrown so callers (TradingWorker) can log and skip the iteration.
/// </summary>
public sealed class ExchangeClient : IExchangeClient
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    private readonly IKrakenRestClient _client;
    private readonly ILogger<ExchangeClient> _logger;

    public ExchangeClient(IKrakenRestClient client, ILogger<ExchangeClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ExchangeCandle>> GetCandlesAsync(string pair, string timeframe, int limit, CancellationToken ct)
    {
        var interval = TimeframeMapper.ToKlineInterval(timeframe);
        var result = await ExecuteWithRetryAsync(
            "GetKlines",
            innerCt => _client.FuturesApi.ExchangeData.GetKlinesAsync(TickType.Trade, pair, interval, limit: limit, ct: innerCt),
            ct);

        return result.Klines
            .Select(k => new ExchangeCandle(k.Timestamp, k.OpenPrice, k.HighPrice, k.LowPrice, k.ClosePrice, k.Volume))
            .OrderBy(c => c.OpenTime)
            .ToList();
    }

    public async Task<ExchangePosition?> GetOpenPositionAsync(string pair, CancellationToken ct)
    {
        var positions = await ExecuteWithRetryAsync(
            "GetOpenPositions",
            innerCt => _client.FuturesApi.Trading.GetOpenPositionsAsync(innerCt),
            ct);

        var position = positions.FirstOrDefault(p => string.Equals(p.Symbol, pair, StringComparison.OrdinalIgnoreCase));
        if (position is null)
            return null;

        var side = position.Side == Kraken.Net.Enums.PositionSide.Long ? PositionSide.Long : PositionSide.Short;
        return new ExchangePosition(position.Symbol, side, position.Quantity, position.Price, position.FillTime, position.UnrealizedPnl);
    }

    public async Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string pair, CancellationToken ct)
    {
        var orders = await ExecuteWithRetryAsync(
            "GetOpenOrders",
            innerCt => _client.FuturesApi.Trading.GetOpenOrdersAsync(innerCt),
            ct);

        return orders
            .Where(o => string.Equals(o.Symbol, pair, StringComparison.OrdinalIgnoreCase))
            .Select(o => new ExchangeOrder(o.OrderId, o.ClientOrderId, o.Symbol, o.Side, o.Type, o.Quantity, o.Price, o.StopPrice, o.ReduceOnly))
            .ToList();
    }

    public async Task<decimal> GetEquityAsync(CancellationToken ct)
    {
        var balances = await ExecuteWithRetryAsync(
            "GetBalances",
            innerCt => _client.FuturesApi.Account.GetBalancesAsync(innerCt),
            ct);

        return balances.CashAccount?.Balances?.Values.Sum() ?? 0m;
    }

    public async Task<string> PlaceMarketOrderAsync(string pair, OrderSide side, decimal quantity, bool reduceOnly, CancellationToken ct)
    {
        var result = await ExecuteWithRetryAsync(
            "PlaceMarketOrder",
            innerCt => _client.FuturesApi.Trading.PlaceOrderAsync(pair, side, FuturesOrderType.Market, quantity, reduceOnly: reduceOnly, ct: innerCt),
            ct);

        return result.OrderId;
    }

    public async Task<string> PlaceStopOrderAsync(string pair, OrderSide side, decimal quantity, decimal stopPrice, bool reduceOnly, CancellationToken ct)
    {
        var result = await ExecuteWithRetryAsync(
            "PlaceStopOrder",
            innerCt => _client.FuturesApi.Trading.PlaceOrderAsync(
                pair, side, FuturesOrderType.Stop, quantity, stopPrice: stopPrice, reduceOnly: reduceOnly, triggerSignal: TriggerSignal.Mark, ct: innerCt),
            ct);

        return result.OrderId;
    }

    public async Task UpdateStopOrderAsync(string orderId, decimal stopPrice, CancellationToken ct)
    {
        await ExecuteWithRetryAsync(
            "UpdateStopOrder",
            innerCt => _client.FuturesApi.Trading.EditOrderAsync(orderId: orderId, stopPrice: stopPrice, ct: innerCt),
            ct);
    }

    public async Task CancelOrderAsync(string orderId, CancellationToken ct)
    {
        await ExecuteWithRetryAsync(
            "CancelOrder",
            innerCt => _client.FuturesApi.Trading.CancelOrderAsync(orderId: orderId, ct: innerCt),
            ct);
    }

    private async Task<T> ExecuteWithRetryAsync<T>(
        string operationName,
        Func<CancellationToken, Task<HttpResult<T>>> call,
        CancellationToken ct)
    {
        Exception? lastError = null;

        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await call(ct);
                if (result.Success)
                    return result.Data!;

                lastError = new InvalidOperationException($"{operationName} failed: {result.Error}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }

            if (attempt < RetryDelays.Length)
            {
                _logger.LogWarning(lastError, "{Operation} attempt {Attempt} failed, retrying in {Delay}", operationName, attempt + 1, RetryDelays[attempt]);
                await Task.Delay(RetryDelays[attempt], ct);
            }
        }

        _logger.LogError(lastError, "{Operation} failed after {Attempts} attempts", operationName, RetryDelays.Length + 1);
        throw lastError ?? new InvalidOperationException($"{operationName} failed for an unknown reason.");
    }
}

public static class TimeframeMapper
{
    public static FuturesKlineInterval ToKlineInterval(string timeframe) => timeframe switch
    {
        "1m" => FuturesKlineInterval.OneMinute,
        "5m" => FuturesKlineInterval.FiveMinutes,
        "15m" => FuturesKlineInterval.FifteenMinutes,
        "30m" => FuturesKlineInterval.ThirtyMinutes,
        "1h" => FuturesKlineInterval.OneHour,
        "4h" => FuturesKlineInterval.FourHours,
        "12h" => FuturesKlineInterval.TwelfHours,
        "1d" => FuturesKlineInterval.OneDay,
        "1w" => FuturesKlineInterval.OneWeek,
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, "Unsupported timeframe.")
    };

    public static TimeSpan ToTimeSpan(string timeframe) => timeframe switch
    {
        "1m" => TimeSpan.FromMinutes(1),
        "5m" => TimeSpan.FromMinutes(5),
        "15m" => TimeSpan.FromMinutes(15),
        "30m" => TimeSpan.FromMinutes(30),
        "1h" => TimeSpan.FromHours(1),
        "4h" => TimeSpan.FromHours(4),
        "12h" => TimeSpan.FromHours(12),
        "1d" => TimeSpan.FromDays(1),
        "1w" => TimeSpan.FromDays(7),
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, "Unsupported timeframe.")
    };
}
