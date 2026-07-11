using System.Text.Json;

namespace TradingBot.SpotWorker;

internal sealed class KrakenSpotUniverseProvider(
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
            var fallback = Configured($"spot universe discovery failed: {ex.Message}");
            _cached ??= fallback;
            _cacheUntilUtc = now.AddMinutes(5);
            return fallback;
        }
    }

    private async Task<IReadOnlyList<InstrumentOptions>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var uri = $"{kraken.BaseUrl.TrimEnd('/')}/0/public/AssetPairs?assetVersion=1";
        using var response = await httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        ThrowIfKrakenError(document.RootElement);

        var instruments = new List<InstrumentOptions>();
        foreach (var pairProperty in document.RootElement.GetProperty("result").EnumerateObject())
        {
            if (pairProperty.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var value = pairProperty.Value;
            var status = GetString(value, "status");
            var wsName = GetString(value, "wsname");
            var altName = GetString(value, "altname") ?? pairProperty.Name;
            if (!string.Equals(status, "online", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(wsName)
                || !wsName.EndsWith("/EUR", StringComparison.OrdinalIgnoreCase)
                || wsName.Contains(".d", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            instruments.Add(new InstrumentOptions
            {
                Pair = wsName,
                KrakenPair = altName,
                Venue = "Kraken",
                Enabled = true
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
                result[instrument.Pair] = instrument;
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
                    Enabled = true
                };
            }
        }

        var instruments = result.Values
            .OrderBy(instrument => instrument.Pair, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new UniverseSelection(
            instruments,
            new UniverseSelectionDiagnostics(
                "kraken-assetpairs",
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

    private static HashSet<string> BuildSet(IEnumerable<string> values) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void ThrowIfKrakenError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var errors)
            && errors.ValueKind == JsonValueKind.Array
            && errors.GetArrayLength() > 0)
        {
            throw new InvalidOperationException(string.Join("; ", errors.EnumerateArray().Select(error => error.GetString())));
        }
    }
}
