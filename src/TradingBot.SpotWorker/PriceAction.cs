namespace TradingBot.SpotWorker;

// One light-snapshot observation for a pair (ticker last/bid/ask at cycle time).
internal sealed record PriceObservation(
    DateTimeOffset Utc,
    decimal Last,
    decimal Bid,
    decimal Ask);

// Snapshot-based recent price action for one pair. Built from the per-cycle light
// ticker snapshots, NOT from candles: candles (and the EMAs computed from them) lag
// by design, while the ticker sequence shows what the price is doing RIGHT NOW.
// This is the input for the anti-lag entry guard that rejects LONG entries whose
// score is driven by lagging indicators while real prices are already rolling over.
internal sealed record PriceActionAssessment(
    string Pair,
    int SnapshotCount,
    bool DataSufficient,
    decimal LastPrice,
    decimal? TrendPercent,
    decimal? RollingAveragePrice,
    int ConsecutiveNonRisingSnapshots,
    int SamplesRequired = 0,
    DateTimeOffset? OldestSampleUtc = null,
    DateTimeOffset? NewestSampleUtc = null,
    bool Stale = false)
{
    public const string StateReady = "READY";
    public const string StateWarmingUp = "WARMING_UP";
    public const string StateStale = "STALE";
    public const string StateInsufficientData = "INSUFFICIENT_DATA";

    // Strictly positive recent trend — required by the dry-run exploratory mode.
    public bool IsPositive => DataSufficient && TrendPercent > 0m;

    public string Direction =>
        !DataSufficient ? "UNKNOWN"
        : TrendPercent > 0m ? "RISING"
        : TrendPercent < 0m ? "FALLING"
        : "FLAT";

    // Explicit warm-up state for diagnostics: READY only when the guard can actually
    // judge this pair. STALE means enough samples exist but the newest is too old to
    // trust (e.g. history hydrated across a long downtime gap).
    public string WarmupState =>
        Stale ? StateStale
        : DataSufficient ? StateReady
        : SnapshotCount > 0 ? StateWarmingUp
        : StateInsufficientData;

    // Warm-up state for a possibly-missing assessment (no observations at all).
    public static string WarmupStateOf(PriceActionAssessment? assessment) =>
        assessment?.WarmupState ?? StateInsufficientData;
}

// Rolling in-memory history of light snapshots per pair, fed once per cycle.
// Restart cost: history starts empty, so the anti-lag guard abstains (never
// rejects) until it has PriceActionMinSnapshots observations for a pair.
internal sealed class SnapshotPriceHistory
{
    private const int MaxObservationsPerPair = 60;

    private readonly Dictionary<string, List<PriceObservation>> _history =
        new(StringComparer.OrdinalIgnoreCase);

    public void Record(DateTimeOffset utc, IReadOnlyList<InstrumentMarketState> lightStates)
    {
        foreach (var state in lightStates)
        {
            if (state.LastPrice <= 0m)
            {
                continue;
            }

            Record(state.Instrument.Pair, new PriceObservation(utc, state.LastPrice, state.BestBid, state.BestAsk));
        }
    }

    public void Record(string pair, PriceObservation observation)
    {
        if (!_history.TryGetValue(pair, out var observations))
        {
            observations = new List<PriceObservation>();
            _history[pair] = observations;
        }

        observations.Add(observation);
        if (observations.Count > MaxObservationsPerPair)
        {
            observations.RemoveRange(0, observations.Count - MaxObservationsPerPair);
        }
    }

    // Bootstraps the rolling history from snapshots persisted before a restart, so
    // the anti-lag guard is not blind for PriceActionMinSnapshots cycles after every
    // deploy. Records are replayed in utc order per pair; the caller is responsible
    // for only passing RECENT snapshots (a long downtime gap must not be bridged).
    public int Hydrate(IEnumerable<MarketSnapshotRecord> snapshots)
    {
        var loaded = 0;
        foreach (var snapshot in snapshots.OrderBy(record => record.Utc))
        {
            if (snapshot.Last <= 0m)
            {
                continue;
            }

            Record(snapshot.Pair, new PriceObservation(snapshot.Utc, snapshot.Last, snapshot.Bid, snapshot.Ask));
            loaded++;
        }

        return loaded;
    }

