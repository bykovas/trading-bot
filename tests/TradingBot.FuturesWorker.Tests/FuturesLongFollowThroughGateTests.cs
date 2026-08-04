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
}
