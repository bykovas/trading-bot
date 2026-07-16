using Xunit;

namespace TradingBot.FuturesWorker.Tests;

// LONG context/anti-knife gate. Wick mid-range is diagnostic only — reclaim after
// wide spikes must pass; late chase near local high still blocks.
public sealed class FuturesLongRangeGuardTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-07-16T03:31:00Z");

    private static FuturesFreshnessOptions Thresholds() => new();

    [Fact]
    public void Reclaim_after_wide_wick_spikes_is_not_blocked_by_mid_24h_range()
    {
        // Scenario: value ~100, spike low 50 / wild 10–150 wick range, reclaim ask=100.
        // Wick position is mid-range — must NOT veto solely for that.
        var candles = new List<Candle>();
        for (var i = 0; i < 40; i++)
        {
            candles.Add(Candle(100m + (i % 3) * 0.1m, high: 101m, low: 99m, i));
        }

        candles.Add(Candle(50m, high: 100m, low: 50m, 40));
        candles.Add(Candle(80m, high: 150m, low: 10m, 41));
        candles.Add(Candle(120m, high: 150m, low: 10m, 42));
        candles.Add(Candle(100m, high: 110m, low: 90m, 43));
        for (var i = 44; i < 50; i++)
        {
            candles.Add(Candle(100m, high: 102m, low: 98m, i));
        }

        var market = new InstrumentMarketState
        {
            Instrument = new InstrumentOptions { Pair = "TEST/USD", KrakenPair = "PF_TESTUSD", Enabled = true },
            Candles = candles,
            Quote = new Quote(99.9m, 100m, 100m, 1_000_000m, MarkPrice: 100m)
        };

        var result = FuturesLongRangeGuard.Evaluate(
            market,
            Freshness(risingSteps: 2, slope: 0.2m, freshTape: true, localHighDistance: 1.0m, drift: 0.02m),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.True(result.Evaluated);
        Assert.False(result.Blocked);
        Assert.Null(result.BlockReasonCode);
        Assert.Equal(FuturesLongRangeGuard.RangeBasisClosePercentile, result.RangeBasis);
        Assert.NotNull(result.ClosePercentile);
    }

    [Fact]
    public void Entry_near_local_high_without_breakout_is_blocked()
    {
        var result = FuturesLongRangeGuard.Evaluate(
            RangeMarket(low: 50m, high: 150m, ask: 70m, candleCount: 5),
            Freshness(risingSteps: 2, slope: 0.4m, freshTape: true, localHighDistance: 0.05m, drift: 0.02m, breakout: false),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.True(result.Blocked);
        Assert.Equal(FuturesLongRangeGuard.EntryTooCloseToLocalHigh, result.BlockReasonCode);
    }

    [Fact]
    public void Confirmed_breakout_may_pass_above_legacy_30_percent_wick_band()
    {
        var result = FuturesLongRangeGuard.Evaluate(
            RangeMarket(low: 50m, high: 150m, ask: 130m, candleCount: 5),
            Freshness(risingSteps: 2, slope: 0.5m, freshTape: true, localHighDistance: 0.05m, drift: 0.20m, breakout: true),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.True(result.Evaluated);
        Assert.False(result.Blocked);
        Assert.True(result.Range24hPosition is > 30m);
    }

    [Fact]
    public void Lower_range_but_still_falling_is_blocked()
    {
        var result = FuturesLongRangeGuard.Evaluate(
            RangeMarket(low: 50m, high: 150m, ask: 70m, candleCount: 5),
            Freshness(risingSteps: 0, slope: -0.3m, freshTape: false),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.True(result.Blocked);
        Assert.Equal(FuturesLongRangeGuard.RisingSnapshotsNotConfirmed, result.BlockReasonCode);
    }

    [Fact]
    public void Close_percentile_ignores_wick_spikes()
    {
        var candles = new List<Candle>();
        for (var i = 0; i < 30; i++)
        {
            candles.Add(Candle(100m, high: 101m, low: 99m, i));
        }

        candles.Add(Candle(100m, high: 200m, low: 1m, 30));
        var rank = FuturesLongRangeGuard.ClosePercentileRank(candles, entryPrice: 100m, lookback: 96);
        Assert.NotNull(rank);
        Assert.InRange(rank!.Value, 40m, 100m);
    }

    private static EntryFreshnessResult Freshness(
        int risingSteps = 2,
        decimal slope = 0.2m,
        bool freshTape = true,
        decimal localHighDistance = 1.0m,
        decimal drift = 0.02m,
        bool breakout = false) =>
        new(
            PositionIn24hRangePct: 20m,
            DistanceFromRecentHighPct: 1m,
            LastSnapshotStepPct: 0.1m,
            ShortSnapshotSlopePct: slope,
            PositiveStepsInLast3: risingSteps,
            IsNearHigh: false,
            HasFreshUpwardTape: freshTape,
            HasFreshBreakout: breakout,
            Blocked: false,
            BlockReason: null,
            EntryDistanceFromLocalHighPct: localHighDistance,
            LocalHighSource: "LOCAL_HIGH",
            LivePriceVsSignalClosePct: drift,
            RecentCandleMomentumPct: 0.2m);

    private static InstrumentMarketState RangeMarket(decimal low, decimal high, decimal ask, int candleCount)
    {
        var candles = new List<Candle>();
        for (var i = 0; i < candleCount; i++)
        {
            var mid = (low + high) / 2m;
            candles.Add(Candle(mid, high: high, low: low, i));
        }

        return new InstrumentMarketState
        {
            Instrument = new InstrumentOptions { Pair = "TEST/USD", KrakenPair = "PF_TESTUSD", Enabled = true },
            Candles = candles,
            Quote = new Quote(ask - 0.01m, ask, ask, 1_000_000m, MarkPrice: ask)
        };
    }

    private static Candle Candle(decimal close, decimal high, decimal low, int index) =>
        new(T.AddMinutes(15 * index), close, high, low, close, 1000m, 1);
}
