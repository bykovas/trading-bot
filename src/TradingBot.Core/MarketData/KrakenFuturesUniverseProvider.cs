using System.Text.Json;

namespace TradingBot.Core.MarketData;

public sealed class KrakenFuturesUniverseProvider(
    HttpClient httpClient,
    KrakenOptions kraken,
    UniverseDiscoveryOptions options,
    IReadOnlyList<InstrumentOptions> configured) : IUniverseProvider
{
    private UniverseSelection? _cached;
    private DateTimeOffset _cacheUntilUtc;

    public async Task<UniverseSelection> GetUniverseAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return Configured("disabled");
        }

        var now = DateTimeOffset.UtcNow;
        if (_cached is not null && now < _cacheUntilUtc)
        {
            return _cached;
        }

        try
        {
            var discovered = await DiscoverAsync(cancellationToken);
            var selected = Merge(discovered, warning: null);
            _cached = selected;
            _cacheUntilUtc = now.AddSeconds(Math.Max(60, options.RefreshSeconds));
            return selected;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var fallback = Configured($"futures universe discovery failed: {ex.Message}");
            _cached ??= fallback;
            _cacheUntilUtc = now.AddMinutes(5);
            return fallback;
        }
    }

    private async Task<IReadOnlyList<InstrumentOptions>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var uri = $"{kraken.BaseUrl.TrimEnd('/')}/derivatives/api/v3/instruments";
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

        var instruments = new List<InstrumentOptions>();
        foreach (var item in instrumentsElement.EnumerateArray())
        {
            var symbol = GetString(item, "symbol");
            if (string.IsNullOrWhiteSpace(symbol)
                || !symbol.StartsWith("PF_", StringComparison.OrdinalIgnoreCase)
                || !symbol.EndsWith("USD", StringComparison.OrdinalIgnoreCase)
                || !GetBool(item, "tradeable")
                || GetBool(item, "postOnly"))
            {
                continue;
            }

            var pair = GetString(item, "pair");
            instruments.Add(new InstrumentOptions
            {
                Pair = NormalizePair(symbol, pair),
                KrakenPair = symbol,
                Venue = "KrakenFutures",
                Enabled = true,
                QuantityDecimals = GetInt(item, "contractValueTradePrecision")
            });
        }

        return instruments
            .GroupBy(instrument => instrument.Pair, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(instrument => instrument.Pair, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private UniverseSelection Merge(IReadOnlyList<InstrumentOptions> discovered, string? warning)
    {
        var result = new Dictionary<string, InstrumentOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var instrument in discovered)
        {
            result[instrument.Pair] = instrument;
        }

        if (options.IncludeConfiguredUniverse)
        {
            foreach (var instrument in configured.Where(instrument => instrument.Enabled))
            {
                result.TryGetValue(instrument.Pair, out var discoveredInstrument);
                result[instrument.Pair] = new InstrumentOptions
                {
                    Pair = instrument.Pair,
                    KrakenPair = string.IsNullOrWhiteSpace(instrument.KrakenPair)
                        ? discoveredInstrument?.KrakenPair ?? instrument.KrakenPair
                        : instrument.KrakenPair,
                    Venue = string.IsNullOrWhiteSpace(instrument.Venue)
                        ? discoveredInstrument?.Venue ?? instrument.Venue
                        : instrument.Venue,
                    Enabled = true,
                    QuantityDecimals = instrument.QuantityDecimals ?? discoveredInstrument?.QuantityDecimals
                };
            }
        }

        var blacklist = BuildSet(options.Blacklist);
        foreach (var key in blacklist)
        {
            result.Remove(key);
        }

        foreach (var pair in options.ForceInclude)
        {
            var configuredInstrument = configured.FirstOrDefault(instrument =>
                instrument.Pair.Equals(pair, StringComparison.OrdinalIgnoreCase)
                || instrument.KrakenPair.Equals(pair, StringComparison.OrdinalIgnoreCase));
            if (configuredInstrument is not null)
            {
                result[configuredInstrument.Pair] = new InstrumentOptions
                {
                    Pair = configuredInstrument.Pair,
                    KrakenPair = configuredInstrument.KrakenPair,
                    Venue = configuredInstrument.Venue,
                    Enabled = true,
                    QuantityDecimals = configuredInstrument.QuantityDecimals
                };
            }
        }

        var instruments = result.Values
            .OrderBy(instrument => instrument.Pair, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new UniverseSelection(
            instruments,
            new UniverseSelectionDiagnostics(
                "kraken-futures-instruments",
                discovered.Count,
                configured.Count,
                instruments.Count,
                blacklist.Count,
                warning));
    }

    private UniverseSelection Configured(string? warning)
    {
        var enabled = configured.Where(instrument => instrument.Enabled).ToList();
        return new UniverseSelection(
            enabled,
            new UniverseSelectionDiagnostics(
                "configured",
                0,
                configured.Count,
                enabled.Count,
                0,
                warning));
    }

    private static string NormalizePair(string symbol, string? pair)
    {
        if (!string.IsNullOrWhiteSpace(pair) && pair.Contains('/'))
        {
            return pair;
        }

        var compact = symbol.StartsWith("PF_", StringComparison.OrdinalIgnoreCase)
            ? symbol[3..]
            : symbol;
        return compact.EndsWith("USD", StringComparison.OrdinalIgnoreCase)
            ? $"{compact[..^3]}/USD"
            : compact;
    }

    private static HashSet<string> BuildSet(IEnumerable<string> values) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            _ => false
        };
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var number) => number,
            _ => null
        };
    }

    private static void ThrowIfErrorResult(JsonElement root, string endpoint)
    {
        if (root.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.String
            && result.GetString()?.Equals("error", StringComparison.OrdinalIgnoreCase) == true)
        {
            var message = root.TryGetProperty("error", out var error) ? error.ToString() : "unknown error";
            throw new InvalidOperationException($"Kraken Futures {endpoint} returned error: {message}");
        }
    }
}
