namespace TradingBot.FuturesWorker;

// TODO(futures): market data from Kraken Futures public endpoints:
//   GET /derivatives/api/v3/tickers            -> markPrice, last, bid/ask,
//                                                  fundingRate, vol24h per symbol
//   GET /api/charts/v1/trade/{symbol}/{interval} -> OHLC candles
// Symbols use the PF_XBTUSD style; the candidate universe maps Pair -> symbol via
// InstrumentOptions.KrakenPair. Until this is implemented the worker runs on
// Core's SampleMarketDataSource (Program.cs falls back for any MarketDataMode
// other than "kraken-futures").
internal sealed class KrakenFuturesMarketDataSource(HttpClient httpClient, KrakenOptions options) : IMarketDataSource
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly KrakenOptions _options = options;

    public Task<IReadOnlyList<InstrumentMarketState>> GetLightMarketStatesAsync(
        IReadOnlyList<InstrumentOptions> instruments,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Kraken Futures market data not implemented yet; run with MarketDataMode=sample.");

    public Task<IReadOnlyList<InstrumentMarketState>> GetFullMarketStatesAsync(
        IReadOnlyList<InstrumentOptions> instruments,
        int timeframeMinutes,
        IReadOnlyList<InstrumentMarketState> lightStates,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Kraken Futures market data not implemented yet; run with MarketDataMode=sample.");
}
