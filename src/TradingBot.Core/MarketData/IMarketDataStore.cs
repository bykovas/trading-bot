namespace TradingBot.Core.MarketData;

public interface IMarketDataStore
{
    void EnsureSchema();

    void UpsertInstruments(IReadOnlyList<InstrumentRegistryRecord> instruments);

    void UpsertQuotes(IReadOnlyList<SharedMarketQuoteRecord> quotes);

    void UpsertCandles(IReadOnlyList<SharedMarketCandleRecord> candles, int timeframeMinutes);

    void UpsertStrategyCandles(IReadOnlyList<SharedStrategyCandleRecord> candles, int timeframeMinutes);

    void AppendLiquidations(IReadOnlyList<SharedLiquidationRecord> liquidations);

    void SetStreamState(string venue, string stream, bool connected, DateTimeOffset utc, bool recordGap);

    void UpsertOrderBooks(IReadOnlyList<SharedMarketOrderBookRecord> orderBooks);

    void AppendCycle(MarketDataCycleRecord cycle);

    IReadOnlyList<InstrumentRegistryRecord> LoadInstruments(string venue);

    IReadOnlyList<SharedMarketQuoteRecord> LoadQuotes(string venue, DateTimeOffset minUtc);

    IReadOnlyList<SharedMarketCandleRecord> LoadCandles(
        string venue,
        string pair,
        int timeframeMinutes,
        int maxBars);

    IReadOnlyList<SharedStrategyCandleRecord> LoadStrategyCandles(
        string venue,
        string pair,
        int timeframeMinutes,
        int maxBars);

    SharedLiquidationWindow LoadLiquidationWindow(
        string venue,
        string pair,
        DateTimeOffset fromExclusive,
        DateTimeOffset toInclusive);

    SharedMarketOrderBookRecord? LoadOrderBook(string venue, string pair);

    IReadOnlyList<string> LoadHeldPairs(string venue);

    DateTimeOffset? LoadLatestQuoteUtc(string venue);
}
