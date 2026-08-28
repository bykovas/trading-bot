namespace TradingBot.FuturesWorker;

// LONG entry context + anti-knife checks. The old hard veto
// "24h wick position > Max24hRangePositionForLong" is removed: wick mid-range
// (e.g. reclaim 100 after spikes 10–150) is diagnostic only. Late-entry protection
// lives here because it needs the primary range zone.
//
// This guard still:
//   - computes close-percentile / recent-swing / wick-24h diagnostics (rangeBasis);
//   - blocks falling-knife entries (no rebound / no rising tape) when enabled;
//   - never blocks solely because wick 24h position is mid-range.
// SHORT is never evaluated here.
internal static class FuturesLongRangeGuard
{
    public const string RangeUnavailable = "LONG_24H_RANGE_UNAVAILABLE";
    public const string ReboundTooSmall = "LONG_REBOUND_FROM_24H_LOW_TOO_SMALL";
    public const string ShortSlopeNotPositive = "LONG_SHORT_SLOPE_NOT_POSITIVE";
    public const string RisingSnapshotsNotConfirmed = "LONG_RISING_SNAPSHOTS_NOT_CONFIRMED";
    public const string FreshTapeNotConfirmed = "LONG_FRESH_TAPE_NOT_CONFIRMED";
    public const string UpperRangeFreshTapeNotEnough = "LONG_UPPER_RANGE_FRESH_TAPE_NOT_ENOUGH";
    public const string StrongConfirmationMissing = "LONG_LOW_RANGE_STRONG_CONFIRMATION_MISSING";
    public const string EntryTooCloseToLocalHigh = "LONG_ENTRY_TOO_CLOSE_TO_LOCAL_HIGH";
    public const string EntryDriftTooHigh = "LONG_ENTRY_DRIFT_TOO_HIGH";

    // Kept for diagnostics / SQL compatibility; no longer used as an admit veto.
    public const string RangePositionTooHigh = "LONG_24H_RANGE_POSITION_TOO_HIGH";
    public const string RangeTooNarrow = "LONG_24H_RANGE_TOO_NARROW";

    public const string RangeBasisClosePercentile = "CLOSE_PERCENTILE_96";
    public const string RangeBasisRecentSwing = "RECENT_SWING_16";
    public const string RangeBasisWick24h = "WICK_24H_DIAGNOSTIC";

