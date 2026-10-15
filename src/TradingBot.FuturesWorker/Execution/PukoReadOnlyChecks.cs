using System.Globalization;
using System.Text.Json;

namespace TradingBot.FuturesWorker;

internal sealed record PaperFeeRate(decimal Percent, string Source);

// Public GETs only. Deliberately has no broker, API credentials, or order methods.
internal sealed class PukoReadOnlyChecks(
    HttpClient http, FuturesBotConfiguration config, ITelegramNotifier telegram, IClock? clock = null)
{
    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private readonly KrakenContractRegistry _registry = new(http, config.Kraken,
        config.CandidateUniverse, config.BykoV2.Universe, clock);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _alerts = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<InstrumentOptions> _instruments = [];
    private readonly object _feeGate = new();
    private DateTimeOffset _nextRefresh;
    private DateTimeOffset _feesValidUntil;
    private DateTimeOffset _feesFetchedAt;
    private decimal? _maker;
    private decimal? _taker;
    private string? _loadedSchedule;
    private decimal _loadedVolume;

    public static bool Applies(FuturesBotConfiguration config) => !config.Futures.LiveTradingEnabled
        && config.BotInstance.Id.Equals("futures-pukis-live", StringComparison.OrdinalIgnoreCase);

    public PaperFeeRate Fee(bool restedAndFilledLater = false)
    {
        lock (_feeGate)
        {
            var fresh = _clock.UtcNow < _feesValidUntil && _loadedSchedule == config.PukoReadOnly.FeeScheduleUid
                && _loadedVolume == config.PukoReadOnly.FeeVolumeUsd;
            var rate = fresh ? restedAndFilledLater ? _maker : _taker : null;
            return rate is { } value
                ? new(value, FormattableString.Invariant($"kraken_public_assumed schedule={_loadedSchedule} volumeUsd={_loadedVolume} fetchedAt={_feesFetchedAt:O} liquidity={(restedAndFilledLater ? "maker" : "taker")} feePct={value}"))
                : new(restedAndFilledLater ? config.Fees.MakerPct : config.Fees.TakerPct,
                    "configured_fallback public_fee_schedule_unavailable_or_stale");
        }
    }

    public async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await RefreshAsync(cancellationToken); }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                await AlertAsync("*", "MAINTENANCE_FAILED", cancellationToken);
            }
            await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_clock.UtcNow < _nextRefresh) return;
            _nextRefresh = _clock.UtcNow.AddHours(1);
            var selection = await _registry.GetUniverseAsync(cancellationToken);
            _instruments = selection.Instruments;
            if (!_registry.HasFreshMetadata)
            {
                _nextRefresh = _clock.UtcNow.AddMinutes(1);
                await AlertAsync("*", "METADATA_UNAVAILABLE", cancellationToken);
            }
            foreach (var pair in config.BykoV2.Universe.Concat(config.CandidateUniverse.Where(x => x.Enabled).Select(x => x.Pair)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (FuturesDecisionWorker.ResolveDebykoInstrument(DebykoMarketClient.ToInstrument(pair), selection.Instruments) is null)
                    await AlertAsync(pair, "CONTRACT_UNAVAILABLE", cancellationToken);
            }
            try
            {
                using var response = await http.GetAsync($"{config.Kraken.BaseUrl.TrimEnd('/')}/derivatives/api/v3/feeschedules", cancellationToken);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (document.RootElement.GetProperty("result").GetString() != "success") throw new InvalidDataException();
                var schedule = document.RootElement.GetProperty("feeSchedules").EnumerateArray()
                    .Single(x => x.GetProperty("uid").GetString() == config.PukoReadOnly.FeeScheduleUid);
                if (config.PukoReadOnly.FeeVolumeUsd < 0m) throw new InvalidDataException();
                var tier = schedule.GetProperty("tiers").EnumerateArray()
                    .Where(x => x.GetProperty("usdVolume").GetDecimal() <= config.PukoReadOnly.FeeVolumeUsd)
                    .OrderByDescending(x => x.GetProperty("usdVolume").GetDecimal()).First();
                var maker = tier.GetProperty("makerFee").GetDecimal();
                var taker = tier.GetProperty("takerFee").GetDecimal();
                if (maker is < -1m or > 5m || taker is < 0m or > 5m) throw new InvalidDataException();
                lock (_feeGate)
                {
                    _maker = maker;
                    _taker = taker;
                    _loadedSchedule = config.PukoReadOnly.FeeScheduleUid;
                    _loadedVolume = config.PukoReadOnly.FeeVolumeUsd;
                    _feesFetchedAt = _clock.UtcNow;
                    _feesValidUntil = _feesFetchedAt.AddHours(2);
                }
                Console.WriteLine($"PUKO_FEE source={Fee().Source}");
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _nextRefresh = _clock.UtcNow.AddMinutes(1);
                await AlertAsync("*", "FEE_SCHEDULE_UNAVAILABLE", cancellationToken);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<string> CheckAsync(InstrumentOptions instrument, FuturesDesiredExposure desired,
        decimal price, decimal notional, CancellationToken cancellationToken)
    {
        var actual = FuturesDecisionWorker.ResolveDebykoInstrument(DebykoMarketClient.ToInstrument(instrument.Pair), _instruments);
        string code;
        string detail = "";
        if (!_registry.HasFreshMetadata) code = "METADATA_UNAVAILABLE";
        else if (actual is null || !_registry.TryGetRules(actual.KrakenPair, out var rules)) code = "CONTRACT_UNAVAILABLE";
        else if (!actual.KrakenPair.Equals(instrument.KrakenPair, StringComparison.OrdinalIgnoreCase)) code = "SYMBOL_MISMATCH";
        else if (price <= 0m || notional <= 0m) code = "INVALID_PRICE_OR_NOTIONAL";
        else
        {
            var quantity = rules.RoundQuantity(notional / price);
            var rawLimit = price * (desired == FuturesDesiredExposure.Short
                ? 1m - config.Entry.MaxEntryPriceDeviationPct / 100m : 1m + config.Entry.MaxEntryPriceDeviationPct / 100m);
            var limit = rules.RoundPrice(rawLimit, desired == FuturesDesiredExposure.Short);
            code = quantity <= 0m ? "BELOW_LOT" : limit <= 0m ? "INVALID_LIMIT" : "OK";
            detail = FormattableString.Invariant($"symbol={rules.Symbol} quantity={quantity} limit={limit} tick={rules.PriceTick} step={rules.QuantityStep} roundedNotional={quantity * price} notionalDelta={quantity * price - notional}");
        }
        var result = $"local_preflight={code} {detail}; indicative_signal_price_not_exchange_acceptance; fee_source={Fee().Source}";
        Console.WriteLine($"PUKO_PREFLIGHT pair={instrument.Pair} {result}");
        if (code != "OK") await AlertAsync(instrument.Pair, code, cancellationToken);
        return result;
    }

    public async Task AlertAsync(string pair, string code, CancellationToken cancellationToken)
    {
        var key = pair + ":" + code;
        lock (_alerts)
        {
            if (_alerts.TryGetValue(key, out var last) && _clock.UtcNow - last < TimeSpan.FromMinutes(30)) return;
            _alerts[key] = _clock.UtcNow;
        }
        Console.WriteLine($"PUKO_READONLY_WARNING pair={pair} code={code}");
        await telegram.TrySendAsync($"PUKO · {System.Net.WebUtility.HtmlEncode(pair)} · local preflight: {code}. Paper execution unchanged; no exchange order sent.", cancellationToken);
    }
}
