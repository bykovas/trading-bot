using TradingBot.Core.Signals;

namespace TradingBot.FuturesWorker;

// SHORT entry context + anti-knife gate. Mirror of FuturesLongRangeGuard +
// FuturesEntryFreshnessGuard, "top down" instead of "bottom up":
//   - executable entry is the BID (a short sells into the bid);
//   - require a confirmed PULLBACK below the 24h high (don't sell while the high is
//     still being made) — mirror of rebound-from-low;
//   - require a fresh DOWNWARD tape (falling snapshots + negative slope + non-positive
//     candle momentum) — mirror of the fresh upward tape;
//   - block shorting into a local LOW / support unless a confirmed BREAKDOWN, and block
//     chasing a dump that drifted far BELOW the signal close — mirrors local-high / drift;
//   - wick 24h position is diagnostic only: mid-range reclaim-down after wide spikes may
//     pass (mirror of the LONG reclaim). LONG is never evaluated here.
// Shared mechanical thresholds (tape counts, lookbacks, breakdown buffer, candle momentum)
// come from FuturesFreshnessOptions with mirrored direction; SHORT-specific structural
// thresholds come from FuturesShortOptions.
internal static class FuturesShortEntryGuard
{
    public const string RangeUnavailable = "SHORT_RANGE_UNAVAILABLE";
    public const string PullbackTooSmall = "SHORT_PULLBACK_FROM_24H_HIGH_TOO_SMALL";
    public const string SlopeNotNegative = "SHORT_SLOPE_NOT_NEGATIVE";
    public const string FallingSnapshotsNotConfirmed = "SHORT_FALLING_SNAPSHOTS_NOT_CONFIRMED";
    public const string FreshTapeNotConfirmed = "SHORT_FRESH_TAPE_NOT_CONFIRMED";
    public const string EntryTooCloseToLocalLow = "SHORT_ENTRY_TOO_CLOSE_TO_LOCAL_LOW";
    public const string EntryDriftTooHigh = "SHORT_ENTRY_DRIFT_TOO_HIGH";

    public const string RangeBasisClosePercentile = "CLOSE_PERCENTILE_96";
    public const string RangeBasisRecentSwing = "RECENT_SWING_16";
    public const string RangeBasisWick24h = "WICK_24H_DIAGNOSTIC";

