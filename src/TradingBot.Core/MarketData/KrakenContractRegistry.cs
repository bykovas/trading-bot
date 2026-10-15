using System.Text.Json;

namespace TradingBot.Core.MarketData;

// Reference data only: never reads tickers, candles, books or account state.
public class KrakenContractRegistry(
    HttpClient http, KrakenOptions options, IReadOnlyList<InstrumentOptions> configured,
    IReadOnlyList<string> rulePairs, IClock? clock = null) : IUniverseProvider
{
    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyDictionary<string, KrakenContractRules> _rules = new Dictionary<string, KrakenContractRules>();
    private IReadOnlyList<InstrumentOptions> _instruments = [];
    private DateTimeOffset _validUntil;
    private DateTimeOffset _retryAt;
    public bool HasFreshMetadata => _clock.UtcNow < _validUntil;

    public bool TryGetRules(string symbol, out KrakenContractRules rules)
    {
        rules = null!;
        return _clock.UtcNow < _validUntil && _rules.TryGetValue(symbol, out rules!);
    }

    public void Invalidate() => _validUntil = _retryAt = DateTimeOffset.MinValue;

    public async Task<UniverseSelection> GetUniverseAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            string? warning = null;
            if (_clock.UtcNow >= _validUntil && _clock.UtcNow >= _retryAt)
            {
                try
                {
                    using var response = await http.GetAsync($"{options.BaseUrl.TrimEnd('/')}/derivatives/api/v3/instruments", cancellationToken);
                    response.EnsureSuccessStatusCode();
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                    if (document.RootElement.GetProperty("result").GetString() != "success")
                        throw new InvalidDataException("Kraken contract registry unavailable");
                    var wanted = configured.Where(x => x.Enabled).Select(x => Instrument(x.Pair))
                        .Concat(rulePairs.Select(Instrument)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var rules = new Dictionary<string, KrakenContractRules>(StringComparer.OrdinalIgnoreCase);
                    var instruments = new List<InstrumentOptions>();
                    foreach (var row in document.RootElement.GetProperty("instruments").EnumerateArray())
                    {
                        var symbol = row.GetProperty("symbol").GetString()!;
                        if (!symbol.StartsWith("PF_", StringComparison.Ordinal) || !symbol.EndsWith("USD", StringComparison.Ordinal)
                            || !KrakenContractMetadata.IsExecutable(row)) continue;
                        var pair = KrakenContractMetadata.Pair(symbol);
                        if (!wanted.Contains(Instrument(pair))) continue;
                        var contract = KrakenContractMetadata.Rules(row);
                        if (contract is null) continue;
                        var precision = KrakenContractMetadata.Precision(row)!.Value;
                        rules.Add(symbol, contract);
                        instruments.Add(new InstrumentOptions { Pair = pair, KrakenPair = symbol, Venue = "KrakenFutures",
                            QuantityDecimals = Math.Max(0, precision), PriceDecimals = KrakenContractMetadata.DecimalPlaces(contract.PriceTick) });
                    }
                    if (rules.Count == 0) throw new InvalidDataException("No executable Kraken contracts");
                    _rules = rules;
                    _instruments = instruments;
                    _validUntil = _clock.UtcNow.AddHours(1);
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    warning = $"contract metadata unavailable ({error.GetType().Name}); new entries fail closed";
                    _retryAt = _clock.UtcNow.AddSeconds(30);
                }
            }
            return new(_instruments, new("kraken-execution-contracts", _instruments.Count, configured.Count,
                _instruments.Count, 0, warning));
        }
        finally { _gate.Release(); }
    }

    private static string Instrument(string pair)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pair);
        var text = pair.Trim().ToUpperInvariant();
        var asset = text.EndsWith("/USD", StringComparison.Ordinal) ? text[..^4]
            : text.EndsWith("-PERP", StringComparison.Ordinal) ? text[..^5] : "";
        if (asset.Length == 0 || asset.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("Expected a USD pair or perpetual instrument", nameof(pair));
        return (asset == "XBT" ? "BTC" : asset) + "-PERP";
    }
}
