namespace TradingBot.FuturesWorker;

// Authoritative LONG entry gate over the 24h price range. A LONG is only admitted
// in the lower part of a robust 24h range AND with a confirmed upward reversal —
// this is what stops the bot from chasing a move that has already happened (buying
// near the daily high) and, symmetrically, from catching a falling knife near the
// low without a confirmed bounce.
//
// It deliberately REUSES the freshness guard's already-computed signals (fresh
// tape, rising snapshots, short slope, local-high distance, entry drift, breakout)
// rather than recomputing them, and adds only the robust 24h range position and the
// rebound-from-low confirmation on top. SHORT is never evaluated here.
internal static class FuturesLongRangeGuard
{
    // Distinct block reasons so the failing condition is visible without parsing text.
    public const string RangePositionTooHigh = "LONG_24H_RANGE_POSITION_TOO_HIGH";
    public const string RangeUnavailable = "LONG_24H_RANGE_UNAVAILABLE";
    public const string RangeTooNarrow = "LONG_24H_RANGE_TOO_NARROW";
    public const string ReboundTooSmall = "LONG_REBOUND_FROM_24H_LOW_TOO_SMALL";
    public const string ShortSlopeNotPositive = "LONG_SHORT_SLOPE_NOT_POSITIVE";
    public const string RisingSnapshotsNotConfirmed = "LONG_RISING_SNAPSHOTS_NOT_CONFIRMED";
    public const string FreshTapeNotConfirmed = "LONG_FRESH_TAPE_NOT_CONFIRMED";
    public const string EntryTooCloseToLocalHigh = "LONG_ENTRY_TOO_CLOSE_TO_LOCAL_HIGH";
    public const string EntryDriftTooHigh = "LONG_ENTRY_DRIFT_TOO_HIGH";

