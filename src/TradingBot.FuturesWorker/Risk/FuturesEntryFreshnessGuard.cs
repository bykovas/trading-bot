using TradingBot.Core.Signals;

namespace TradingBot.FuturesWorker;

internal sealed record EntryFreshnessResult(
    decimal? PositionIn24hRangePct,
    decimal? DistanceFromRecentHighPct,
    decimal? LastSnapshotStepPct,
    decimal? ShortSnapshotSlopePct,
    int PositiveStepsInLast3,
    bool IsNearHigh,
    bool HasFreshUpwardTape,
    bool HasFreshBreakout,
    bool Blocked,
    string? BlockReason,
    decimal? EntryDistanceFromLocalHighPct = null,
    string? LocalHighSource = null,
    decimal? BreakoutBufferPct = null,
    decimal? LivePriceVsSignalClosePct = null,
    decimal? PostFillEntryDistanceFromLocalHighPct = null,
    decimal? PostFillLivePriceVsSignalClosePct = null);

internal static class FuturesEntryFreshnessGuard
{
    public const string HoldReasonCode = "ENTRY_STALE_NEAR_HIGH";

    public static EntryFreshnessResult Evaluate(
        InstrumentMarketState marketState,
        IReadOnlyList<PriceObservation> priceActionHistoryObservations,
        FuturesDesiredExposure desired,
        FuturesFreshnessOptions thresholds)
    {
        if (desired != FuturesDesiredExposure.Long)
        {
            return new EntryFreshnessResult(null, null, null, null, 0, false, true, false, false, null);
        }

        var range = CalculateRangePosition(marketState.Candles);
        var recentHigh = RecentHigh(marketState.Candles, thresholds.RecentHighLookbackCandles);
        var signalClose = marketState.LastPrice;
        var livePrice = EntryReferencePrice(marketState, desired);
        var distanceFromRecentHighPct = recentHigh is > 0m && livePrice > 0m
            ? Math.Max(0m, (recentHigh.Value - livePrice) / recentHigh.Value * 100m)
            : (decimal?)null;
        var breakoutAboveHighPct = recentHigh is > 0m && livePrice > 0m
            ? (livePrice - recentHigh.Value) / recentHigh.Value * 100m
            : (decimal?)null;
        var tape = EvaluateTape(priceActionHistoryObservations, thresholds);
        var localHigh = LocalExecutionHigh(marketState.Candles, thresholds.LocalHighLookbackClosedCandles);
        var localHighSource = localHigh.Source;
        var entryDistanceFromLocalHighPct = DistanceBelowHighPct(livePrice, localHigh.High);
        var livePriceVsSignalClosePct = signalClose > 0m && livePrice > 0m
            ? (livePrice - signalClose) / signalClose * 100m
            : (decimal?)null;
        var spreadOk = SpreadPercentOf(marketState) <= Math.Max(0m, thresholds.NearHighMaxDistanceFromRecentHighPct);

        // 15m candle momentum over the recent lookback. A fresh micro-tape must not
        // rescue a continuation LONG when the underlying candles are rolling over.
        // Momentum that cannot be computed abstains (does not block).
        var candleMomentumPct = RecentCandleMomentumPct(marketState.Candles, thresholds.ContinuationCandleMomentumLookback);
        var candleMomentumOk = candleMomentumPct is not { } momentum || momentum >= thresholds.MinContinuationCandleMomentumPct;

        // "Fresh continuation" needs BOTH a fresh snapshot tape AND non-falling 15m
        // candles — a positive micro-tick into a declining structure is not fresh.
        var freshContinuationTape = tape.HasFreshUpwardTape && candleMomentumOk;

        var isNearHigh =
            range.PositionIn24hRangePct is { } position
            && position >= thresholds.NearHighMin24hRangePositionPct
            && distanceFromRecentHighPct is { } distance
            && distance <= thresholds.NearHighMaxDistanceFromRecentHighPct;
        var breakoutHeld =
            localHigh.High is > 0m
            && BreakoutConfirmedByTape(priceActionHistoryObservations, localHigh.High.Value, thresholds);
        var freshBreakout =
            breakoutAboveHighPct is { } breakout
            && breakout >= thresholds.BreakoutMinAboveRecentHighPct
            && spreadOk
            && tape.HasFreshUpwardTape
            && breakoutHeld;
        var continuationZone =
            range.PositionIn24hRangePct is { } continuationPosition
            && continuationPosition >= thresholds.FreshContinuationMin24hRangePositionPct;
        var staleContinuation = continuationZone && !freshContinuationTape && !freshBreakout;
        var localExecutionOverextended =
            localHigh.High is > 0m
            && entryDistanceFromLocalHighPct is { } distanceFromLocalHigh
            && distanceFromLocalHigh <= thresholds.MaxEntryDistanceFromLocalHighPct
            && !freshBreakout;
        var chasedSignal =
            livePriceVsSignalClosePct is { } signalDrift
            && signalDrift > thresholds.MaxEntryDriftFromSignalPct
            && !freshBreakout;
        var blocked = staleContinuation
            || (isNearHigh && !freshContinuationTape && !freshBreakout)
            || localExecutionOverextended
            || chasedSignal;
        var momentumNote = !candleMomentumOk && tape.HasFreshUpwardTape
            ? $"; fresh micro-tape ignored because {thresholds.ContinuationCandleMomentumLookback}-candle momentum {candleMomentumPct:0.###}% < {thresholds.MinContinuationCandleMomentumPct:0.###}%"
            : string.Empty;
        var reason = blocked
            ? localExecutionOverextended
                ? $"entry local-high chase: executable entry price is {entryDistanceFromLocalHighPct:0.###}% below {localHighSource} high, threshold {thresholds.MaxEntryDistanceFromLocalHighPct:0.###}%, breakout buffer {thresholds.BreakoutMinAboveRecentHighPct:0.###}% was not confirmed by {thresholds.BreakoutHoldSnapshotCount} snapshots"
                : chasedSignal
                    ? $"entry chased signal: executable entry price drifted +{livePriceVsSignalClosePct:0.###}% from signal close, max {thresholds.MaxEntryDriftFromSignalPct:0.###}%, breakout not confirmed"
                : isNearHigh
                ? $"entry stale near high: 24h range position {range.PositionIn24hRangePct:0.###}% >= {thresholds.NearHighMin24hRangePositionPct:0.###}%, distance from recent high {distanceFromRecentHighPct:0.###}% <= {thresholds.NearHighMaxDistanceFromRecentHighPct:0.###}%, short tape slope {tape.ShortSnapshotSlopePct:0.###}% is not fresh{momentumNote}"
                : $"entry stale continuation: 24h range position {range.PositionIn24hRangePct:0.###}% >= {thresholds.FreshContinuationMin24hRangePositionPct:0.###}%, but no fresh upward tape and no valid breakout{momentumNote}"
            : null;

        return new EntryFreshnessResult(
            range.PositionIn24hRangePct,
            distanceFromRecentHighPct,
            tape.LastSnapshotStepPct,
            tape.ShortSnapshotSlopePct,
            tape.PositiveSteps,
            isNearHigh,
            freshContinuationTape,
            freshBreakout,
            blocked,
            reason,
            entryDistanceFromLocalHighPct,
            localHighSource,
            thresholds.BreakoutMinAboveRecentHighPct,
            livePriceVsSignalClosePct);
    }