    public static LongRangeResult Evaluate(
        InstrumentMarketState marketState,
        EntryFreshnessResult freshness,
        FuturesDesiredExposure desired,
        FuturesFreshnessOptions thresholds)
    {
        if (desired != FuturesDesiredExposure.Long)
        {
            return LongRangeResult.NotEvaluated;
        }

        // LongRangeGuardEnabled toggles the low-range RELAXATION only, never the
        // protective vetoes. Late-entry protection (upper-range breakout rule,
        // local-high, drift) has no other owner since the freshness guard stopped
        // vetoing, so a single flag must not be able to switch it all off.
        var relaxationEnabled = thresholds.LongRangeGuardEnabled;

        var (entryPrice, entryPriceSource) = ExecutableEntryPrice(marketState);
        var wickRange = Compute24hRange(marketState.Candles, thresholds.RobustRangeMinSampleCount);
        var closePercentile = ClosePercentileRank(marketState.Candles, entryPrice, lookback: 96);
        var swing = RecentSwingPosition(marketState.Candles, entryPrice, lookback: 16);

        var low = wickRange.RobustLow ?? wickRange.AbsoluteLow;
        var high = wickRange.RobustHigh ?? wickRange.AbsoluteHigh;
        var absoluteLow = wickRange.AbsoluteLow;

        decimal? rawWickPosition = null;
        decimal? clampedWickPosition = null;
        if (low is { } rangeLow && high is { } rangeHigh && entryPrice > 0m && rangeHigh > rangeLow)
        {
            rawWickPosition = (entryPrice - rangeLow) / (rangeHigh - rangeLow) * 100m;
            clampedWickPosition = Math.Clamp(rawWickPosition.Value, 0m, 100m);
        }

        decimal? distanceFromLowPct = absoluteLow is > 0m && entryPrice > 0m
            ? (entryPrice - absoluteLow.Value) / absoluteLow.Value * 100m
            : null;

        // Primary structural basis for Dip/Reclaim labeling: where the entry sits in
        // the recent close distribution. Fall back to swing, then wick diagnostic.
        var rangeBasis = closePercentile is not null ? RangeBasisClosePercentile
            : swing.PositionPct is not null ? RangeBasisRecentSwing
            : RangeBasisWick24h;
        var primaryPosition = closePercentile ?? swing.PositionPct ?? clampedWickPosition;
        var zone = ZoneOf(primaryPosition, thresholds);
        // With the relaxation disabled a LOW zone behaves like MID: strict fresh tape and
        // anti-chase everywhere, i.e. the pre-relaxation behaviour.
        if (!relaxationEnabled && zone == "LOW")
        {
            zone = "MID";
        }

        var antiChaseApplied = !relaxationEnabled
            || primaryPosition is not { } position
            || position >= thresholds.AntiChaseMinRangePositionPct;
        var (confirmations, hasStrongConfirmation) = CountLowRangeConfirmations(marketState, freshness, thresholds);
        var requiredConfirmations = thresholds.LowRangeMinConfirmations;
        var atrPct = CalculateAtrPct(marketState.Candles, entryPrice);
        var effectiveMaxDriftPct = atrPct is { } atr
            ? Math.Max(thresholds.MaxEntryDriftFromSignalPct, thresholds.DriftAtrMultiple * atr)
            : thresholds.MaxEntryDriftFromSignalPct;

        LongRangeResult Blocked(string code, string reason) => Build(
            blocked: true, code, reason,
            entryPrice, entryPriceSource, wickRange,
            rawWickPosition, clampedWickPosition, distanceFromLowPct,
            freshness, thresholds, rangeBasis, closePercentile, swing.PositionPct, primaryPosition,
            zone, antiChaseApplied, confirmations, requiredConfirmations, effectiveMaxDriftPct, atrPct);

        // Wick mid-range is NEVER a hard veto (reclaim after wide spikes must pass).
        // Still require computable entry + anti-knife confirmation.
        if (entryPrice <= 0m)
        {
            return Blocked(RangeUnavailable,
                "long blocked: executable entry price unavailable — cannot evaluate reclaim/tape context");
        }

        if (distanceFromLowPct is { } rebound && rebound < thresholds.MinReboundFrom24hLowPct)
        {
            return Blocked(ReboundTooSmall,
                $"long blocked: rebound from 24h low {rebound:0.###}% < required {thresholds.MinReboundFrom24hLowPct:0.###}% (entry {entryPrice:0.######}, 24h low {absoluteLow:0.######})");
        }

        // TODO (measured 2026-08-28, left as-is deliberately): this condition is inverted
        // from what its name suggests. It blocks the top of the range ONLY WHILE the tape
        // is rising, so an impulse that has just run out of breath - the tape flat at the
        // high - passes, and a live one is refused. MSTRX/USD on 2026-08-28 went through
        // exactly there: blocked at 00:16 and 00:18 while the tape rose, admitted at 00:20
        // at the same 139.31 once it stopped, at 100% of the range after a two-day climb.
        //
        // Both obvious repairs were measured over 2026-07-08..08-21 (28,549 walked entries)
        // and NEITHER is an improvement, which is why the code stays wrong on purpose:
        //   * blocking the top unconditionally costs 260 USD - entries at exactly 100% of
        //     the range return +0.063%/trade, and 90-100% carries +3,700 of the +3,705 the
        //     whole strategy makes. The losing zone is the MIDDLE, 50-80% at -0.101%.
        //   * entries at the top are not even a distinct population: median -0.67% against
        //     -0.64% elsewhere, 7.1% reach +4% against 7.5%, 37.9% stop out against 37.2%.
        // Within the top the only losing slice is coins up 2-10% over the prior day; both
        // the quiet ones and the genuinely running ones (>10%) pay.
        //
        // So the fix is not a threshold. If this is ever revisited, the question worth
        // asking is why the guard reads a two-hour horizon at all: every extension test
        // here measures against the fast EMA, so a coin that doubled over two days but has
        // been flat for two hours reads as unextended. That is the real blind spot.
        if (zone == "UPPER" && freshness.HasFreshUpwardTape && !freshness.HasFreshBreakout)
        {
            return Blocked(UpperRangeFreshTapeNotEnough,
                $"long blocked: upper-range continuation at {primaryPosition:0.###}% of primary range; fresh tape is not sufficient without confirmed breakout");
        }

        if (zone == "LOW")
        {
            if (confirmations < requiredConfirmations)
            {
                return Blocked(FreshTapeNotConfirmed,
                    $"long blocked: low-range confirmations {confirmations}/{requiredConfirmations} below required minimum");
            }

            // The count alone can be met by the two single-observation signals (one
            // positive snapshot step + one green candle) while the structural ones are
            // absent — a dead-cat bounce inside a downtrend. Require at least one
            // structural confirmation: fresh upward tape or multi-candle momentum.
            if (thresholds.LowRangeRequireStrongConfirmation && !hasStrongConfirmation)
            {
                return Blocked(StrongConfirmationMissing,
                    $"long blocked: low-range confirmations {confirmations}/{requiredConfirmations} met, but neither a fresh upward tape nor {thresholds.ContinuationCandleMomentumLookback}-candle momentum >= {thresholds.MinContinuationCandleMomentumPct:0.###}% confirmed the reversal");
            }
        }
        else if (thresholds.RequireFreshTapeForLowRangeLong
            && !freshness.HasFreshUpwardTape
            && !freshness.HasFreshBreakout)
        {
            // HasFreshUpwardTape carries the STRICTER freshContinuationTape (raw tape AND
            // candle momentum), while HasFreshBreakout was computed from the RAW tape. A
            // confirmed breakout could therefore be refused here for the very tape it had
            // already satisfied. A breakout exempts this veto, as it does the anti-chase
            // ones below.
            return Blocked(FreshTapeNotConfirmed,
                "long blocked: fresh upward tape not confirmed (rising snapshots and candle momentum did not both hold)");
        }
        else if (!thresholds.RequireFreshTapeForLowRangeLong)
        {
            // Fresh tape is the gate that implies rising snapshots and a positive slope.
            // When it is switched off, fall back to those weaker vetoes so turning the
            // flag off cannot leave the tape completely unchecked.
            if (freshness.PositiveStepsInLast3 < thresholds.RequiredRisingSnapshotCount
                && !freshness.HasFreshBreakout)
            {
                return Blocked(RisingSnapshotsNotConfirmed,
                    $"long blocked: rising snapshots {freshness.PositiveStepsInLast3} < required {thresholds.RequiredRisingSnapshotCount}");
            }

            if (thresholds.RequirePositiveShortSlope
                && freshness.ShortSnapshotSlopePct is not > 0m
                && !freshness.HasFreshBreakout)
            {
                return Blocked(ShortSlopeNotPositive,
                    $"long blocked: short snapshot slope {freshness.ShortSnapshotSlopePct:0.###}% is not positive");
            }
        }

        if (antiChaseApplied
            && freshness.EntryDistanceFromLocalHighPct is { } localHighDistance
            && localHighDistance <= thresholds.MaxEntryDistanceFromLocalHighPct
            && !freshness.HasFreshBreakout)
        {
            return Blocked(EntryTooCloseToLocalHigh,
                $"long blocked: entry {localHighDistance:0.###}% below {freshness.LocalHighSource} high (min {thresholds.MaxEntryDistanceFromLocalHighPct:0.###}%), no confirmed breakout");
        }

        if (antiChaseApplied
            && freshness.LivePriceVsSignalClosePct is { } drift
            && drift > effectiveMaxDriftPct
            && !freshness.HasFreshBreakout)
        {
            return Blocked(EntryDriftTooHigh,
                $"long blocked: executable entry drifted +{drift:0.###}% from signal close (max {effectiveMaxDriftPct:0.###}%), no confirmed breakout");
        }

        return Build(
            blocked: false, null, null,
            entryPrice, entryPriceSource, wickRange,
            rawWickPosition, clampedWickPosition, distanceFromLowPct,
            freshness, thresholds, rangeBasis, closePercentile, swing.PositionPct, primaryPosition,
            zone, antiChaseApplied, confirmations, requiredConfirmations, effectiveMaxDriftPct, atrPct);
    }