    public static LongRangeResult Evaluate(
        InstrumentMarketState marketState,
        EntryFreshnessResult freshness,
        FuturesDesiredExposure desired,
        FuturesFreshnessOptions thresholds)
    {
        if (desired != FuturesDesiredExposure.Long || !thresholds.LongRangeGuardEnabled)
        {
            return LongRangeResult.NotEvaluated;
        }

        var (entryPrice, entryPriceSource) = ExecutableEntryPrice(marketState);
        var range = Compute24hRange(marketState.Candles, thresholds.RobustRangeMinSampleCount);

        // The range used for the position: robust percentile when available, else the
        // absolute 24h range. Rebound is always measured from the ABSOLUTE 24h low so a
        // fresh new-low print is never treated as "off the low".
        var low = range.RobustLow ?? range.AbsoluteLow;
        var high = range.RobustHigh ?? range.AbsoluteHigh;
        var absoluteLow = range.AbsoluteLow;

        decimal? rawPosition = null;
        decimal? clampedPosition = null;
        decimal? distanceFromLowPct = absoluteLow is > 0m && entryPrice > 0m
            ? (entryPrice - absoluteLow.Value) / absoluteLow.Value * 100m
            : null;

        LongRangeResult Blocked(string code, string reason) => new(
            Evaluated: true, Blocked: true, BlockReasonCode: code, BlockReason: reason,
            EntryPrice: entryPrice, EntryPriceSource: entryPriceSource,
            AbsoluteLow24h: range.AbsoluteLow, AbsoluteHigh24h: range.AbsoluteHigh,
            RobustLow24h: range.RobustLow, RobustHigh24h: range.RobustHigh,
            Range24hSource: range.Source, Range24hSampleCount: range.SampleCount,
            Range24hPositionRaw: rawPosition, Range24hPosition: clampedPosition,
            Max24hRangePositionForLong: thresholds.Max24hRangePositionForLong,
            DistanceFrom24hLowPct: distanceFromLowPct,
            MinReboundFrom24hLowPct: thresholds.MinReboundFrom24hLowPct,
            RisingSnapshotCount: freshness.PositiveStepsInLast3,
            RequiredRisingSnapshotCount: thresholds.RequiredRisingSnapshotCount,
            ShortSlopePct: freshness.ShortSnapshotSlopePct,
            FreshTape: freshness.HasFreshUpwardTape,
            EntryDistanceFromLocalHighPct: freshness.EntryDistanceFromLocalHighPct,
            EntryDriftFromSignalPct: freshness.LivePriceVsSignalClosePct);

        // 1. Range data must be computable — never let a calculation gap silently admit.
        if (low is not { } rangeLow || high is not { } rangeHigh || entryPrice <= 0m)
        {
            return Blocked(RangeUnavailable,
                $"long blocked: 24h range unavailable (source {range.Source}, {range.SampleCount} candles) — cannot place the entry in its range");
        }

        // 2. Range width sanity — a near-flat range makes the position meaningless and
        //    risks a divide-by-tiny amplification.
        var rangeWidthPct = rangeLow > 0m ? (rangeHigh - rangeLow) / rangeLow * 100m : 0m;
        if (rangeHigh <= rangeLow || rangeWidthPct < thresholds.Min24hRangeWidthPct)
        {
            return Blocked(RangeTooNarrow,
                $"long blocked: 24h range too narrow ({rangeWidthPct:0.###}% < {thresholds.Min24hRangeWidthPct:0.###}%, source {range.Source})");
        }

        rawPosition = (entryPrice - rangeLow) / (rangeHigh - rangeLow) * 100m;
        clampedPosition = Math.Clamp(rawPosition.Value, 0m, 100m);

        // 3. Range position: LONG only in the lower band of the robust 24h range.
        if (clampedPosition > thresholds.Max24hRangePositionForLong)
        {
            return Blocked(RangePositionTooHigh,
                $"long blocked: 24h range position {clampedPosition:0.###}% > allowed max {thresholds.Max24hRangePositionForLong:0.###}% (entry {entryPrice:0.######} via {entryPriceSource}, robust low {rangeLow:0.######}, robust high {rangeHigh:0.######}, source {range.Source})");
        }

        // 4. Rebound from the absolute 24h low — do not buy while the low is being made.
        if (distanceFromLowPct is { } rebound && rebound < thresholds.MinReboundFrom24hLowPct)
        {
            return Blocked(ReboundTooSmall,
                $"long blocked: rebound from 24h low {rebound:0.###}% < required {thresholds.MinReboundFrom24hLowPct:0.###}% (entry {entryPrice:0.######}, 24h low {absoluteLow:0.######})");
        }

        // 5. Confirmed upward reversal: rising snapshots, positive short slope, fresh tape.
        if (freshness.PositiveStepsInLast3 < thresholds.RequiredRisingSnapshotCount)
        {
            return Blocked(RisingSnapshotsNotConfirmed,
                $"long blocked: rising snapshots {freshness.PositiveStepsInLast3} < required {thresholds.RequiredRisingSnapshotCount}");
        }

        if (thresholds.RequirePositiveShortSlope && freshness.ShortSnapshotSlopePct is not { } slope)
        {
            return Blocked(ShortSlopeNotPositive,
                "long blocked: short snapshot slope is unavailable (insufficient tape)");
        }

        if (thresholds.RequirePositiveShortSlope && freshness.ShortSnapshotSlopePct <= 0m)
        {
            return Blocked(ShortSlopeNotPositive,
                $"long blocked: short snapshot slope {freshness.ShortSnapshotSlopePct:0.###}% is not positive");
        }

        if (thresholds.RequireFreshTapeForLowRangeLong && !freshness.HasFreshUpwardTape)
        {
            return Blocked(FreshTapeNotConfirmed,
                $"long blocked: fresh upward tape not confirmed (rising snapshots and candle momentum did not both hold)");
        }

        // 6. Local-high guard — never buy right into a local high unless it is a
        //    confirmed breakout. Reuses the freshness guard's measurement.
        if (freshness.EntryDistanceFromLocalHighPct is { } localHighDistance
            && localHighDistance <= thresholds.MaxEntryDistanceFromLocalHighPct
            && !freshness.HasFreshBreakout)
        {
            return Blocked(EntryTooCloseToLocalHigh,
                $"long blocked: entry {localHighDistance:0.###}% below {freshness.LocalHighSource} high (min {thresholds.MaxEntryDistanceFromLocalHighPct:0.###}%), no confirmed breakout");
        }

        // 7. Entry drift — the executable ask must not have run away from the signal.
        if (freshness.LivePriceVsSignalClosePct is { } drift
            && drift > thresholds.MaxEntryDriftFromSignalPct
            && !freshness.HasFreshBreakout)
        {
            return Blocked(EntryDriftTooHigh,
                $"long blocked: executable entry drifted +{drift:0.###}% from signal close (max {thresholds.MaxEntryDriftFromSignalPct:0.###}%), no confirmed breakout");
        }

        return new LongRangeResult(
            Evaluated: true, Blocked: false, BlockReasonCode: null, BlockReason: null,
            EntryPrice: entryPrice, EntryPriceSource: entryPriceSource,
            AbsoluteLow24h: range.AbsoluteLow, AbsoluteHigh24h: range.AbsoluteHigh,
            RobustLow24h: range.RobustLow, RobustHigh24h: range.RobustHigh,
            Range24hSource: range.Source, Range24hSampleCount: range.SampleCount,
            Range24hPositionRaw: rawPosition, Range24hPosition: clampedPosition,
            Max24hRangePositionForLong: thresholds.Max24hRangePositionForLong,
            DistanceFrom24hLowPct: distanceFromLowPct,
            MinReboundFrom24hLowPct: thresholds.MinReboundFrom24hLowPct,
            RisingSnapshotCount: freshness.PositiveStepsInLast3,
            RequiredRisingSnapshotCount: thresholds.RequiredRisingSnapshotCount,
            ShortSlopePct: freshness.ShortSnapshotSlopePct,
            FreshTape: freshness.HasFreshUpwardTape,
            EntryDistanceFromLocalHighPct: freshness.EntryDistanceFromLocalHighPct,
            EntryDriftFromSignalPct: freshness.LivePriceVsSignalClosePct);
    }

