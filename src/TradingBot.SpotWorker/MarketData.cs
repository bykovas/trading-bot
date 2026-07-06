using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace TradingBot.SpotWorker;

internal interface IMarketDataSource
{
    Task<IReadOnlyList<InstrumentMarketState>> GetLightMarketStatesAsync(
        IReadOnlyList<InstrumentOptions> instruments,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<InstrumentMarketState>> GetFullMarketStatesAsync(
        IReadOnlyList<InstrumentOptions> instruments,
        int timeframeMinutes,
        IReadOnlyList<InstrumentMarketState> lightStates,
        CancellationToken cancellationToken);
}

internal sealed class KrakenMarketDataSource(HttpClient httpClient, KrakenOptions options) : IMarketDataSource
{
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    // Kraken public endpoints are ~1 req/s per IP; OHLC is per-pair. 500ms between
    // OHLC calls keeps a 22-pair active set around ~11s, comfortably inside the loop.
    private static readonly TimeSpan OhlcDelay = TimeSpan.FromMilliseconds(500);
    // Escalating backoffs for repeated 429s: first retry after 2s, second after 5s.
    private static readonly TimeSpan[] RateLimitBackoffs =
    {
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5)
    };

    // AssetPairs is static reference data (altname / wsname / ticker key / trading
    // rules). It is cached for the process lifetime and shared by the light and full
    // paths so we never re-fetch it every cycle. Guarded by a gate because the light
    // and full paths can both trigger a build.
    private readonly Dictionary<string, PairMetadata> _metadataCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _metadataGate = new(1, 1);

    public async Task<IReadOnlyList<InstrumentMarketState>> GetLightMarketStatesAsync(
        IReadOnlyList<InstrumentOptions> instruments,
        CancellationToken cancellationToken)
    {
        var (metadata, metadataWarning) = await TryGetPairMetadataAsync(instruments, cancellationToken);
        var (quotes, quoteWarning) = await TryGetQuotesAsync(instruments, metadata, cancellationToken);
        var batchWarning = CombineWarnings(metadataWarning, quoteWarning);

        return instruments.Select(instrument =>
        {
            quotes.TryGetValue(instrument.KrakenPair, out var quote);
            return new InstrumentMarketState
            {
                Instrument = instrument,
                Candles = Array.Empty<Candle>(),
                Quote = quote,
                DataWarning = quote is not null
                    ? null
                    : batchWarning ?? "Ticker data unavailable."
            };
        }).ToList();
    }

    public async Task<IReadOnlyList<InstrumentMarketState>> GetFullMarketStatesAsync(
        IReadOnlyList<InstrumentOptions> instruments,
        int timeframeMinutes,
        IReadOnlyList<InstrumentMarketState> lightStates,
        CancellationToken cancellationToken)
    {
        var states = new List<InstrumentMarketState>();
        var (metadata, _) = await TryGetPairMetadataAsync(instruments, cancellationToken);
        // Reuse the quotes already fetched for the light snapshot instead of hitting
        // Ticker again seconds later: one fewer call, and advisor + decisions agree.
        var quotes = BuildQuoteLookup(lightStates);

        for (var index = 0; index < instruments.Count; index++)
        {
            var instrument = instruments[index];
            try
            {
                if (index > 0)
                {
                    await Task.Delay(OhlcDelay, cancellationToken);
                }

                metadata.TryGetValue(instrument.KrakenPair, out var pairMetadata);
                quotes.TryGetValue(instrument.KrakenPair, out var quote);
                var candles = await GetClosedCandlesWithRetryAsync(instrument, timeframeMinutes, cancellationToken);
                states.Add(new InstrumentMarketState
                {
                    Instrument = instrument,
                    Candles = candles,
                    PairRules = pairMetadata?.Rules,
                    Quote = quote,
                    DataWarning = candles.Count < 30 ? $"Only {candles.Count} closed candles returned." : null
                });
            }
            catch (Exception ex) when (IsTransient(ex, cancellationToken))
            {
                states.Add(new InstrumentMarketState
                {
                    Instrument = instrument,
                    Candles = Array.Empty<Candle>(),
                    DataWarning = ex.Message
                });
            }
        }

        return states;
    }

    private static Dictionary<string, Quote> BuildQuoteLookup(IReadOnlyList<InstrumentMarketState> lightStates)
    {
        var lookup = new Dictionary<string, Quote>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in lightStates)
        {
            if (state.Quote is not null)
            {
                lookup[state.Instrument.KrakenPair] = state.Quote;
            }
        }