    private static LongRangeResult Build(
        bool blocked,
        string? code,
        string? reason,
        decimal entryPrice,
        string entryPriceSource,
        Range24h range,
        decimal? rawWickPosition,
        decimal? clampedWickPosition,
        decimal? distanceFromLowPct,
        EntryFreshnessResult freshness,
        FuturesFreshnessOptions thresholds,
        string rangeBasis,
        decimal? closePercentile,
        decimal? recentSwingPosition,
        decimal? primaryPosition,
        string zone,
        bool antiChaseApplied,
        int confirmationsMet,
        int confirmationsRequired,
        decimal effectiveMaxDriftPct,
        decimal? atrPct) =>
        new(
            Evaluated: true, Blocked: blocked, BlockReasonCode: code, BlockReason: reason,
            EntryPrice: entryPrice, EntryPriceSource: entryPriceSource,
            AbsoluteLow24h: range.AbsoluteLow, AbsoluteHigh24h: range.AbsoluteHigh,
            RobustLow24h: range.RobustLow, RobustHigh24h: range.RobustHigh,
            Range24hSource: range.Source, Range24hSampleCount: range.SampleCount,
            Range24hPositionRaw: rawWickPosition, Range24hPosition: clampedWickPosition,
            Max24hRangePositionForLong: thresholds.Max24hRangePositionForLong,
            DistanceFrom24hLowPct: distanceFromLowPct,
            MinReboundFrom24hLowPct: thresholds.MinReboundFrom24hLowPct,
            RisingSnapshotCount: freshness.PositiveStepsInLast3,
            RequiredRisingSnapshotCount: thresholds.RequiredRisingSnapshotCount,
            ShortSlopePct: freshness.ShortSnapshotSlopePct,
            FreshTape: freshness.HasFreshUpwardTape,
            EntryDistanceFromLocalHighPct: freshness.EntryDistanceFromLocalHighPct,
            EntryDriftFromSignalPct: freshness.LivePriceVsSignalClosePct,
            RangeBasis: rangeBasis,
            ClosePercentile: closePercentile,
            RecentSwingPosition: recentSwingPosition,
            PrimaryRangePosition: primaryPosition,
            Zone: zone,
            AntiChaseApplied: antiChaseApplied,
            ConfirmationsMet: confirmationsMet,
            ConfirmationsRequired: confirmationsRequired,
            EffectiveMaxDriftPct: effectiveMaxDriftPct,
            AtrPct: atrPct);