    public static ShortEntryResult Evaluate(
        InstrumentMarketState marketState,
        IReadOnlyList<PriceObservation> observations,
        FuturesDesiredExposure desired,
        FuturesShortOptions shorts,
        FuturesFreshnessOptions freshness)
    {
        if (desired != FuturesDesiredExposure.Short || !shorts.RangeGuardEnabled)
        {
            return ShortEntryResult.NotEvaluated;
        }

        var (entryPrice, entryPriceSource) = ExecutableEntryPrice(marketState);
        var wickRange = FuturesLongRangeGuard.Compute24hRange(marketState.Candles, freshness.RobustRangeMinSampleCount);
        var closePercentile = FuturesLongRangeGuard.ClosePercentileRank(marketState.Candles, entryPrice, lookback: 96);
        var swing = FuturesLongRangeGuard.RecentSwingPosition(marketState.Candles, entryPrice, lookback: 16);

        var low = wickRange.RobustLow ?? wickRange.AbsoluteLow;
        var high = wickRange.RobustHigh ?? wickRange.AbsoluteHigh;
        var absoluteHigh = wickRange.AbsoluteHigh;

        decimal? rawWickPosition = null;
        decimal? clampedWickPosition = null;
        if (low is { } rangeLow && high is { } rangeHigh && entryPrice > 0m && rangeHigh > rangeLow)
        {
            rawWickPosition = (entryPrice - rangeLow) / (rangeHigh - rangeLow) * 100m;
            clampedWickPosition = Math.Clamp(rawWickPosition.Value, 0m, 100m);
        }

        decimal? distanceFromHighPct = absoluteHigh is > 0m && entryPrice > 0m
            ? (absoluteHigh.Value - entryPrice) / absoluteHigh.Value * 100m
            : null;

        var tape = EvaluateDownwardTape(observations, freshness);
        var candleMomentumPct = RecentCandleMomentumPct(marketState.Candles, freshness.ContinuationCandleMomentumLookback);
        // Mirror of the LONG momentum gate: a fresh micro down-tape must not rescue a short
        // when the 15m candles are turning UP. Non-positive momentum (<= -floor) confirms.
        var candleMomentumOk = candleMomentumPct is not { } momentum || momentum <= -freshness.MinContinuationCandleMomentumPct;
        var freshDownwardTape = tape.HasFreshDownwardTape && candleMomentumOk;

        var localLow = LocalExecutionLow(marketState.Candles, freshness.LocalHighLookbackClosedCandles);
        var entryDistanceAboveLocalLowPct = localLow.Low is > 0m && entryPrice > 0m
            ? (entryPrice - localLow.Low.Value) / localLow.Low.Value * 100m
            : (decimal?)null;
        var signalClose = marketState.LastPrice;
        var driftBelowSignalPct = signalClose > 0m && entryPrice > 0m
            ? (signalClose - entryPrice) / signalClose * 100m
            : (decimal?)null;
        var freshBreakdown = FreshBreakdown(marketState, observations, entryPrice, localLow.Low, freshness);

        var rangeBasis = closePercentile is not null ? RangeBasisClosePercentile
            : swing.PositionPct is not null ? RangeBasisRecentSwing
            : RangeBasisWick24h;
        var primaryPosition = closePercentile ?? swing.PositionPct ?? clampedWickPosition;

        ShortEntryResult Blocked(string code, string reason) => Build(
            blocked: true, code, reason, entryPrice, entryPriceSource, wickRange,
            rawWickPosition, clampedWickPosition, distanceFromHighPct, tape, freshDownwardTape,
            entryDistanceAboveLocalLowPct, driftBelowSignalPct, freshBreakdown,
            shorts, rangeBasis, closePercentile, swing.PositionPct, primaryPosition);

        if (entryPrice <= 0m)
        {
            return Blocked(RangeUnavailable,
                "short blocked: executable entry price unavailable — cannot evaluate reclaim/tape context");
        }

        if (distanceFromHighPct is { } pullback && pullback < shorts.MinPullbackFrom24hHighPct)
        {
            return Blocked(PullbackTooSmall,
                $"short blocked: pullback from 24h high {pullback:0.###}% < required {shorts.MinPullbackFrom24hHighPct:0.###}% (entry {entryPrice:0.######}, 24h high {absoluteHigh:0.######})");
        }

        if (tape.FallingSteps < shorts.RequiredFallingSnapshotCount)
        {
            return Blocked(FallingSnapshotsNotConfirmed,
                $"short blocked: falling snapshots {tape.FallingSteps} < required {shorts.RequiredFallingSnapshotCount}");
        }

        if (shorts.RequireNegativeShortSlope && tape.ShortSlopePct is not { } slope)
        {
            return Blocked(SlopeNotNegative,
                "short blocked: short snapshot slope is unavailable (insufficient tape)");
        }

        if (shorts.RequireNegativeShortSlope && tape.ShortSlopePct >= 0m)
        {
            return Blocked(SlopeNotNegative,
                $"short blocked: short snapshot slope {tape.ShortSlopePct:0.###}% is not negative");
        }

        if (shorts.RequireFreshTapeForHighRangeShort && !freshDownwardTape)
        {
            return Blocked(FreshTapeNotConfirmed,
                "short blocked: fresh downward tape not confirmed (falling snapshots and candle momentum did not both hold)");
        }

        if (entryDistanceAboveLocalLowPct is { } aboveLocalLow
            && aboveLocalLow <= freshness.MaxEntryDistanceFromLocalHighPct
            && !freshBreakdown)
        {
            return Blocked(EntryTooCloseToLocalLow,
                $"short blocked: entry {aboveLocalLow:0.###}% above {localLow.Source} low (min {freshness.MaxEntryDistanceFromLocalHighPct:0.###}%), no confirmed breakdown");
        }

        if (driftBelowSignalPct is { } drift
            && drift > freshness.MaxEntryDriftFromSignalPct
            && !freshBreakdown)
        {
            return Blocked(EntryDriftTooHigh,
                $"short blocked: executable entry drifted -{drift:0.###}% from signal close (max {freshness.MaxEntryDriftFromSignalPct:0.###}%), no confirmed breakdown");
        }

        return Build(
            blocked: false, null, null, entryPrice, entryPriceSource, wickRange,
            rawWickPosition, clampedWickPosition, distanceFromHighPct, tape, freshDownwardTape,
            entryDistanceAboveLocalLowPct, driftBelowSignalPct, freshBreakdown,
            shorts, rangeBasis, closePercentile, swing.PositionPct, primaryPosition);
    }

