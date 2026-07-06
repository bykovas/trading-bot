namespace TradingBot.FuturesWorker;

// Futures execution seam. Deliberately NOT shared with ISpotBroker: a spot SELL
// disposes an owned asset, a futures SELL can open a short - the two must never
// share a code path (see .ai/core-spot-futures-blueprint.md).
internal interface IFuturesBroker
{
    bool IsConfigured { get; }

    Task<IReadOnlyList<FuturesAccountBalance>> GetAccountsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<FuturesOpenPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken);

    // reduceOnly MUST be true for every exit order; leverage applies to entries.
    Task<FuturesOrderResult> SendOrderAsync(
        string symbol,
        string side,
        decimal size,
        bool reduceOnly,
        decimal leverage,
        CancellationToken cancellationToken);

    // Dead man's switch: cancel all orders if not refreshed within timeoutSeconds.
    Task CancelAllAfterAsync(int timeoutSeconds, CancellationToken cancellationToken);
}

internal sealed record FuturesAccountBalance(string Currency, decimal MarginBalance, decimal AvailableMargin);

internal sealed record FuturesOpenPosition(string Symbol, string Side, decimal Size, decimal EntryPrice, decimal MarkPrice, decimal Leverage);

internal sealed record FuturesOrderResult(string Status, string? OrderId, string? Error)
{
    public static FuturesOrderResult Rejected(string error) => new("REJECTED", null, error);
}
