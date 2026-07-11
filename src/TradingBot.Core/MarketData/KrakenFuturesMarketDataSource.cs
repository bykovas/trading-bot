using System.Globalization;
using System.Text.Json;

namespace TradingBot.Core.MarketData;

// Public Kraken Futures market data adapter. It intentionally implements only
// read-only endpoints: instruments, tickers, and chart candles. Trading remains
// impossible until the separate broker adapter is implemented and tested.
public sealed class KrakenFuturesMarketDataSource(HttpClient httpClient, KrakenOptions options) : IMarketDataSource
{
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly Dictionary<string, FuturesInstrumentMetadata> _metadataCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _metadataGate = new(1, 1);

    public async Task<IReadOnlyList<InstrumentMarketState>> GetLightMarketStatesAsync(
        IReadOnlyList<InstrumentOptions> instruments,
        CancellationToken cancellationToken)
    {
        var enabled = instruments.Where(instrument => instrument.Enabled).ToList();
        var (metadata, metadataWarning) = await TryGetInstrumentMetadataAsync(enabled, cancellationToken);
        var (quotes, quoteWarning) = await TryGetQuotesAsync(enabled, cancellationToken);
        var batchWarning = CombineWarnings(metadataWarning, quoteWarning);

        return enabled.Select(instrument =>
        {
            var symbol = SymbolOf(instrument);
            metadata.TryGetValue(symbol, out var instrumentMetadata);
            quotes.TryGetValue(symbol, out var quote);
            var warning = WarningFor(instrument, instrumentMetadata, quote, batchWarning);
            return new InstrumentMarketState
            {
                Instrument = instrument,
                Candles = Array.Empty<Candle>(),
                PairRules = instrumentMetadata?.Rules,
                Quote = quote,
                DataWarning = warning
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
        var (metadata, metadataWarning) = await TryGetInstrumentMetadataAsync(instruments, cancellationToken);
        var quotes = BuildQuoteLookup(lightStates);

        foreach (var instrument in instruments.Where(instrument => instrument.Enabled))
        {
            var symbol = SymbolOf(instrument);
            metadata.TryGetValue(symbol, out var instrumentMetadata);
            quotes.TryGetValue(symbol, out var quote);
            var warning = WarningFor(instrument, instrumentMetadata, quote, metadataWarning);

            try
            {
                var candles = await GetClosedCandlesAsync(symbol, timeframeMinutes, cancellationToken);
                var orderBook = await GetOrderBookAsync(symbol, cancellationToken);
                states.Add(new InstrumentMarketState
                {
                    Instrument = instrument,
                    Candles = candles,
                    PairRules = instrumentMetadata?.Rules,
                    Quote = quote,
                    OrderBook = orderBook,
                    DataWarning = candles.Count < 30
                        ? CombineWarnings(warning, $"Only {candles.Count} closed mark candles returned.")
                        : warning
                });
            }
            catch (Exception ex) when (IsTransient(ex, cancellationToken))
            {
                states.Add(new InstrumentMarketState
                {
                    Instrument = instrument,
                    Candles = Array.Empty<Candle>(),
                    PairRules = instrumentMetadata?.Rules,
                    Quote = quote,
                    DataWarning = CombineWarnings(warning, $"futures candles failed: {ex.Message}")
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
                lookup[SymbolOf(state.Instrument)] = state.Quote;
            }
        }

        return lookup;
    }

    private async Task<(IReadOnlyDictionary<string, FuturesInstrumentMetadata> Metadata, string? Warning)> TryGetInstrumentMetadataAsync(
        IReadOnlyList<InstrumentOptions> instruments,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await GetInstrumentMetadataAsync(instruments, cancellationToken), null);
        }
        catch (Exception ex) when (IsTransient(ex, cancellationToken))
        {
            return (_metadataCache, $"futures instruments metadata unavailable ({ex.Message})");
        }
    }

    private async Task<IReadOnlyDictionary<string, FuturesInstrumentMetadata>> GetInstrumentMetadataAsync(
        IReadOnlyList<InstrumentOptions> instruments,
        CancellationToken cancellationToken)
    {
        var missing = instruments
            .Select(SymbolOf)
            .Where(symbol => !_metadataCache.ContainsKey(symbol))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missing.Count == 0)
        {
            return _metadataCache;
        }

        await _metadataGate.WaitAsync(cancellationToken);
        try
        {
            missing = instruments
                .Select(SymbolOf)
                .Where(symbol => !_metadataCache.ContainsKey(symbol))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (missing.Count == 0)
            {
                return _metadataCache;
            }

            var uri = $"{BaseUrl()}/derivatives/api/v3/instruments";
            using var response = await httpClient.GetAsync(uri, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            ThrowIfErrorResult(document.RootElement, "instruments");

            if (!document.RootElement.TryGetProperty("instruments", out var instrumentsElement)
                || instrumentsElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Kraken Futures instruments response missing instruments array.");
            }

            foreach (var item in instrumentsElement.EnumerateArray())
            {
                var symbol = GetString(item, "symbol");
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    continue;
                }

                _metadataCache[symbol] = new FuturesInstrumentMetadata(
                    symbol,
                    GetString(item, "pair") ?? symbol,
                    GetBool(item, "tradeable"),
                    GetBool(item, "postOnly"),
                    new PairRules(
                        symbol,
                        GetBool(item, "tradeable") ? "online" : "not_tradeable",
                        0m,
                        GetDecimal(item, "contractSize"),
                        GetInt(item, "contractValueTradePrecision"),
                        TickDecimals(GetDecimal(item, "tickSize"))));
            }
        }
        finally
        {
            _metadataGate.Release();
        }

        return _metadataCache;
    }

    private async Task<(Dictionary<string, Quote> Quotes, string? Warning)> TryGetQuotesAsync(
        IReadOnlyList<InstrumentOptions> instruments,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await GetQuotesAsync(instruments, cancellationToken), null);
        }
        catch (Exception ex) when (IsTransient(ex, cancellationToken))
        {
            return (new Dictionary<string, Quote>(StringComparer.OrdinalIgnoreCase), $"futures tickers failed: {ex.Message}");
        }
    }

