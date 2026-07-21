namespace TradingBot.SpotWorker;

// Spot execution seam. Deliberately separate from any futures broker
// abstraction: spot SELL disposes an owned asset, futures SELL can open a
// short - the two must never share a code path.
internal interface ISpotBroker
{
    bool IsConfigured { get; }

    Task<IReadOnlyDictionary<string, decimal>> GetBalanceAsync(CancellationToken cancellationToken);

    Task<BrokerOrderResult> AddOrderAsync(
        string krakenPair,
        string side,
        decimal volume,
        bool validate,
        CancellationToken cancellationToken);

    Task<BrokerOrderResult> AddLimitPostOnlyOrderAsync(
        string krakenPair,
        string side,
        decimal volume,
        decimal price,
        bool validate,
        CancellationToken cancellationToken);

    // Immediate-or-cancel limit order: crosses the book up to the given limit price
    // and cancels any unfilled remainder instead of resting. Used only by the BUY
    // taker fallback after a maker miss; the price is a hard slippage cap, never a
    // market order.
    Task<BrokerOrderResult> AddLimitIocOrderAsync(
        string krakenPair,
        string side,
        decimal volume,
        decimal price,
        bool validate,
        CancellationToken cancellationToken);

    Task<BrokerOrderResult> CancelOrderAsync(string txid, CancellationToken cancellationToken);

    // Reads back the exchange's view of a submitted order so the portfolio can be
    // committed with the REAL fill (average price, executed volume, quote cost and
    // fee) instead of the modeled ask/bid+slippage fill. Returns null when the
    // order is unknown or the query failed; callers fall back to the modeled fill.
    Task<BrokerOrderQuery?> QueryOrderAsync(string txid, CancellationToken cancellationToken);

    // Recent executed trades from Kraken TradesHistory. Used to (a) recover a live
    // SELL fill by its ordertxid when QueryOrders could not confirm it, and (b)
    // reconcile the real average cost basis of a spot holding — neither of which a
    // balance-only snapshot can provide. Best-effort: returns an empty list (never
    // throws) when the call fails, so a history outage degrades to the prior
    // balance-only behaviour rather than blocking the trading cycle.
    Task<IReadOnlyList<SpotTradeHistoryEntry>> GetTradeHistoryAsync(CancellationToken cancellationToken);
}

// One executed spot trade as reported by Kraken TradesHistory. Cost and Fee are in
// the quote currency; Pair is Kraken's canonical pair name for the trade.
internal sealed record SpotTradeHistoryEntry(
    string OrderTxId,
    string Pair,
    string Type,
    decimal Price,
    decimal Volume,
    decimal CostQuote,
    decimal FeeQuote,
    DateTimeOffset Time);

// Exchange-reported state of a single order. Cost and Fee are in the quote
// currency (EUR for */EUR pairs); AveragePrice is the volume-weighted fill price.
internal sealed record BrokerOrderQuery(
    string Status,
    decimal VolumeExecuted,
    decimal AveragePrice,
    decimal CostQuote,
    decimal FeeQuote);

// Real execution numbers handed to the portfolio when a live order was confirmed
// filled. Kept separate from BrokerOrderQuery so the portfolio layer never sees
// order-status vocabulary.
internal sealed record LiveOrderFill(
    decimal AveragePrice,
    decimal VolumeExecuted,
    decimal CostEur,
    decimal FeeEur,
    int RepegCount = 0,
    long TimeToFillMs = 0,
    decimal RequestedPrice = 0m);