        return lookup;
    }

    // Wraps the AssetPairs fetch so a transient failure degrades to pair-name quote
    // matching (with a warning) instead of crashing the cycle.
    private async Task<(IReadOnlyDictionary<string, PairMetadata> Metadata, string? Warning)> TryGetPairMetadataAsync(
        IReadOnlyList<InstrumentOptions> instruments,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await GetPairMetadataAsync(instruments, cancellationToken), null);
        }
        catch (Exception ex) when (IsTransient(ex, cancellationToken))
        {
            return (_metadataCache, $"assetpairs metadata unavailable ({ex.Message}); falling back to pair-name matching");
        }
    }

    // Wraps the batched Ticker fetch so one transient 5xx/429 (or a whole-batch
    // EQuery) degrades to empty quotes + a warning instead of throwing up the chain.
    private async Task<(Dictionary<string, Quote> Quotes, string? Warning)> TryGetQuotesAsync(
        IReadOnlyList<InstrumentOptions> instruments,
        IReadOnlyDictionary<string, PairMetadata> metadata,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await GetQuotesAsync(instruments, metadata, cancellationToken), null);
        }
        catch (Exception ex) when (IsTransient(ex, cancellationToken))
        {
            return (new Dictionary<string, Quote>(StringComparer.OrdinalIgnoreCase), $"ticker batch failed: {ex.Message}");
        }
    }

    private static string? CombineWarnings(string? first, string? second) =>
        (first, second) switch
        {
            (null, null) => null,
            (not null, null) => first,
            (null, not null) => second,
            _ => $"{first}; {second}"
        };

    private async Task<IReadOnlyDictionary<string, PairMetadata>> GetPairMetadataAsync(
        IReadOnlyList<InstrumentOptions> instruments,
        CancellationToken cancellationToken)
    {
        var missing = instruments.Where(instrument => !_metadataCache.ContainsKey(instrument.KrakenPair)).ToList();
        if (missing.Count == 0)
        {
            return _metadataCache;
        }

        await _metadataGate.WaitAsync(cancellationToken);
        try
        {
            missing = instruments.Where(instrument => !_metadataCache.ContainsKey(instrument.KrakenPair)).ToList();
            if (missing.Count > 0)
            {
                foreach (var entry in await FetchPairMetadataAsync(missing, cancellationToken))
                {
                    _metadataCache[entry.Key] = entry.Value;
                }
            }
        }
        finally
        {
            _metadataGate.Release();
        }

        return _metadataCache;
    }

    private async Task<Dictionary<string, PairMetadata>> FetchPairMetadataAsync(
        IReadOnlyList<InstrumentOptions> instruments,
        CancellationToken cancellationToken)
    {
        if (instruments.Count == 0)
        {
            return new Dictionary<string, PairMetadata>(StringComparer.OrdinalIgnoreCase);
        }

        var pairs = string.Join(",", instruments.Select(instrument => instrument.KrakenPair));
        var uri = $"{options.BaseUrl.TrimEnd('/')}/0/public/AssetPairs?assetVersion=1&pair={Uri.EscapeDataString(pairs)}";
        using var response = await httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        ThrowIfKrakenError(doc.RootElement);

        var metadata = new Dictionary<string, PairMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var pairProperty in doc.RootElement.GetProperty("result").EnumerateObject())
        {
            if (pairProperty.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var value = pairProperty.Value;
            var altName = GetString(value, "altname") ?? pairProperty.Name;
            var wsName = GetString(value, "wsname") ?? pairProperty.Name;
            var instrument = instruments.FirstOrDefault(item =>
                item.KrakenPair.Equals(altName, StringComparison.OrdinalIgnoreCase)
                || item.Pair.Equals(wsName, StringComparison.OrdinalIgnoreCase));
            if (instrument is null)
            {
                continue;
            }

            // With assetVersion=1 the AssetPairs response PROPERTY NAME (e.g. "BTC/EUR")
            // is exactly the key the Ticker response uses, even though wsname stays
            // "XBT/EUR". Storing it as the ticker key is what keeps BTC/DOGE quotes.
            metadata[instrument.KrakenPair] = new PairMetadata(
                altName,
                wsName,
                pairProperty.Name,
                new PairRules(
                    instrument.Pair,
                    GetString(value, "status") ?? "unknown",
                    GetDecimal(value, "ordermin"),
                    GetDecimal(value, "costmin"),
                    GetInt(value, "lot_decimals"),
                    GetInt(value, "pair_decimals")));
        }

        return metadata;
    }

    private async Task<Dictionary<string, Quote>> GetQuotesAsync(
        IReadOnlyList<InstrumentOptions> instruments,
        IReadOnlyDictionary<string, PairMetadata> metadata,
        CancellationToken cancellationToken)
    {
        if (instruments.Count == 0)
        {
            return new Dictionary<string, Quote>(StringComparer.OrdinalIgnoreCase);
        }

        var pairs = string.Join(",", instruments.Select(instrument => instrument.KrakenPair));
        var uri = $"{options.BaseUrl.TrimEnd('/')}/0/public/Ticker?assetVersion=1&pair={Uri.EscapeDataString(pairs)}";
        using var response = await httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        ThrowIfKrakenError(doc.RootElement);

        var quotes = new Dictionary<string, Quote>(StringComparer.OrdinalIgnoreCase);
        foreach (var pairProperty in doc.RootElement.GetProperty("result").EnumerateObject())
        {
            var instrument = ResolveQuoteInstrument(instruments, metadata, pairProperty.Name);
            if (instrument is null || pairProperty.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var value = pairProperty.Value;
            var last = ParseKrakenDecimal(value.GetProperty("c")[0]);
            var open = ParseKrakenDecimal(value.GetProperty("o"));
            var changePercent = open == 0m ? 0m : decimal.Round((last - open) / open * 100m, 2);
            quotes[instrument.KrakenPair] = new Quote(
                ParseKrakenDecimal(value.GetProperty("b")[0]),
                ParseKrakenDecimal(value.GetProperty("a")[0]),
                last,
                ParseKrakenDecimal(value.GetProperty("v")[1]),
                changePercent);
        }

        return quotes;
    }

    // Matches a Ticker response key against a configured instrument. Primary match is
    // the AssetPairs-derived ticker key; if metadata is unavailable (cache build
    // failed) we fall back to plain pair / kraken-pair name matching.
    private static InstrumentOptions? ResolveQuoteInstrument(
        IReadOnlyList<InstrumentOptions> instruments,
        IReadOnlyDictionary<string, PairMetadata> metadata,
        string tickerKey)
    {
        var byTickerKey = instruments.FirstOrDefault(item =>
            metadata.TryGetValue(item.KrakenPair, out var pairMetadata)
            && pairMetadata.TickerKey.Equals(tickerKey, StringComparison.OrdinalIgnoreCase));
        if (byTickerKey is not null)
        {
            return byTickerKey;
        }

        return instruments.FirstOrDefault(item =>
            item.Pair.Equals(tickerKey, StringComparison.OrdinalIgnoreCase)
            || item.KrakenPair.Equals(tickerKey, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<Candle>> GetClosedCandlesWithRetryAsync(
        InstrumentOptions instrument,
        int timeframeMinutes,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await GetClosedCandlesAsync(instrument, timeframeMinutes, cancellationToken);
            }
            catch (HttpRequestException ex)
                when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < RateLimitBackoffs.Length)
            {
                await Task.Delay(RateLimitBackoffs[attempt], cancellationToken);
            }
        }
    }

    private static bool IsTransient(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or JsonException or InvalidOperationException
        || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private async Task<IReadOnlyList<Candle>> GetClosedCandlesAsync(
        InstrumentOptions instrument,
        int timeframeMinutes,
        CancellationToken cancellationToken)
    {
        var uri = $"{options.BaseUrl.TrimEnd('/')}/0/public/OHLC?pair={Uri.EscapeDataString(instrument.KrakenPair)}&interval={timeframeMinutes}";
        using var response = await httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        ThrowIfKrakenError(doc.RootElement);

        var result = doc.RootElement.GetProperty("result");
        var pairProperty = result.EnumerateObject().FirstOrDefault(prop => prop.Name != "last");
        if (pairProperty.Value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Candle>();
        }

        var candles = new List<Candle>();
        foreach (var item in pairProperty.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 8)
            {
                continue;
            }

            candles.Add(new Candle(
                DateTimeOffset.FromUnixTimeSeconds(item[0].GetInt64()),
                ParseKrakenDecimal(item[1]),
                ParseKrakenDecimal(item[2]),
                ParseKrakenDecimal(item[3]),
                ParseKrakenDecimal(item[4]),
                ParseKrakenDecimal(item[6]),
                item[7].GetInt32()));
        }

        if (candles.Count == 0)
        {
            return candles;
        }

        var interval = TimeSpan.FromMinutes(timeframeMinutes);
        var now = DateTimeOffset.UtcNow;
        return candles
            .Where(candle => candle.OpenTime + interval <= now)
            .ToList();
    }

    private static void ThrowIfKrakenError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Array || error.GetArrayLength() == 0)
        {
            return;
        }

        var errors = error.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item));
        throw new InvalidOperationException($"Kraken error: {string.Join(", ", errors)}");
    }

    private static decimal ParseKrakenDecimal(JsonElement element) =>
        decimal.Parse(element.GetString() ?? "0", NumberStyles.Number, CultureInfo.InvariantCulture);

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) ? property.GetString() : null;

    private static decimal GetDecimal(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
            ? decimal.Parse(property.GetString() ?? "0", NumberStyles.Number, CultureInfo.InvariantCulture)
            : 0m;

    private static int GetInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value) ? value : 0;

    private sealed record PairMetadata(string AltName, string WsName, string TickerKey, PairRules Rules);
}

