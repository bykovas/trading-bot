namespace TradingBot.FuturesWorker;

// Side-specific entry gate for LONG setups that already passed scoring, quality,
// freshness, and range checks. It separates "the instrument is bullish" from "this
// exact entry has immediate follow-through". Recent live losses clustered in weak
// upper breakouts and mid-range reclaims that had enough lagging score but did not
// keep pushing after the signal.
internal static class FuturesLongFollowThroughGate
{
    public const string UpperBreakoutWeakFollowThrough = "LONG_UPPER_BREAKOUT_WEAK_FOLLOW_THROUGH";
    public const string MidRangeWeakFollowThrough = "LONG_MID_RANGE_WEAK_FOLLOW_THROUGH";
    public const string MidRangeChoppyMarket = "LONG_MID_RANGE_CHOPPY_MARKET";

    public static RiskEvaluation Evaluate(
        FuturesDesiredExposure desired,
        LongRangeResult? longRange,
        EntryFreshnessResult? freshness,
        PriceActionAssessment? priceAction,
        FuturesFreshnessOptions thresholds,
        IReadOnlyList<Candle>? candles = null)
    {
        if (desired != FuturesDesiredExposure.Long
            || longRange is not { Evaluated: true, Blocked: false }
            || freshness is null)
        {
            return new RiskEvaluation(true, new[] { "long follow-through gate: not applicable" });
        }

        var priceActionTrend = priceAction is { DataSufficient: true }
            ? priceAction.TrendPercent
            : null;
        var candleMomentum = freshness.RecentCandleMomentumPct;

        if (longRange.Zone == "UPPER" && freshness.HasFreshBreakout)
        {
            var min = thresholds.UpperBreakoutMinFollowThroughPct;
            if (!AtLeast(candleMomentum, min) && !AtLeast(priceActionTrend, min))
            {
                return Reject(
                    UpperBreakoutWeakFollowThrough,
                    $"upper breakout lacks follow-through: candle momentum {FormatPct(candleMomentum)} and price-action trend {FormatPct(priceActionTrend)} are both below {min:0.###}%");
            }
        }

        if (longRange.Zone == "MID" && !freshness.HasFreshBreakout)
        {
            var efficiency = DirectionalEfficiencyPct(
                candles,
                thresholds.DirectionalEfficiencyLookbackCandles);
            var minEfficiency = thresholds.MinMidRangeDirectionalEfficiencyPct;
            if (minEfficiency > 0m && efficiency is { } actualEfficiency && actualEfficiency < minEfficiency)
            {
                return Reject(
                    MidRangeChoppyMarket,
                    $"mid-range long is inside a choppy market: directional efficiency {actualEfficiency:0.###}% over {thresholds.DirectionalEfficiencyLookbackCandles} closed candles below {minEfficiency:0.###}%");
            }

            var min = thresholds.MidRangeReclaimMinPriceActionTrendPct;
            if (!AtLeast(priceActionTrend, min))
            {
                return Reject(
                    MidRangeWeakFollowThrough,
                    $"mid-range long lacks price-action follow-through: recent price-action trend {FormatPct(priceActionTrend)} below {min:0.###}%");
            }
        }

        return new RiskEvaluation(true, new[] { "long follow-through gate passed" });
    }

    private static RiskEvaluation Reject(string code, string reason) =>
        new(false, new[] { $"{code}: {reason}" });

    private static bool AtLeast(decimal? value, decimal threshold) =>
        value is { } actual && actual >= threshold;

    internal static decimal? DirectionalEfficiencyPct(
        IReadOnlyList<Candle>? candles,
        int lookbackCandles)
    {
        if (candles is null || lookbackCandles < 2 || candles.Count < lookbackCandles)
        {
            return null;
        }

        var window = candles.TakeLast(lookbackCandles).ToList();
        var path = 0m;
        for (var index = 1; index < window.Count; index++)
        {
            path += Math.Abs(window[index].Close - window[index - 1].Close);
        }

        if (path == 0m)
        {
            return 0m;
        }

        var netMove = Math.Abs(window[^1].Close - window[0].Close);
        return decimal.Round(netMove / path * 100m, 3);
    }

    private static string FormatPct(decimal? value) =>
        value is { } actual ? $"{actual:0.###}%" : "unknown";
}
