namespace TradingBot.Core.MarketData;

public sealed class DatabaseMarketDataSource(
    IMarketDataStore store,
    MarketDataConsumerOptions options,
    IMarketDataSource? fallbackSource = null,
    IClock? clock = null) : IMarketDataSource
{
    private readonly IClock _clock = clock ?? SystemClock.Instance;

    public async Task<IReadOnlyList<InstrumentMarketState>> GetLightMarketStatesAsync(
        IReadOnlyList<InstrumentOptions> instruments,
        CancellationToken cancellationToken)
    {
        var venue = MarketDataVenue.Normalize(options.Venue);
        var minUtc = _clock.UtcNow.AddSeconds(-Math.Max(1, options.MaxQuoteAgeSeconds));
        var quotes = store.LoadQuotes(venue, minUtc)
            .ToDictionary(quote => quote.Pair, StringComparer.OrdinalIgnoreCase);

        if (ShouldFallback(quotes.Count, store.LoadLatestQuoteUtc(venue)))
        {
            if (fallbackSource is not null && options.FallbackEnabled)
            {
                Console.WriteLine($"market-data: database quotes stale/missing for {venue}; falling back to direct Kraken fetch");
                return await fallbackSource.GetLightMarketStatesAsync(instruments, cancellationToken);
            }
        }

        return instruments.Select(instrument =>
        {
            quotes.TryGetValue(instrument.Pair, out var quote);
            return new InstrumentMarketState
            {
                Instrument = instrument,
                Candles = Array.Empty<Candle>(),
                Quote = quote is null
                    ? null
                    : new Quote(
                        quote.Bid,
                        quote.Ask,
                        quote.Last,
                        quote.Volume24h,
                        quote.ChangePercent,
                        quote.FundingRatePercent,
                        quote.MarkPrice,
                        quote.IndexPrice),
                DataWarning = quote is null ? "Shared quote unavailable." : null
            };
        }).ToList();
    }

    public async Task<IReadOnlyList<InstrumentMarketState>> GetFullMarketStatesAsync(
        IReadOnlyList<InstrumentOptions> instruments,
        int timeframeMinutes,
        IReadOnlyList<InstrumentMarketState> lightStates,
        CancellationToken cancellationToken)
    {
        var venue = MarketDataVenue.Normalize(options.Venue);
        var minUtc = _clock.UtcNow.AddSeconds(-Math.Max(1, options.MaxQuoteAgeSeconds));
        var quotes = store.LoadQuotes(venue, minUtc)
            .ToDictionary(quote => quote.Pair, StringComparer.OrdinalIgnoreCase);
        var staleCandlePairs = new List<string>();

        if (ShouldFallback(quotes.Count, store.LoadLatestQuoteUtc(venue)))
        {
            if (fallbackSource is not null && options.FallbackEnabled)
            {
                Console.WriteLine($"market-data: database full fetch stale for {venue}; falling back to direct Kraken fetch");
                return await fallbackSource.GetFullMarketStatesAsync(instruments, timeframeMinutes, lightStates, cancellationToken);
            }
        }

        var states = new List<InstrumentMarketState>();
        foreach (var instrument in instruments)
        {
            quotes.TryGetValue(instrument.Pair, out var quote);
            var candles = store.LoadCandles(venue, instrument.Pair, timeframeMinutes, options.MaxCandleBars)
                .Select(record => new Candle(
                    record.OpenTime,
                    record.Open,
                    record.High,
                    record.Low,
                    record.Close,
                    record.Volume,
                    0))
                .ToList();
            var orderBookRecord = store.LoadOrderBook(venue, instrument.Pair);
            var newestCandleUtc = candles.Count == 0 ? (DateTimeOffset?)null : candles[^1].OpenTime;
            var candleStale = newestCandleUtc is null
                || newestCandleUtc < _clock.UtcNow.AddMinutes(-Math.Max(1, options.MaxCandleAgeMinutes));

            if (candleStale)
            {
                staleCandlePairs.Add(instrument.Pair);
            }

            states.Add(new InstrumentMarketState
            {
                Instrument = instrument,
                Candles = candles,
                Quote = quote is null
                    ? lightStates.FirstOrDefault(state => state.Instrument.Pair.Equals(instrument.Pair, StringComparison.OrdinalIgnoreCase))?.Quote
                    : new Quote(
                        quote.Bid,
                        quote.Ask,
                        quote.Last,
                        quote.Volume24h,
                        quote.ChangePercent,
                        quote.FundingRatePercent,
                        quote.MarkPrice,
                        quote.IndexPrice),
                OrderBook = orderBookRecord is null
                    ? null
                    : new OrderBookSnapshot(orderBookRecord.Bids, orderBookRecord.Asks),
                DataWarning = candleStale
                    ? $"Shared candles stale or missing for {instrument.Pair}."
                    : candles.Count < 30
                        ? $"Only {candles.Count} shared candles returned."
                        : null
            });
        }

        if (staleCandlePairs.Count > 0 && fallbackSource is not null && options.FallbackEnabled)
        {
            Console.WriteLine($"market-data: refreshing {staleCandlePairs.Count} stale pairs from direct Kraken for {venue}");
            var staleInstruments = instruments
                .Where(instrument => staleCandlePairs.Contains(instrument.Pair, StringComparer.OrdinalIgnoreCase))
                .ToList();
            var refreshed = await fallbackSource.GetFullMarketStatesAsync(
                staleInstruments,
                timeframeMinutes,
                lightStates,
                cancellationToken);
            var refreshedLookup = refreshed.ToDictionary(state => state.Instrument.Pair, StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < states.Count; index++)
            {
                var pair = states[index].Instrument.Pair;
                if (refreshedLookup.TryGetValue(pair, out var replacement))
                {
                    states[index] = replacement;
                }
            }
        }

        return states;
    }

    private bool ShouldFallback(int quoteCount, DateTimeOffset? latestQuoteUtc)
    {
        if (quoteCount == 0)
        {
            return true;
        }

        if (latestQuoteUtc is null)
        {
            return true;
        }

        return latestQuoteUtc < _clock.UtcNow.AddSeconds(-Math.Max(1, options.MaxQuoteAgeSeconds));
    }
}