internal sealed class SampleMarketDataSource : IMarketDataSource
{
    public Task<IReadOnlyList<InstrumentMarketState>> GetLightMarketStatesAsync(
        IReadOnlyList<InstrumentOptions> instruments,
        CancellationToken cancellationToken)
    {
        var states = instruments.Select((instrument, index) =>
        {
            var candles = BuildSampleCandles(instrument.Pair, 5, index);
            var last = candles[^1].Close;
            var first = candles[Math.Max(0, candles.Count - 24)].Close;
            return new InstrumentMarketState
            {
                Instrument = instrument,
                Candles = Array.Empty<Candle>(),
                Quote = new Quote(
                    decimal.Round(last * 0.9995m, 4),
                    decimal.Round(last * 1.0005m, 4),
                    last,
                    candles[^1].Volume,
                    first == 0m ? 0m : decimal.Round((last - first) / first * 100m, 2)),
                PairRules = new PairRules(instrument.Pair, "sample", 0.001m, 0.5m, 8, 2),
                DataWarning = null
            };
        }).ToList();

        return Task.FromResult<IReadOnlyList<InstrumentMarketState>>(states);
    }

    public Task<IReadOnlyList<InstrumentMarketState>> GetFullMarketStatesAsync(
        IReadOnlyList<InstrumentOptions> instruments,
        int timeframeMinutes,
        IReadOnlyList<InstrumentMarketState> lightStates,
        CancellationToken cancellationToken)
    {
        var states = instruments.Select((instrument, index) =>
        {
            var candles = BuildSampleCandles(instrument.Pair, timeframeMinutes, index);
            return new InstrumentMarketState
            {
                Instrument = instrument,
                Candles = candles,
                Quote = new Quote(
                    decimal.Round(candles[^1].Close * 0.9995m, 4),
                    decimal.Round(candles[^1].Close * 1.0005m, 4),
                    candles[^1].Close,
                    candles[^1].Volume),
                PairRules = new PairRules(instrument.Pair, "sample", 0.001m, 0.5m, 8, 2),
                DataWarning = null
            };
        }).ToList();

        return Task.FromResult<IReadOnlyList<InstrumentMarketState>>(states);
    }

