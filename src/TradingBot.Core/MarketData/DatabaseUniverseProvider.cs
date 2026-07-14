namespace TradingBot.Core.MarketData;

public sealed class DatabaseUniverseProvider(
    IMarketDataStore store,
    MarketDataConsumerOptions options,
    IReadOnlyList<InstrumentOptions> configuredFallback) : IUniverseProvider
{
    public Task<UniverseSelection> GetUniverseAsync(CancellationToken cancellationToken)
    {
        var venue = MarketDataVenue.Normalize(options.Venue);
        var registry = store.LoadInstruments(venue);
        if (registry.Count == 0)
        {
            var enabled = configuredFallback.Where(instrument => instrument.Enabled).ToList();
            return Task.FromResult(new UniverseSelection(
                enabled,
                new UniverseSelectionDiagnostics(
                    "database-fallback-configured",
                    0,
                    configuredFallback.Count,
                    enabled.Count,
                    0,
                    "shared instrument registry empty; using configured universe")));
        }

        var instruments = registry.Select(record => new InstrumentOptions
        {
            Pair = record.Pair,
            KrakenPair = record.KrakenSymbol,
            Venue = venue == MarketDataVenue.Futures ? "KrakenFutures" : "Kraken",
            Enabled = record.Enabled,
            QuantityDecimals = record.QuantityDecimals,
            PriceDecimals = record.PriceDecimals
        }).ToList();

        return Task.FromResult(new UniverseSelection(
            instruments,
            new UniverseSelectionDiagnostics(
                "database-instrument-registry",
                registry.Count,
                configuredFallback.Count,
                instruments.Count,
                0,
                null)));
    }
}