    private static string ZoneOf(decimal? primaryPosition, FuturesFreshnessOptions thresholds)
    {
        if (primaryPosition is not { } position)
        {
            return "MID";
        }

        if (position < thresholds.AntiChaseMinRangePositionPct)
        {
            return "LOW";
        }

        return position >= thresholds.MaxContinuationRangePositionPct ? "UPPER" : "MID";
    }

    // Counts the low-range reversal confirmations and reports whether at least one of
    // them is STRUCTURAL. Structural = fresh upward tape (3 snapshots) or multi-candle
    // momentum; the remaining two (a single positive snapshot step, a single green
    // candle) are one-observation signals that a dead-cat bounce also produces.
    private static (int Confirmations, bool HasStrong) CountLowRangeConfirmations(
        InstrumentMarketState marketState,
        EntryFreshnessResult freshness,
        FuturesFreshnessOptions thresholds)
    {
        var confirmations = 0;
        var hasStrong = false;
        if (freshness.HasFreshUpwardTape)
        {
            confirmations++;
            hasStrong = true;
        }

        if (freshness.RecentCandleMomentumPct is { } momentum && momentum >= thresholds.MinContinuationCandleMomentumPct)
        {
            confirmations++;
            hasStrong = true;
        }

        if (freshness.LastSnapshotStepPct is > 0m)
        {
            confirmations++;
        }

        if (LastClosedCandleIsGreen(marketState.Candles))
        {
            confirmations++;
        }

        return (confirmations, hasStrong);
    }