    private static ShortEntryResult Build(
        bool blocked,
        string? code,
        string? reason,
        decimal entryPrice,
        string entryPriceSource,
        Range24h range,
        decimal? rawWickPosition,
        decimal? clampedWickPosition,
        decimal? distanceFromHighPct,
        (decimal? LastStepPct, decimal? ShortSlopePct, int FallingSteps, bool HasFreshDownwardTape) tape,
        bool freshDownwardTape,
        decimal? entryDistanceAboveLocalLowPct,
        decimal? driftBelowSignalPct,
        bool freshBreakdown,
        FuturesShortOptions shorts,
        string rangeBasis,
        decimal? closePercentile,
        decimal? recentSwingPosition,
        decimal? primaryPosition) =>
        new(
            Evaluated: true, Blocked: blocked, BlockReasonCode: code, BlockReason: reason,
            EntryPrice: entryPrice, EntryPriceSource: entryPriceSource,
            AbsoluteLow24h: range.AbsoluteLow, AbsoluteHigh24h: range.AbsoluteHigh,
            RobustLow24h: range.RobustLow, RobustHigh24h: range.RobustHigh,
            Range24hSource: range.Source, Range24hSampleCount: range.SampleCount,
            Range24hPositionRaw: rawWickPosition, Range24hPosition: clampedWickPosition,
            Min24hRangePositionForShort: shorts.Min24hRangePositionForShort,
            DistanceFrom24hHighPct: distanceFromHighPct,
            MinPullbackFrom24hHighPct: shorts.MinPullbackFrom24hHighPct,
            FallingSnapshotCount: tape.FallingSteps,
            RequiredFallingSnapshotCount: shorts.RequiredFallingSnapshotCount,
            ShortSlopePct: tape.ShortSlopePct,
            FreshTape: freshDownwardTape,
            EntryDistanceFromLocalLowPct: entryDistanceAboveLocalLowPct,
            EntryDriftFromSignalPct: driftBelowSignalPct,
            HasFreshBreakdown: freshBreakdown,
            RangeBasis: rangeBasis,
            ClosePercentile: closePercentile,
            RecentSwingPosition: recentSwingPosition,
            PrimaryRangePosition: primaryPosition);

    private static (decimal Price, string Source) ExecutableEntryPrice(InstrumentMarketState marketState)
    {
        if (marketState.Quote?.Bid is > 0m)
        {
            return (marketState.Quote.Bid, "EXECUTABLE_BID");
        }

        if (marketState.Quote?.Last is > 0m)
        {
            return (marketState.Quote.Last, "QUOTE_LAST");
        }

        return (marketState.LastPrice, "CANDLE_CLOSE");
    }

    private static decimal? RecentCandleMomentumPct(IReadOnlyList<Candle> candles, int lookback)
    {
        var bars = Math.Max(1, lookback);
        if (candles.Count <= bars)
        {
            return null;
        }

        var latest = candles[^1].Close;
        var reference = candles[^(bars + 1)].Close;
        return reference > 0m ? (latest - reference) / reference * 100m : (decimal?)null;
    }

    private static (decimal? Low, string? Source) LocalExecutionLow(IReadOnlyList<Candle> candles, int lookbackClosedCandles)
    {
        var count = Math.Clamp(lookbackClosedCandles <= 0 ? 2 : lookbackClosedCandles, 1, 8);
        var recent = candles.TakeLast(Math.Min(count, candles.Count)).ToList();
        if (recent.Count == 0)
        {
            return (null, null);
        }

        return (recent.Min(candle => candle.Low), $"last-{recent.Count}-closed-15m");
    }

    private static bool FreshBreakdown(
        InstrumentMarketState marketState,
        IReadOnlyList<PriceObservation> observations,
        decimal entryPrice,
        decimal? localLow,
        FuturesFreshnessOptions thresholds)
    {
        if (localLow is not { } lowRef || lowRef <= 0m || entryPrice <= 0m)
        {
            return false;
        }

        var belowLowPct = (lowRef - entryPrice) / lowRef * 100m;
        if (belowLowPct < thresholds.BreakoutMinAboveRecentHighPct || !SpreadOk(marketState, thresholds))
        {
            return false;
        }

        var count = Math.Max(1, thresholds.BreakoutHoldSnapshotCount);
        var tape = observations.TakeLast(Math.Min(count, observations.Count)).ToList();
        if (tape.Count < count)
        {
            return false;
        }

        var trigger = lowRef * (1m - thresholds.BreakoutMinAboveRecentHighPct / 100m);
        return tape.All(observation => observation.Last <= trigger);
    }

