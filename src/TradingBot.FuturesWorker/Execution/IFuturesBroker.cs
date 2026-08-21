namespace TradingBot.FuturesWorker;

// Futures execution seam. Deliberately NOT shared with ISpotBroker: a spot SELL
// disposes an owned asset, a futures SELL can open a short - the two must never
// share a code path (see .ai/core-spot-futures-blueprint.md).
internal interface IFuturesBroker
{
    bool IsConfigured { get; }

    Task<IReadOnlyList<FuturesAccountBalance>> GetAccountsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<FuturesOpenPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<FuturesOpenOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken);

    Task<FuturesTickerQuote?> GetTickerAsync(string symbol, CancellationToken cancellationToken);

    // Deposits, withdrawals and transfers since the given instant, from the futures
    // account log. Best-effort: an outage returns an empty list.
    // Fills are how the exchange reports what it did without being asked. A position
    // closed by a protection order never passes through this worker, so without them a
    // trailing stop that took profit leaves an entry with no exit in the journal.
    Task<IReadOnlyList<FuturesFill>> GetFillsAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken);

    Task<IReadOnlyList<PortfolioCashEvent>> GetCashEventsAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken);

    // reduceOnly MUST be true for every exit order; leverage applies to entries.
    Task<FuturesOrderResult> SendOrderAsync(
        string symbol,
        string side,
        decimal size,
        bool reduceOnly,
        decimal leverage,
        CancellationToken cancellationToken);

    Task<FuturesOrderResult> SendFillOrKillLimitOrderAsync(
        string symbol,
        string side,
        decimal size,
        decimal limitPrice,
        bool reduceOnly,
        CancellationToken cancellationToken);

    Task<FuturesOrderResult> SendTriggerOrderAsync(
        string symbol,
        string side,
        decimal size,
        string orderType,
        decimal stopPrice,
        string triggerSignal,
        bool reduceOnly,
        CancellationToken cancellationToken);

    Task<FuturesOrderResult> SendTrailingStopOrderAsync(
        string symbol,
        string side,
        decimal size,
        decimal trailingStopPercent,
        string triggerSignal,
        bool reduceOnly,
        CancellationToken cancellationToken);

    Task<FuturesOrderResult> CancelOrderAsync(string orderId, CancellationToken cancellationToken);

    // Sets the per-symbol max leverage (Kraken margin preference). Kraken Futures
    // leverage is NOT an order field, so this MUST be set before an entry order or
    // the position inherits the exchange/account default (often 10x+), posting a
    // fraction of the intended margin. Returns false on failure so the caller can
    // refuse to open at an unknown/wrong leverage.
    Task<bool> SetLeveragePreferenceAsync(string symbol, decimal maxLeverage, CancellationToken cancellationToken);

    // Dead man's switch: cancel all orders if not refreshed within timeoutSeconds.
    Task CancelAllAfterAsync(int timeoutSeconds, CancellationToken cancellationToken);
}

// Name is the key Kraken uses for the wallet ("cash", "flex", "fi_xbtusd", ...).
// It is optional so existing call sites and tests keep compiling; only the live
// broker fills it, and only the diagnostics read it.
// One execution as Kraken reports it. RealizedPnl is populated on the fills that close
// a position, which is exactly the number the journal was missing.
internal sealed record FuturesFill(
    string OrderId,
    string FillId,
    string Symbol,
    string Side,
    decimal Size,
    decimal Price,
    DateTimeOffset FillTime,
    string FillType,
    decimal? RealizedPnl);

internal sealed record FuturesAccountBalance(string Currency, decimal MarginBalance, decimal AvailableMargin, string Name = "");

internal sealed record FuturesOpenPosition(string Symbol, string Side, decimal Size, decimal EntryPrice, decimal MarkPrice, decimal Leverage);

internal sealed record FuturesOpenOrder(
    string OrderId,
    string Symbol,
    string Side,
    string OrderType,
    decimal UnfilledSize,
    decimal? StopPrice,
    bool ReduceOnly,
    decimal? TrailingStopPercent = null);

internal sealed record FuturesTickerQuote(string Symbol, decimal Bid, decimal Ask, decimal Last, decimal? MarkPrice, DateTimeOffset TimestampUtc);

internal sealed record FuturesOrderFill(decimal Quantity, decimal AveragePrice, decimal? Fee, DateTimeOffset? TimestampUtc);

internal sealed record FuturesOrderResult(string Status, string? OrderId, string? Error, FuturesOrderFill? Fill = null)
{
    public bool Accepted =>
        Status.Equals("placed", StringComparison.OrdinalIgnoreCase)
        || Status.Equals("filled", StringComparison.OrdinalIgnoreCase)
        || Status.Equals("executed", StringComparison.OrdinalIgnoreCase)
        || Status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
        || Status.Equals("canceled", StringComparison.OrdinalIgnoreCase);

    public bool FillKnown => Fill is { Quantity: > 0m, AveragePrice: > 0m };

    public static FuturesOrderResult Rejected(string error) => new("REJECTED", null, error);
}