    private static bool LastClosedCandleIsGreen(IReadOnlyList<Candle> candles)
    {
        if (candles.Count == 0)
        {
            return false;
        }

        var candle = candles[^1];
        return candle.Close > candle.Open;
    }

    private static decimal? CalculateAtrPct(IReadOnlyList<Candle> candles, decimal entryPrice)
    {
        var atr = AtrIndicator.CalculateLatestClosedAtr(candles, 14);
        return atr is > 0m && entryPrice > 0m
            ? atr.Value / entryPrice * 100m
            : null;
    }

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

    // Rank of entryPrice among the last `lookback` closes, 0..100.
    // Spike wicks do not move this; a reclaim to prior value sits near historical closes.
    public static decimal? ClosePercentileRank(IReadOnlyList<Candle> candles, decimal entryPrice, int lookback)
    {
        if (entryPrice <= 0m || candles.Count < 2)
        {
            return null;
        }

        var closes = candles.TakeLast(Math.Min(lookback, candles.Count)).Select(c => c.Close).OrderBy(v => v).ToList();
        if (closes.Count < 2)
        {
            return null;
        }

        // Flat value area (all closes equal): treat the shared close as mid-distribution.
        if (closes[^1] <= closes[0])
        {
            if (entryPrice < closes[0])
            {
                return 0m;
            }

            return entryPrice > closes[0] ? 100m : 50m;
        }

        var below = closes.Count(c => c < entryPrice);
        var equal = closes.Count(c => c == entryPrice);
        var rank = (below + 0.5m * equal) / closes.Count * 100m;
        return Math.Clamp(decimal.Round(rank, 4), 0m, 100m);
    }

    public static (decimal? PositionPct, decimal? Low, decimal? High) RecentSwingPosition(
        IReadOnlyList<Candle> candles, decimal entryPrice, int lookback)
    {
        if (entryPrice <= 0m || candles.Count < 2)
        {
            return (null, null, null);
        }

        var recent = candles.TakeLast(Math.Min(lookback, candles.Count)).ToList();
        var low = recent.Min(c => c.Low);
        var high = recent.Max(c => c.High);
        if (high <= low)
        {
            return (null, low, high);
        }

        return (Math.Clamp((entryPrice - low) / (high - low) * 100m, 0m, 100m), low, high);
    }

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
    decimal? EntryDriftFromSignalPct,
    string RangeBasis = "NONE",
    decimal? ClosePercentile = null,
    decimal? RecentSwingPosition = null,
    decimal? PrimaryRangePosition = null,
    string Zone = "MID",
    bool AntiChaseApplied = true,
    int ConfirmationsMet = 0,
    int ConfirmationsRequired = 0,
    decimal? EffectiveMaxDriftPct = null,
    decimal? AtrPct = null)
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
