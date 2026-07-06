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
}