    private static (decimal? PositionIn24hRangePct, decimal? High, decimal? Low) CalculateRangePosition(IReadOnlyList<Candle> candles)
    {
        var recent = candles.TakeLast(Math.Min(96, candles.Count)).ToList();
        if (recent.Count < 2)
        {
            return (null, null, null);
        }

        var high = recent.Max(candle => candle.High);
        var low = recent.Min(candle => candle.Low);
        var live = recent[^1].Close;
        if (high <= low)
        {
            return (null, high, low);
        }

        return ((live - low) / (high - low) * 100m, high, low);
    }

    // Percent change of the latest close versus the close `lookback` candles earlier.
    // Null when there are not enough candles or the reference close is non-positive.
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

    private static decimal? RecentHigh(IReadOnlyList<Candle> candles, int lookbackCandles)
    {
        var recent = candles.TakeLast(Math.Min(Math.Max(2, lookbackCandles), candles.Count)).ToList();
        return recent.Count == 0 ? null : recent.Max(candle => candle.High);
    }

    public static EntryFreshnessResult WithFillDiagnostics(
        EntryFreshnessResult result,
        InstrumentMarketState marketState,
        FuturesDesiredExposure desired,
        decimal fillPrice,
        FuturesFreshnessOptions thresholds)
    {
        if (desired != FuturesDesiredExposure.Long || fillPrice <= 0m)
        {
            return result;
        }

        var localHigh = LocalExecutionHigh(marketState.Candles, thresholds.LocalHighLookbackClosedCandles);
        var signalClose = marketState.LastPrice;
        var postFillDistance = DistanceBelowHighPct(fillPrice, localHigh.High);
        var postFillDrift = signalClose > 0m
            ? (fillPrice - signalClose) / signalClose * 100m
            : (decimal?)null;

        return result with
        {
            PostFillEntryDistanceFromLocalHighPct = postFillDistance,
            PostFillLivePriceVsSignalClosePct = postFillDrift
        };
    }

