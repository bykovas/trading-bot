using System.Diagnostics;

namespace TradingBot.MarketDataWorker;

internal sealed class MarketDataIngestionWorker(
    MarketDataWorkerConfiguration config,
    IMarketDataStore store,
    IUniverseProvider spotUniverseProvider,
    IUniverseProvider futuresUniverseProvider,
    IMarketDataSource spotMarketDataSource,
    IMarketDataSource futuresMarketDataSource,
    IClock? clock = null)
{
    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private DateTimeOffset _nextCandleRefreshUtc = DateTimeOffset.MinValue;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!config.Database.Enabled || string.IsNullOrWhiteSpace(config.Database.ConnectionString))
        {
            throw new InvalidOperationException("Market data worker requires TRADINGBOT_DATABASE_ENABLED=true and a connection string.");
        }

        store.EnsureSchema();
        Console.WriteLine(
            $"market-data-worker start light={config.Ingestion.LightIntervalSeconds}s candles={config.Ingestion.CandleIntervalSeconds}s timeframe={config.Ingestion.TimeframeMinutes}m maxPairs={config.Ingestion.MaxCandlePairs}");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var utc = _clock.UtcNow;
                await RefreshLightAsync(MarketDataVenue.Spot, spotUniverseProvider, spotMarketDataSource, utc, cancellationToken);
                await RefreshLightAsync(MarketDataVenue.Futures, futuresUniverseProvider, futuresMarketDataSource, utc, cancellationToken);

                if (utc >= _nextCandleRefreshUtc)
                {
                    await RefreshCandlesAsync(MarketDataVenue.Spot, spotUniverseProvider, spotMarketDataSource, utc, cancellationToken);
                    await RefreshCandlesAsync(MarketDataVenue.Futures, futuresUniverseProvider, futuresMarketDataSource, utc, cancellationToken);
                    _nextCandleRefreshUtc = utc.AddSeconds(config.Ingestion.CandleIntervalSeconds);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"market-data-worker cycle FAILED: {ex.Message}");
            }

            if (config.Worker.RunOnce)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(config.Ingestion.LightIntervalSeconds), cancellationToken);
        }
    }

    private async Task RefreshLightAsync(
        string venue,
        IUniverseProvider universeProvider,
        IMarketDataSource marketDataSource,
        DateTimeOffset utc,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var universe = await universeProvider.GetUniverseAsync(cancellationToken);
        var instruments = universe.Instruments.Where(instrument => instrument.Enabled).ToList();
        store.UpsertInstruments(instruments.Select(instrument => new InstrumentRegistryRecord(
            venue,
            instrument.Pair,
            instrument.KrakenPair,
            instrument.Enabled,
            utc,
            instrument.QuantityDecimals,
            instrument.PriceDecimals)).ToList());

        var lightStates = await marketDataSource.GetLightMarketStatesAsync(instruments, cancellationToken);
        var quotes = lightStates
            .Where(state => state.Quote is not null)
            .Select(state => new SharedMarketQuoteRecord(
                venue,
                state.Instrument.Pair,
                utc,
                state.Quote!.Bid,
                state.Quote.Ask,
                state.Quote.Last,
                state.Quote.VolumeToday,
                state.Quote.ChangePercent ?? state.ChangePercent,
                state.Quote.FundingRatePercent,
                state.Quote.MarkPrice,
                state.Quote.IndexPrice))
            .ToList();
        store.UpsertQuotes(quotes);

        store.AppendCycle(new MarketDataCycleRecord(
            $"market-data-{venue}-light-{utc:yyyyMMddHHmmss}",
            venue,
            utc,
            instruments.Count,
            quotes.Count,
            0,
            (int)stopwatch.ElapsedMilliseconds,
            universe.Diagnostics.Warning));

        Console.WriteLine(
            $"market-data light venue={venue} quotes={quotes.Count} universe={instruments.Count} durationMs={stopwatch.ElapsedMilliseconds}");
    }

    private async Task RefreshCandlesAsync(
        string venue,
        IUniverseProvider universeProvider,
        IMarketDataSource marketDataSource,
        DateTimeOffset utc,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var universe = await universeProvider.GetUniverseAsync(cancellationToken);
        var instruments = universe.Instruments.Where(instrument => instrument.Enabled).ToList();
        var lightStates = await marketDataSource.GetLightMarketStatesAsync(instruments, cancellationToken);
        var heldPairs = store.LoadHeldPairs(venue);
        var selected = CandlePairSelector.Select(venue, instruments, lightStates, heldPairs, config.Ingestion);
        var fullStates = await marketDataSource.GetFullMarketStatesAsync(
            selected,
            config.Ingestion.TimeframeMinutes,
            lightStates,
            cancellationToken);

        var candleRecords = new List<SharedMarketCandleRecord>();
        var orderBooks = new List<SharedMarketOrderBookRecord>();
        foreach (var state in fullStates)
        {
            foreach (var candle in state.Candles)
            {
                candleRecords.Add(new SharedMarketCandleRecord(
                    venue,
                    state.Instrument.Pair,
                    config.Ingestion.TimeframeMinutes,
                    candle.OpenTime,
                    candle.Open,
                    candle.High,
                    candle.Low,
                    candle.Close,
                    candle.Volume));
            }

            if (state.OrderBook is not null)
            {
                orderBooks.Add(new SharedMarketOrderBookRecord(
                    venue,
                    state.Instrument.Pair,
                    utc,
                    state.OrderBook.Bids,
                    state.OrderBook.Asks));
            }
        }

        store.UpsertCandles(candleRecords, config.Ingestion.TimeframeMinutes);
        store.UpsertOrderBooks(orderBooks);
        store.AppendCycle(new MarketDataCycleRecord(
            $"market-data-{venue}-candles-{utc:yyyyMMddHHmmss}",
            venue,
            utc,
            instruments.Count,
            lightStates.Count(state => state.Quote is not null),
            selected.Count,
            (int)stopwatch.ElapsedMilliseconds,
            $"held={heldPairs.Count} selected={string.Join(',', selected.Select(instrument => instrument.Pair))}"));

        Console.WriteLine(
            $"market-data candles venue={venue} pairs={selected.Count} candles={candleRecords.Count} held={heldPairs.Count} durationMs={stopwatch.ElapsedMilliseconds}");
    }
}
