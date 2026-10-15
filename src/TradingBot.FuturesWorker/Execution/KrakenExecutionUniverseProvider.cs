namespace TradingBot.FuturesWorker;

// BYKO execution adapter: shared reference-data implementation, unchanged cache policy.
internal sealed class KrakenExecutionUniverseProvider(
    HttpClient http, KrakenOptions options, IReadOnlyList<InstrumentOptions> configured,
    IReadOnlyList<string> rulePairs, IClock? clock = null)
    : KrakenContractRegistry(http, options, configured, rulePairs, clock);