    private static decimal EntryReferencePrice(InstrumentMarketState marketState, FuturesDesiredExposure desired)
    {
        if (desired == FuturesDesiredExposure.Long && marketState.BestAsk > 0m)
        {
            return marketState.BestAsk;
        }

        if (desired == FuturesDesiredExposure.Short && marketState.BestBid > 0m)
        {
            return marketState.BestBid;
        }

        return marketState.Quote?.Last > 0m
            ? marketState.Quote.Last
            : marketState.LastPrice;
    }

    private static (decimal? High, string? Source) LocalExecutionHigh(IReadOnlyList<Candle> candles, int lookbackClosedCandles)
    {
        var count = Math.Clamp(lookbackClosedCandles <= 0 ? 2 : lookbackClosedCandles, 1, 8);
        var recent = candles.TakeLast(Math.Min(count, candles.Count)).ToList();
        if (recent.Count == 0)
        {
            return (null, null);
        }

        return (recent.Max(candle => candle.High), $"last-{recent.Count}-closed-15m");
    }

    private static decimal? DistanceBelowHighPct(decimal entryPrice, decimal? high) =>
        high is > 0m && entryPrice > 0m
            ? (high.Value - entryPrice) / high.Value * 100m
            : null;

    private static bool BreakoutConfirmedByTape(
        IReadOnlyList<PriceObservation> observations,
        decimal localHigh,
        FuturesFreshnessOptions thresholds)
    {
        if (localHigh <= 0m)
        {
            return false;
        }

        var count = Math.Max(1, thresholds.BreakoutHoldSnapshotCount);
        var tape = observations.TakeLast(Math.Min(count, observations.Count)).ToList();
        if (tape.Count < count)
        {
            return false;
        }

        var trigger = localHigh * (1m + thresholds.BreakoutMinAboveRecentHighPct / 100m);
        return tape.All(observation => observation.Last >= trigger);
    }

    private static (decimal? LastSnapshotStepPct, decimal? ShortSnapshotSlopePct, int PositiveSteps, bool HasFreshUpwardTape) EvaluateTape(
        IReadOnlyList<PriceObservation> observations,
        FuturesFreshnessOptions thresholds)
    {
        var count = Math.Max(2, thresholds.FreshTapeSnapshotCount);
        var tape = observations.TakeLast(Math.Min(count, observations.Count)).ToList();
        if (tape.Count < count)
        {
            return (null, null, 0, false);
        }

        var positiveSteps = 0;
        for (var i = 1; i < tape.Count; i++)
        {
            if (tape[i].Last > tape[i - 1].Last)
            {
                positiveSteps++;
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
            && slope >= thresholds.FreshTapeMinSlopePct
            && positiveSteps >= thresholds.FreshTapeMinPositiveSteps
            && latest >= average;

        return (lastStep, shortSlope, positiveSteps, hasFreshTape);
    }

    private static decimal SpreadPercentOf(InstrumentMarketState marketState)
    {
        var bid = marketState.BestBid;
        var ask = marketState.BestAsk;
        if (bid <= 0m || ask <= 0m || ask < bid)
        {
            return 0m;
        }

        var mid = (bid + ask) / 2m;
        return mid == 0m ? 0m : (ask - bid) / mid * 100m;
    }
}