    private async Task<Dictionary<string, Quote>> GetQuotesAsync(
        IReadOnlyList<InstrumentOptions> instruments,
        CancellationToken cancellationToken)
    {
        var symbols = instruments.Select(SymbolOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (symbols.Count == 0)
        {
            return new Dictionary<string, Quote>(StringComparer.OrdinalIgnoreCase);
        }

        var quotes = new Dictionary<string, Quote>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in symbols.Chunk(80))
        {
            var batchSymbols = batch.ToList();
            var query = string.Join("&", batchSymbols.Select(symbol => $"symbol={Uri.EscapeDataString(symbol)}"));
            var uri = $"{BaseUrl()}/derivatives/api/v3/tickers?{query}";
            using var response = await httpClient.GetAsync(uri, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            ThrowIfErrorResult(document.RootElement, "tickers");

            if (!document.RootElement.TryGetProperty("tickers", out var tickers)
                || tickers.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Kraken Futures tickers response missing tickers array.");
            }

            foreach (var item in tickers.EnumerateArray())
            {
                var symbol = GetString(item, "symbol");
                if (string.IsNullOrWhiteSpace(symbol) || !batchSymbols.Contains(symbol, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var mark = GetDecimal(item, "markPrice");
                var last = GetDecimal(item, "last");
                var bid = GetDecimal(item, "bid");
                var ask = GetDecimal(item, "ask");
                quotes[symbol] = new Quote(
                    bid,
                    ask,
                    mark > 0m ? mark : last,
                    GetDecimal(item, "volumeQuote", fallbackProperty: "vol24h"),
                    GetNullableDecimal(item, "change24h"),
                    GetNullableDecimal(item, "fundingRate"),
                    mark > 0m ? mark : null,
                    GetNullableDecimal(item, "indexPrice"),
                    GetNullableDecimal(item, "bidSize"),
                    GetNullableDecimal(item, "askSize"));
            }
        }

        return quotes;
    }

    private async Task<OrderBookSnapshot?> GetOrderBookAsync(string symbol, CancellationToken cancellationToken)
    {
        var uri = $"{BaseUrl()}/derivatives/api/v3/orderbook?symbol={Uri.EscapeDataString(symbol)}";
        using var response = await httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        ThrowIfErrorResult(document.RootElement, "orderbook");

        if (!document.RootElement.TryGetProperty("orderBook", out var book)
            || book.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Kraken Futures orderbook response missing orderBook object.");
        }

        return new OrderBookSnapshot(ParseLevels(book, "bids"), ParseLevels(book, "asks"));
    }

    private static IReadOnlyList<OrderBookLevel> ParseLevels(JsonElement book, string propertyName)
    {
        if (!book.TryGetProperty(propertyName, out var levels)
            || levels.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<OrderBookLevel>();
        }

        var parsed = new List<OrderBookLevel>();
        foreach (var level in levels.EnumerateArray())
        {
            if (level.ValueKind != JsonValueKind.Array || level.GetArrayLength() < 2)
            {
                continue;
            }

            parsed.Add(new OrderBookLevel(ParseDecimal(level[0]), ParseDecimal(level[1])));
        }

        return propertyName == "bids"
            ? parsed.OrderByDescending(level => level.Price).ToList()
            : parsed.OrderBy(level => level.Price).ToList();
    }

    private async Task<IReadOnlyList<Candle>> GetClosedCandlesAsync(
        string symbol,
        int timeframeMinutes,
        CancellationToken cancellationToken)
    {
        var resolution = ResolutionOf(timeframeMinutes);
        var uri = $"{BaseUrl()}/api/charts/v1/mark/{Uri.EscapeDataString(symbol)}/{resolution}?count=120";
        using var response = await httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("candles", out var candlesElement)
            || candlesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Kraken Futures candles response missing candles array.");
        }

        var candles = new List<Candle>();
        foreach (var item in candlesElement.EnumerateArray())
        {
            var openTime = DateTimeOffset.FromUnixTimeMilliseconds(GetInt64(item, "time"));
            candles.Add(new Candle(
                openTime,
                GetDecimal(item, "open"),
                GetDecimal(item, "high"),
                GetDecimal(item, "low"),
                GetDecimal(item, "close"),
                GetDecimal(item, "volume"),
                0));
        }

        var interval = TimeSpan.FromMinutes(timeframeMinutes);
        var now = DateTimeOffset.UtcNow;
        return candles
            .Where(candle => candle.OpenTime + interval <= now)
            .OrderBy(candle => candle.OpenTime)
            .ToList();
    }

    private string BaseUrl() => options.BaseUrl.TrimEnd('/');

    private static string SymbolOf(InstrumentOptions instrument) =>
        string.IsNullOrWhiteSpace(instrument.KrakenPair) ? instrument.Pair : instrument.KrakenPair;

    private static string? WarningFor(
        InstrumentOptions instrument,
        FuturesInstrumentMetadata? metadata,
        Quote? quote,
        string? batchWarning)
    {
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(batchWarning))
        {
            warnings.Add(batchWarning);
        }

        if (metadata is null)
        {
            warnings.Add($"futures instrument metadata unavailable for {SymbolOf(instrument)}");
        }
        else if (!metadata.Tradeable)
        {
            warnings.Add($"futures instrument {metadata.Symbol} is not tradeable");
        }
        else if (metadata.PostOnly)
        {
            warnings.Add($"futures instrument {metadata.Symbol} is post-only");
        }

        if (quote is null)
        {
            warnings.Add($"futures ticker unavailable for {SymbolOf(instrument)}");
        }
        else
        {
            if (quote.MarkPrice is null || quote.MarkPrice <= 0m)
            {
                warnings.Add($"futures mark price unavailable for {SymbolOf(instrument)}");
            }

            if (quote.Bid <= 0m || quote.Ask <= 0m || quote.Ask < quote.Bid)
            {
                warnings.Add($"futures bid/ask unavailable for {SymbolOf(instrument)}");
            }
        }

        return warnings.Count == 0 ? null : string.Join("; ", warnings.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string? CombineWarnings(string? first, string? second) =>
        (first, second) switch
        {
            (null, null) => null,
            (not null, null) => first,
            (null, not null) => second,
            _ => $"{first}; {second}"
        };

    private static string ResolutionOf(int timeframeMinutes) => timeframeMinutes switch
    {
        <= 1 => "1m",
        <= 5 => "5m",
        <= 15 => "15m",
        <= 30 => "30m",
        <= 60 => "1h",
        <= 240 => "4h",
        <= 720 => "12h",
        _ => "1d"
    };

    private static bool IsTransient(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or JsonException or InvalidOperationException
        || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private static void ThrowIfErrorResult(JsonElement root, string endpoint)
    {
        if (root.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.String
            && !result.GetString()!.Equals("success", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Kraken Futures {endpoint} result={result.GetString()}");
        }
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) ? property.GetString() : null;

    private static bool GetBool(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var parsed) && parsed,
            _ => false
        };

    private static int GetInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value) ? value : 0;

    private static long GetInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value) ? value : 0;

    private static decimal GetDecimal(JsonElement element, string propertyName, string? fallbackProperty = null)
    {
        if (element.TryGetProperty(propertyName, out var property))
        {
            return ParseDecimal(property);
        }

        return fallbackProperty is not null && element.TryGetProperty(fallbackProperty, out var fallback)
            ? ParseDecimal(fallback)
            : 0m;
    }

    private static decimal? GetNullableDecimal(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) ? ParseDecimal(property) : null;

    private static decimal ParseDecimal(JsonElement property) =>
        property.ValueKind switch
        {
            JsonValueKind.Number => property.GetDecimal(),
            JsonValueKind.String => decimal.Parse(property.GetString() ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture),
            _ => 0m
        };

    private static int TickDecimals(decimal tickSize)
    {
        if (tickSize <= 0m)
        {
            return 0;
        }

        var text = tickSize.ToString("0.############################", CultureInfo.InvariantCulture);
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? 0 : text.Length - dot - 1;
    }

    private sealed record FuturesInstrumentMetadata(
        string Symbol,
        string Pair,
        bool Tradeable,
        bool PostOnly,
        PairRules Rules);
}