    private static bool SpreadOk(InstrumentMarketState marketState, FuturesFreshnessOptions thresholds)
    {
        var bid = marketState.BestBid;
        var ask = marketState.BestAsk;
        if (bid <= 0m || ask <= 0m || ask < bid)
        {
            return true;
        }

        var mid = (bid + ask) / 2m;
        var spreadPct = mid == 0m ? 0m : (ask - bid) / mid * 100m;
        return spreadPct <= Math.Max(0m, thresholds.NearHighMaxDistanceFromRecentHighPct);
    }

    private static (decimal? LastStepPct, decimal? ShortSlopePct, int FallingSteps, bool HasFreshDownwardTape) EvaluateDownwardTape(
        IReadOnlyList<PriceObservation> observations,
        FuturesFreshnessOptions thresholds)
    {
        var count = Math.Max(2, thresholds.FreshTapeSnapshotCount);
        var tape = observations.TakeLast(Math.Min(count, observations.Count)).ToList();
        if (tape.Count < count)
        {
            return (null, null, 0, false);
        }

        var fallingSteps = 0;
        for (var i = 1; i < tape.Count; i++)
        {
            if (tape[i].Last < tape[i - 1].Last)
            {
                fallingSteps++;
            }
        }

        var first = tape[0].Last;
        var latest = tape[^1].Last;
        var previous = tape[^2].Last;
        var shortSlope = first > 0m ? (latest - first) / first * 100m : (decimal?)null;
        var lastStep = previous > 0m ? (latest - previous) / previous * 100m : (decimal?)null;
        var average = tape.Average(item => item.Last);
        var hasFreshTape =
            shortSlope is { } slope
            && slope <= -thresholds.FreshTapeMinSlopePct
            && fallingSteps >= thresholds.FreshTapeMinPositiveSteps
            && latest <= average;

        return (lastStep, shortSlope, fallingSteps, hasFreshTape);
    }
}

internal sealed record ShortEntryResult(
    bool Evaluated,
    bool Blocked,
    string? BlockReasonCode,
    string? BlockReason,
    decimal EntryPrice,
    string EntryPriceSource,
    decimal? AbsoluteLow24h,
    decimal? AbsoluteHigh24h,
    decimal? RobustLow24h,
    decimal? RobustHigh24h,
    string Range24hSource,
    int Range24hSampleCount,
    decimal? Range24hPositionRaw,
    decimal? Range24hPosition,
    decimal Min24hRangePositionForShort,
    decimal? DistanceFrom24hHighPct,
    decimal MinPullbackFrom24hHighPct,
    int FallingSnapshotCount,
    int RequiredFallingSnapshotCount,
    decimal? ShortSlopePct,
    bool FreshTape,
    decimal? EntryDistanceFromLocalLowPct,
    decimal? EntryDriftFromSignalPct,
    bool HasFreshBreakdown,
    string RangeBasis = "NONE",
    decimal? ClosePercentile = null,
    decimal? RecentSwingPosition = null,
    decimal? PrimaryRangePosition = null)
{
    public static readonly ShortEntryResult NotEvaluated = new(
        Evaluated: false, Blocked: false, BlockReasonCode: null, BlockReason: null,
        EntryPrice: 0m, EntryPriceSource: "NONE",
        AbsoluteLow24h: null, AbsoluteHigh24h: null, RobustLow24h: null, RobustHigh24h: null,
        Range24hSource: "NONE", Range24hSampleCount: 0,
        Range24hPositionRaw: null, Range24hPosition: null,
        Min24hRangePositionForShort: 0m, DistanceFrom24hHighPct: null, MinPullbackFrom24hHighPct: 0m,
        FallingSnapshotCount: 0, RequiredFallingSnapshotCount: 0,
        ShortSlopePct: null, FreshTape: false,
        EntryDistanceFromLocalLowPct: null, EntryDriftFromSignalPct: null, HasFreshBreakdown: false);
}
