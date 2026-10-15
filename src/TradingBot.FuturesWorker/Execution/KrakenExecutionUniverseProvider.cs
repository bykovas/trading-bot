using System.Globalization;
using System.Text.Json;

namespace TradingBot.FuturesWorker;

internal sealed record KrakenContractRules(string Symbol, decimal PriceTick, decimal QuantityStep)
{
    public decimal RoundPrice(decimal price, bool shortEntry) =>
        (shortEntry ? Math.Ceiling(price / PriceTick) : Math.Floor(price / PriceTick)) * PriceTick;
    public decimal RoundQuantity(decimal quantity) => Math.Floor(quantity / QuantityStep) * QuantityStep;
}

// Reference data only: never reads tickers, candles, books or account state.
internal sealed class KrakenExecutionUniverseProvider(
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
                    var wanted = configured.Where(x => x.Enabled).Select(x => DebykoMarketClient.ToInstrument(x.Pair))
                        .Concat(rulePairs.Select(DebykoMarketClient.ToInstrument)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var rules = new Dictionary<string, KrakenContractRules>(StringComparer.OrdinalIgnoreCase);
                    var instruments = new List<InstrumentOptions>();
                    foreach (var row in document.RootElement.GetProperty("instruments").EnumerateArray())
                    {
                        var symbol = row.GetProperty("symbol").GetString()!;
                        if (!symbol.StartsWith("PF_", StringComparison.Ordinal) || !symbol.EndsWith("USD", StringComparison.Ordinal)
                            || !row.GetProperty("tradeable").GetBoolean()
                            || row.TryGetProperty("postOnly", out var postOnly) && postOnly.GetBoolean()
                            || row.TryGetProperty("suspended", out var suspended) && suspended.GetBoolean()) continue;
                        var asset = symbol[3..^3];
                        var pair = (asset is "BTC" or "XBT" ? "XBT" : asset is "XDG" ? "DOGE" : asset) + "/USD";
                        if (!wanted.Contains(DebykoMarketClient.ToInstrument(pair))) continue;
                        var tick = row.GetProperty("tickSize").GetDecimal();
                        var precision = row.GetProperty("contractValueTradePrecision").GetInt32();
                        if (tick <= 0m || precision is < -8 or > 12) continue;
                        var step = 1m;
                        for (var i = 0; i < Math.Abs(precision); i++) step = precision >= 0 ? step / 10m : step * 10m;
                        rules.Add(symbol, new(symbol, tick, step));
                        instruments.Add(new InstrumentOptions { Pair = pair, KrakenPair = symbol, Venue = "KrakenFutures",
                            QuantityDecimals = Math.Max(0, precision), PriceDecimals = DecimalPlaces(tick) });
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

    private static int DecimalPlaces(decimal value)
    {
        var text = value.ToString("0.############################", CultureInfo.InvariantCulture);
        var dot = text.IndexOf('.');
        return dot < 0 ? 0 : text.Length - dot - 1;
    }
}