    private static IReadOnlyList<Candle> BuildSampleCandles(string pair, int timeframeMinutes, int index)
    {
        var basePrice = pair.StartsWith("BTC", StringComparison.OrdinalIgnoreCase)
            ? 62000m
            : pair.StartsWith("ETH", StringComparison.OrdinalIgnoreCase)
                ? 3200m
                : 145m;

        var now = DateTimeOffset.UtcNow;
        var start = now.AddMinutes(-timeframeMinutes * 80);
        var candles = new List<Candle>();

        for (var i = 0; i < 80; i++)
        {
            var drift = pair.StartsWith("SOL", StringComparison.OrdinalIgnoreCase) ? i * 0.045m : i * 0.01m;
            var wave = (decimal)Math.Sin((i + index) / 5.0) * (basePrice * 0.002m);
            var close = decimal.Round(basePrice + drift + wave, 4);
            var open = i == 0 ? close - 0.2m : candles[^1].Close;
            var high = Math.Max(open, close) + basePrice * 0.001m;
            var low = Math.Min(open, close) - basePrice * 0.001m;
            candles.Add(new Candle(
                start.AddMinutes(i * timeframeMinutes),
                open,
                decimal.Round(high, 4),
                decimal.Round(low, 4),
                close,
                1000m + i * 3m + index * 100m,
                10 + i));
        }

        return candles;
    }
}
