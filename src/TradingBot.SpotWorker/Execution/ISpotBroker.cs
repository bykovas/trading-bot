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

    Task<BrokerOrderResult> CancelOrderAsync(string txid, CancellationToken cancellationToken);

    // Reads back the exchange's view of a submitted order so the portfolio can be
    // committed with the REAL fill (average price, executed volume, quote cost and
    // fee) instead of the modeled ask/bid+slippage fill. Returns null when the
    // order is unknown or the query failed; callers fall back to the modeled fill.
    Task<BrokerOrderQuery?> QueryOrderAsync(string txid, CancellationToken cancellationToken);
}

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