    // Executable entry price for a LONG is the current ask; fall back explicitly and
    // tag the source so a fallback is never mistaken for a real ask.
    private static (decimal Price, string Source) ExecutableEntryPrice(InstrumentMarketState marketState)
    {
        if (marketState.Quote?.Ask is > 0m)
        {
            return (marketState.Quote.Ask, "EXECUTABLE_ASK");
        }

        if (marketState.Quote?.Last is > 0m)
        {
            return (marketState.Quote.Last, "QUOTE_LAST");
        }

        return (marketState.LastPrice, "CANDLE_CLOSE");
    }

    // Robust 24h range from the last 96 closed 15m candles. Percentile 5/95 (over
    // candle lows/highs) rejects a single spike; falls back to the absolute range when
    // the sample is too small, and reports which source was used.
    public static Range24h Compute24hRange(IReadOnlyList<Candle> candles, int minSampleCount)
    {
        var recent = candles.TakeLast(Math.Min(96, candles.Count)).ToList();
        if (recent.Count < 2)
        {
            return new Range24h(null, null, null, null, "UNAVAILABLE", recent.Count);
        }

        var absoluteLow = recent.Min(candle => candle.Low);
        var absoluteHigh = recent.Max(candle => candle.High);
        if (recent.Count >= Math.Max(2, minSampleCount))
        {
            var lows = recent.Select(candle => candle.Low).OrderBy(value => value).ToList();
            var highs = recent.Select(candle => candle.High).OrderBy(value => value).ToList();
            return new Range24h(
                absoluteLow,
                absoluteHigh,
                Percentile(lows, 5m),
                Percentile(highs, 95m),
                "PERCENTILE_5_95",
                recent.Count);
        }

        return new Range24h(absoluteLow, absoluteHigh, null, null, "ABSOLUTE_24H", recent.Count);
    }

    // Linear-interpolation percentile over an already-sorted ascending list.
    private static decimal Percentile(IReadOnlyList<decimal> sorted, decimal percentile)
    {
        if (sorted.Count == 0)
        {
            return 0m;
        }

        if (sorted.Count == 1)
        {
            return sorted[0];
        }

        var rank = percentile / 100m * (sorted.Count - 1);
        var lowerIndex = (int)Math.Floor(rank);
        var upperIndex = (int)Math.Ceiling(rank);
        if (lowerIndex == upperIndex)
        {
            return sorted[lowerIndex];
        }

        var weight = rank - lowerIndex;
        return sorted[lowerIndex] + (sorted[upperIndex] - sorted[lowerIndex]) * weight;
    }
}

internal sealed record Range24h(
    decimal? AbsoluteLow,
    decimal? AbsoluteHigh,
    decimal? RobustLow,
    decimal? RobustHigh,
    string Source,
    int SampleCount);

internal sealed record LongRangeResult(
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
    decimal Max24hRangePositionForLong,
    decimal? DistanceFrom24hLowPct,
    decimal MinReboundFrom24hLowPct,
    int RisingSnapshotCount,
    int RequiredRisingSnapshotCount,
    decimal? ShortSlopePct,
    bool FreshTape,
    decimal? EntryDistanceFromLocalHighPct,
    decimal? EntryDriftFromSignalPct)
{
    public static readonly LongRangeResult NotEvaluated = new(
        Evaluated: false, Blocked: false, BlockReasonCode: null, BlockReason: null,
        EntryPrice: 0m, EntryPriceSource: "NONE",
        AbsoluteLow24h: null, AbsoluteHigh24h: null, RobustLow24h: null, RobustHigh24h: null,
        Range24hSource: "NONE", Range24hSampleCount: 0,
        Range24hPositionRaw: null, Range24hPosition: null,
        Max24hRangePositionForLong: 0m, DistanceFrom24hLowPct: null, MinReboundFrom24hLowPct: 0m,
        RisingSnapshotCount: 0, RequiredRisingSnapshotCount: 0,
        ShortSlopePct: null, FreshTape: false,
        EntryDistanceFromLocalHighPct: null, EntryDriftFromSignalPct: null);
}
