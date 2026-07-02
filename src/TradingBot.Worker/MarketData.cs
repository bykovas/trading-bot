using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace TradingBot.Worker;

internal interface IMarketDataSource
{
    Task<IReadOnlyList<InstrumentMarketState>> GetMarketStatesAsync(
        IReadOnlyList<InstrumentOptions> instruments,
        int timeframeMinutes,
        CancellationToken cancellationToken);
}

internal sealed class KrakenMarketDataSource(HttpClient httpClient, KrakenOptions options) : IMarketDataSource
{
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<InstrumentMarketState>> GetMarketStatesAsync(
        IReadOnlyList<InstrumentOptions> instruments,
        int timeframeMinutes,
        CancellationToken cancellationToken)
    {
        var states = new List<InstrumentMarketState>();
        foreach (var instrument in instruments)
        {
            try
            {
                var rules = await GetPairRulesAsync(instrument, cancellationToken);
                var candles = await GetClosedCandlesAsync(instrument, timeframeMinutes, cancellationToken);
                var quote = await GetQuoteAsync(instrument, cancellationToken);
                states.Add(new InstrumentMarketState
                {
                    Instrument = instrument,
                    Candles = candles,
                    PairRules = rules,
                    Quote = quote,
                    DataWarning = candles.Count < 30 ? $"Only {candles.Count} closed candles returned." : null
                });
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
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

    private async Task<PairRules?> GetPairRulesAsync(InstrumentOptions instrument, CancellationToken cancellationToken)
    {
        var uri = $"{options.BaseUrl.TrimEnd('/')}/0/public/AssetPairs?assetVersion=1&pair={Uri.EscapeDataString(instrument.KrakenPair)}";
        using var response = await httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        ThrowIfKrakenError(doc.RootElement);

        var result = doc.RootElement.GetProperty("result");
        var pairProperty = result.EnumerateObject().FirstOrDefault();
        if (pairProperty.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var value = pairProperty.Value;
        return new PairRules(
            instrument.Pair,
            GetString(value, "status") ?? "unknown",
            GetDecimal(value, "ordermin"),
            GetDecimal(value, "costmin"),
            GetInt(value, "lot_decimals"),
            GetInt(value, "pair_decimals"));
    }

    private async Task<Quote?> GetQuoteAsync(InstrumentOptions instrument, CancellationToken cancellationToken)
    {
        var uri = $"{options.BaseUrl.TrimEnd('/')}/0/public/Ticker?pair={Uri.EscapeDataString(instrument.KrakenPair)}";
        using var response = await httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        ThrowIfKrakenError(doc.RootElement);

        var result = doc.RootElement.GetProperty("result");
        var pairProperty = result.EnumerateObject().FirstOrDefault();
        if (pairProperty.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var value = pairProperty.Value;
        return new Quote(
            ParseKrakenDecimal(value.GetProperty("b")[0]),
            ParseKrakenDecimal(value.GetProperty("a")[0]),
            ParseKrakenDecimal(value.GetProperty("c")[0]),
            ParseKrakenDecimal(value.GetProperty("v")[1]));
    }

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
}

internal sealed class SampleMarketDataSource : IMarketDataSource
{
    public Task<IReadOnlyList<InstrumentMarketState>> GetMarketStatesAsync(
        IReadOnlyList<InstrumentOptions> instruments,
        int timeframeMinutes,
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
