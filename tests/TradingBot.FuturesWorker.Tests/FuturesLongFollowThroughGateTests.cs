using TradingBot.Core.Signals;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class FuturesLongFollowThroughGateTests
{
    [Fact]
    public void Weak_upper_breakout_is_blocked()
    {
        var result = FuturesLongFollowThroughGate.Evaluate(
            FuturesDesiredExposure.Long,
            Range("UPPER"),
            Freshness(breakout: true, candleMomentum: 0.31m),
            PriceAction(trendPct: 0.38m),
            new FuturesFreshnessOptions());

        Assert.False(result.Approved);
        Assert.Contains(FuturesLongFollowThroughGate.UpperBreakoutWeakFollowThrough, result.Reasons[0]);
    }

    [Fact]
    public void Strong_upper_breakout_is_allowed_by_price_action_follow_through()
    {
        var result = FuturesLongFollowThroughGate.Evaluate(
            FuturesDesiredExposure.Long,
            Range("UPPER"),
            Freshness(breakout: true, candleMomentum: 0.31m),
            PriceAction(trendPct: 0.81m),
            new FuturesFreshnessOptions());

        Assert.True(result.Approved);
    }

    [Fact]
    public void Strong_upper_breakout_is_allowed_by_candle_momentum()
    {
        var result = FuturesLongFollowThroughGate.Evaluate(
            FuturesDesiredExposure.Long,
            Range("UPPER"),
            Freshness(breakout: true, candleMomentum: 1.01m),
            PriceAction(trendPct: 0.14m),
            new FuturesFreshnessOptions());

        Assert.True(result.Approved);
    }

    [Fact]
    public void Mid_range_non_breakout_without_follow_through_is_blocked()
    {
        var result = FuturesLongFollowThroughGate.Evaluate(
            FuturesDesiredExposure.Long,
            Range("MID"),
            Freshness(breakout: false, candleMomentum: 1.17m),
            PriceAction(trendPct: 0.29m),
            new FuturesFreshnessOptions());

        Assert.False(result.Approved);
        Assert.Contains(FuturesLongFollowThroughGate.MidRangeWeakFollowThrough, result.Reasons[0]);
    }

    [Fact]
    public void Mid_range_non_breakout_with_follow_through_is_allowed()
    {
        var result = FuturesLongFollowThroughGate.Evaluate(
            FuturesDesiredExposure.Long,
            Range("MID"),
            Freshness(breakout: false, candleMomentum: 0.23m),
            PriceAction(trendPct: 0.62m),
            new FuturesFreshnessOptions());

        Assert.True(result.Approved);
    }

    [Fact]
    public void Mid_range_non_breakout_inside_choppy_market_is_blocked()
    {
        var options = new FuturesFreshnessOptions
        {
            DirectionalEfficiencyLookbackCandles = 96,
            MinMidRangeDirectionalEfficiencyPct = 5m
        };

        var result = FuturesLongFollowThroughGate.Evaluate(
            FuturesDesiredExposure.Long,
            Range("MID"),
            Freshness(breakout: false, candleMomentum: 1.10m),
            PriceAction(trendPct: 0.80m),
            options,
            ChoppyCandles(96));

        Assert.False(result.Approved);
        Assert.Contains(FuturesLongFollowThroughGate.MidRangeChoppyMarket, result.Reasons[0]);
    }

    [Fact]
    public void Mid_range_non_breakout_with_directional_market_is_allowed()
    {
        var options = new FuturesFreshnessOptions
        {
            DirectionalEfficiencyLookbackCandles = 96,
            MinMidRangeDirectionalEfficiencyPct = 5m
        };

        var result = FuturesLongFollowThroughGate.Evaluate(
            FuturesDesiredExposure.Long,
            Range("MID"),
            Freshness(breakout: false, candleMomentum: 1.10m),
            PriceAction(trendPct: 0.80m),
            options,
            TrendingCandles(96));

        Assert.True(result.Approved);
    }

    [Fact]
    public void Low_range_rebound_ignores_directional_efficiency()
    {
        var result = FuturesLongFollowThroughGate.Evaluate(
            FuturesDesiredExposure.Long,
            Range("LOW"),
            Freshness(breakout: false, candleMomentum: 0.15m),
            PriceAction(trendPct: 0.10m),
            new FuturesFreshnessOptions(),
            ChoppyCandles(96));

        Assert.True(result.Approved);
    }

    [Fact]
    public void Low_range_rebound_is_not_changed()
    {
        var result = FuturesLongFollowThroughGate.Evaluate(
            FuturesDesiredExposure.Long,
            Range("LOW"),
            Freshness(breakout: false, candleMomentum: 0.15m),
            PriceAction(trendPct: 0.10m),
            new FuturesFreshnessOptions());

        Assert.True(result.Approved);
    }

    [Fact]
    public void Short_side_is_not_changed()
    {
        var result = FuturesLongFollowThroughGate.Evaluate(
            FuturesDesiredExposure.Short,
            Range("UPPER"),
            Freshness(breakout: true, candleMomentum: 0.01m),
            PriceAction(trendPct: 0.01m),
            new FuturesFreshnessOptions());

        Assert.True(result.Approved);
    }

    private static LongRangeResult Range(string zone) =>
        LongRangeResult.NotEvaluated with
        {
            Evaluated = true,
            Blocked = false,
            Zone = zone
        };

    private static EntryFreshnessResult Freshness(bool breakout, decimal candleMomentum) =>
        new(
            PositionIn24hRangePct: null,
            DistanceFromRecentHighPct: null,
            LastSnapshotStepPct: 0.10m,
            ShortSnapshotSlopePct: 0.15m,
            PositiveStepsInLast3: 2,
            IsNearHigh: false,
            HasFreshUpwardTape: true,
            HasFreshBreakout: breakout,
            Blocked: false,
            BlockReason: null,
            RecentCandleMomentumPct: candleMomentum);

    private static PriceActionAssessment PriceAction(decimal trendPct) =>
        new(
            Pair: "TEST/USD",
            SnapshotCount: 6,
            DataSufficient: true,
            LastPrice: 100m,
            TrendPercent: trendPct,
            RollingAveragePrice: 99m,
            ConsecutiveNonRisingSnapshots: 0);

    private static IReadOnlyList<Candle> ChoppyCandles(int count) =>
        Enumerable.Range(0, count)
            .Select(index => Candle(index, index % 2 == 0 ? 100m : 101m))
            .ToList();

    private static IReadOnlyList<Candle> TrendingCandles(int count) =>
        Enumerable.Range(0, count)
            .Select(index => Candle(index, 100m + index * 0.10m))
            .ToList();

    private static Candle Candle(int index, decimal close) =>
        new(
            OpenTime: DateTimeOffset.UnixEpoch.AddMinutes(index * 15),
            Open: close,
            High: close,
            Low: close,
            Close: close,
            Volume: 1m,
            TradeCount: 1);
}
