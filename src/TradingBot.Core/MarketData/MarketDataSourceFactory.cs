namespace TradingBot.Core.MarketData;

public static class MarketDataSourceFactory
{
    public static (IMarketDataSource Source, IUniverseProvider Universe) Create(
        HttpClient httpClient,
        KrakenOptions kraken,
        UniverseDiscoveryOptions universeDiscovery,
        IReadOnlyList<InstrumentOptions> configuredUniverse,
        DatabaseOptions database,
        MarketDataConsumerOptions consumer,
        int timeframeMinutes)
    {
        var mode = kraken.MarketDataMode?.Trim() ?? "sample";
        var venue = MarketDataVenue.Normalize(consumer.Venue);

        if (mode.Equals("database", StringComparison.OrdinalIgnoreCase)
            && database.Enabled
            && !string.IsNullOrWhiteSpace(database.ConnectionString))
        {
            var store = new PostgresMarketDataStore(database.ConnectionString);
            store.EnsureSchema();
            var venueConsumer = CloneConsumer(consumer, venue);
            var fallback = CreateDirectSource(httpClient, kraken, venue);
            var source = new DatabaseMarketDataSource(store, venueConsumer, fallback);
            var universe = new DatabaseUniverseProvider(store, venueConsumer, configuredUniverse);
            return (source, universe);
        }

        return (
            CreateDirectSource(httpClient, kraken, venue),
            CreateDirectUniverse(httpClient, kraken, universeDiscovery, configuredUniverse, venue));
    }

    public static IMarketDataSource CreateDirectSource(HttpClient httpClient, KrakenOptions kraken, string venue)
    {
        if (venue == MarketDataVenue.Futures)
        {
            return kraken.MarketDataMode.Equals("kraken-futures", StringComparison.OrdinalIgnoreCase)
                || kraken.MarketDataMode.Equals("database", StringComparison.OrdinalIgnoreCase)
                ? new KrakenFuturesMarketDataSource(httpClient, kraken)
                : new SampleMarketDataSource();
        }

        return kraken.MarketDataMode.Equals("kraken", StringComparison.OrdinalIgnoreCase)
            || kraken.MarketDataMode.Equals("database", StringComparison.OrdinalIgnoreCase)
            ? new KrakenMarketDataSource(httpClient, kraken)
            : new SampleMarketDataSource();
    }

    public static IUniverseProvider CreateDirectUniverse(
        HttpClient httpClient,
        KrakenOptions kraken,
        UniverseDiscoveryOptions universeDiscovery,
        IReadOnlyList<InstrumentOptions> configuredUniverse,
        string venue) =>
        venue == MarketDataVenue.Futures
            ? kraken.MarketDataMode.Equals("kraken-futures", StringComparison.OrdinalIgnoreCase)
                || kraken.MarketDataMode.Equals("database", StringComparison.OrdinalIgnoreCase)
                ? new KrakenFuturesUniverseProvider(httpClient, kraken, universeDiscovery, configuredUniverse)
                : new ConfiguredUniverseProvider(configuredUniverse)
            : kraken.MarketDataMode.Equals("kraken", StringComparison.OrdinalIgnoreCase)
                || kraken.MarketDataMode.Equals("database", StringComparison.OrdinalIgnoreCase)
                ? new KrakenSpotUniverseProvider(httpClient, kraken, universeDiscovery, configuredUniverse)
                : new ConfiguredUniverseProvider(configuredUniverse);

    private static MarketDataConsumerOptions CloneConsumer(MarketDataConsumerOptions source, string venue) =>
        new()
        {
            Venue = venue,
            FallbackEnabled = source.FallbackEnabled,
            MaxQuoteAgeSeconds = source.MaxQuoteAgeSeconds,
            MaxCandleAgeMinutes = source.MaxCandleAgeMinutes,
            MaxCandleBars = source.MaxCandleBars
        };
}