    // Assessment over the most recent snapshots, including the current cycle's one.
    // TrendPercent compares the newest last price against the one lookback snapshots
    // earlier; the rolling average covers the same window. When maxSampleAgeMinutes
    // is positive and the newest sample is older than that, the assessment is STALE
    // and DataSufficient is forced false: an outdated series must never pass for a
    // live read of the market.
    public PriceActionAssessment? Assess(
        string pair,
        int lookbackSnapshots,
        int minSnapshots,
        DateTimeOffset? nowUtc = null,
        int maxSampleAgeMinutes = 0)
    {
        if (!_history.TryGetValue(pair, out var observations) || observations.Count == 0)
        {
            return null;
        }

        var lookback = Math.Max(1, lookbackSnapshots);
        var current = observations[^1];
        var required = Math.Max(2, minSnapshots);
        var stale = maxSampleAgeMinutes > 0
            && nowUtc is { } now
            && current.Utc < now.AddMinutes(-maxSampleAgeMinutes);
        var dataSufficient = observations.Count >= required && !stale;

        decimal? trendPercent = null;
        decimal? rollingAverage = null;
        if (dataSufficient)
        {
            var windowStart = Math.Max(0, observations.Count - 1 - lookback);
            var reference = observations[windowStart].Last;
            if (reference > 0m)
            {
                trendPercent = decimal.Round((current.Last - reference) / reference * 100m, 3);
            }

            var window = observations
                .Skip(Math.Max(0, observations.Count - lookback - 1))
                .ToList();
            rollingAverage = decimal.Round(window.Average(observation => observation.Last), 10);
        }

        return new PriceActionAssessment(
            pair,
            observations.Count,
            dataSufficient,
            current.Last,
            trendPercent,
            rollingAverage,
            CountConsecutiveNonRising(observations),
            SamplesRequired: required,
            OldestSampleUtc: observations[0].Utc,
            NewestSampleUtc: current.Utc,
            Stale: stale);
    }

    // Number of most-recent snapshot-to-snapshot steps where the last price did NOT
    // rise (flat counts as non-rising: a flat ticker after a decline is stagnation,
    // not recovery). Resets on the first rising step walking backwards.
    private static int CountConsecutiveNonRising(IReadOnlyList<PriceObservation> observations)
    {
        var count = 0;
        for (var i = observations.Count - 1; i > 0; i--)
        {
            if (observations[i].Last < observations[i - 1].Last
                || observations[i].Last == observations[i - 1].Last)
            {
                count++;
            }
            else
            {
                break;
            }
        }

        return count;
    }
}

// Pure evaluation of the anti-lag LONG entry guard against a strategy's thresholds.
// Kept separate from the tracker so tests can drive it with hand-built assessments.
internal static class PriceActionGuard
{
    // Returns null when the entry is allowed, otherwise a human-readable reason why
    // recent REAL price action contradicts the (lagging) indicator-driven signal.
    // An unavailable / insufficient assessment abstains: the guard only ever blocks
    // on evidence, never on missing data.
    public static string? RejectLongEntryReason(PriceActionAssessment? priceAction, StrategyOptions strategy)
    {
        if (priceAction is null || !priceAction.DataSufficient)
        {
            return null;
        }

        if (strategy.PriceActionMaxDeclinePercent > 0m
            && priceAction.TrendPercent is { } trend
            && trend <= -strategy.PriceActionMaxDeclinePercent)
        {
            return $"recent price action declined {trend:0.###}% over the last {strategy.PriceActionLookbackSnapshots} snapshots (limit -{strategy.PriceActionMaxDeclinePercent:0.###}%)";
        }

        if (strategy.PriceActionMaxNonRisingSnapshots > 0
            && priceAction.ConsecutiveNonRisingSnapshots >= strategy.PriceActionMaxNonRisingSnapshots
            && priceAction.TrendPercent <= 0m)
        {
            return $"price failed to rise for {priceAction.ConsecutiveNonRisingSnapshots} consecutive snapshots with a non-positive trend {priceAction.TrendPercent:0.###}%";
        }

        if (priceAction.RollingAveragePrice is { } average
            && average > 0m
            && priceAction.LastPrice < average
            && priceAction.TrendPercent <= 0m)
        {
            var gapPercent = (priceAction.LastPrice - average) / average * 100m;
            return $"last price {priceAction.LastPrice:0.######} is {gapPercent:0.###}% below the {strategy.PriceActionLookbackSnapshots}-snapshot rolling average with a non-positive trend";
        }

        return null;
    }
}
