using Xunit;

namespace TradingBot.FuturesWorker.Tests;

// Authoritative LONG 24h-range gate. The guard consumes an already-computed freshness
// result (tape / local-high / drift / breakout) and adds the robust 24h range position
// and rebound-from-low confirmation. Range positions are 0..100, percents follow the
// project convention (0.20 == 0.20%).
public sealed class FuturesLongRangeGuardTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-07-16T03:31:00Z");

    private static FuturesFreshnessOptions Thresholds() => new();

    // --- Range position math -------------------------------------------------

    [Theory]
    [InlineData(50.0, 0.0)]
    [InlineData(70.0, 20.0)]
    [InlineData(100.0, 50.0)]
    [InlineData(150.0, 100.0)]
    public void Range_position_is_linear_across_the_24h_range(double ask, double expectedPosition)
    {
        // Fewer than RobustRangeMinSampleCount candles -> ABSOLUTE_24H range [50,150].
        var result = FuturesLongRangeGuard.Evaluate(
            RangeMarket(low: 50m, high: 150m, ask: (decimal)ask, candleCount: 5),
            Freshness(),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.Equal("ABSOLUTE_24H", result.Range24hSource);
        Assert.NotNull(result.Range24hPosition);
        Assert.Equal((decimal)expectedPosition, decimal.Round(result.Range24hPosition!.Value, 4));
    }

    // --- Decision scenarios --------------------------------------------------

    [Fact]
    public void Lower_range_with_confirmed_reversal_passes_the_range_guard()
    {
        var result = FuturesLongRangeGuard.Evaluate(
            RangeMarket(low: 50m, high: 150m, ask: 70m, candleCount: 5), // pos 20%
            Freshness(risingSteps: 2, slope: 0.4m, freshTape: true, localHighDistance: 1.0m, drift: 0.02m),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.True(result.Evaluated);
        Assert.False(result.Blocked);
        Assert.Null(result.BlockReasonCode);
    }

    [Fact]
    public void Lower_range_but_still_falling_is_blocked()
    {
        var result = FuturesLongRangeGuard.Evaluate(
            RangeMarket(low: 50m, high: 150m, ask: 70m, candleCount: 5), // pos 20%
            Freshness(risingSteps: 0, slope: -0.3m, freshTape: false),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.True(result.Blocked);
        Assert.Equal(FuturesLongRangeGuard.RisingSnapshotsNotConfirmed, result.BlockReasonCode);
    }

    [Fact]
    public void Upper_range_is_blocked_by_the_range_guard()
    {
        var result = FuturesLongRangeGuard.Evaluate(
            RangeMarket(low: 50m, high: 150m, ask: 130m, candleCount: 5), // pos 80%
            Freshness(),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.True(result.Blocked);
        Assert.Equal(FuturesLongRangeGuard.RangePositionTooHigh, result.BlockReasonCode);
    }

    [Fact]
    public void Fresh_tape_does_not_bypass_the_upper_range()
    {
        var result = FuturesLongRangeGuard.Evaluate(
            RangeMarket(low: 50m, high: 150m, ask: 135m, candleCount: 5), // pos 85%
            Freshness(freshTape: true, freshBreakout: false),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.True(result.Blocked);
        Assert.Equal(FuturesLongRangeGuard.RangePositionTooHigh, result.BlockReasonCode);
    }

    [Fact]
    public void Lower_range_but_at_local_high_is_blocked_without_breakout()
    {
        var result = FuturesLongRangeGuard.Evaluate(
            RangeMarket(low: 50m, high: 150m, ask: 70m, candleCount: 5), // pos 20%
            Freshness(localHighDistance: 0.05m, freshBreakout: false),   // within 0.12% of local high
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.True(result.Blocked);
        Assert.Equal(FuturesLongRangeGuard.EntryTooCloseToLocalHigh, result.BlockReasonCode);
    }

    [Fact]
    public void Breakout_exempts_local_high_but_never_the_upper_range()
    {
        // A confirmed breakout in the lower range clears the local-high guard...
        var lowerRangeBreakout = FuturesLongRangeGuard.Evaluate(
            RangeMarket(low: 50m, high: 150m, ask: 70m, candleCount: 5), // pos 20%
            Freshness(localHighDistance: 0.05m, freshBreakout: true),
            FuturesDesiredExposure.Long,
            Thresholds());
        Assert.False(lowerRangeBreakout.Blocked);

        // ...but the same breakout cannot admit a LONG in the upper range.
        var upperRangeBreakout = FuturesLongRangeGuard.Evaluate(
            RangeMarket(low: 50m, high: 150m, ask: 140m, candleCount: 5), // pos 90%
            Freshness(localHighDistance: -0.10m, freshBreakout: true),
            FuturesDesiredExposure.Long,
            Thresholds());
        Assert.True(upperRangeBreakout.Blocked);
        Assert.Equal(FuturesLongRangeGuard.RangePositionTooHigh, upperRangeBreakout.BlockReasonCode);
    }

    [Fact]
    public void Single_spike_uses_robust_percentile_range()
    {
        // 40 candles trading 50..65 with one 150 spike. entry 65 is at the top of the
        // robust range even though it is only ~15% of the absolute range.
        var result = FuturesLongRangeGuard.Evaluate(
            SpikeMarket(normalLow: 50m, normalHigh: 65m, spikeHigh: 150m, ask: 65m, candleCount: 40),
            Freshness(),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.Equal("PERCENTILE_5_95", result.Range24hSource);
        Assert.NotNull(result.RobustHigh24h);
        Assert.True(result.RobustHigh24h < 100m);      // the 150 spike is excluded
        Assert.Equal(150m, result.AbsoluteHigh24h);    // absolute range still records it
        Assert.True(result.Range24hPosition > 80m);    // robustly near the top
        Assert.True(result.Blocked);
        Assert.Equal(FuturesLongRangeGuard.RangePositionTooHigh, result.BlockReasonCode);
    }

    [Fact]
    public void Insufficient_data_blocks_safely_without_throwing()
    {
        var result = FuturesLongRangeGuard.Evaluate(
            RangeMarket(low: 50m, high: 150m, ask: 60m, candleCount: 1),
            Freshness(),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.True(result.Blocked);
        Assert.Equal(FuturesLongRangeGuard.RangeUnavailable, result.BlockReasonCode);
        Assert.Equal("UNAVAILABLE", result.Range24hSource);
    }

    [Fact]
    public void Zero_range_blocks_without_division_by_zero()
    {
        var result = FuturesLongRangeGuard.Evaluate(
            RangeMarket(low: 100m, high: 100m, ask: 100m, candleCount: 25),
            Freshness(),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.True(result.Blocked);
        Assert.Equal(FuturesLongRangeGuard.RangeTooNarrow, result.BlockReasonCode);
        Assert.Null(result.Range24hPosition);
    }

    [Fact]
    public void Short_is_not_evaluated_by_the_long_range_guard()
    {
        var result = FuturesLongRangeGuard.Evaluate(
            RangeMarket(low: 50m, high: 150m, ask: 130m, candleCount: 5),
            Freshness(),
            FuturesDesiredExposure.Short,
            Thresholds());

        Assert.False(result.Evaluated);
        Assert.False(result.Blocked);
    }

    [Fact]
    public void Disabled_guard_is_a_no_op()
    {
        var thresholds = Thresholds();
        thresholds.LongRangeGuardEnabled = false;
        var result = FuturesLongRangeGuard.Evaluate(
            RangeMarket(low: 50m, high: 150m, ask: 130m, candleCount: 5),
            Freshness(),
            FuturesDesiredExposure.Long,
            thresholds);

        Assert.False(result.Evaluated);
        Assert.False(result.Blocked);
    }

    // --- Fixtures ------------------------------------------------------------

    private static EntryFreshnessResult Freshness(
        int risingSteps = 2,
        decimal? slope = 0.4m,
        bool freshTape = true,
        bool freshBreakout = false,
        decimal? localHighDistance = 1.0m,
        decimal? drift = 0.02m) =>
        new(
            PositionIn24hRangePct: null,
            DistanceFromRecentHighPct: null,
            LastSnapshotStepPct: null,
            ShortSnapshotSlopePct: slope,
            PositiveStepsInLast3: risingSteps,
            IsNearHigh: false,
            HasFreshUpwardTape: freshTape,
            HasFreshBreakout: freshBreakout,
            Blocked: false,
            BlockReason: null,
            EntryDistanceFromLocalHighPct: localHighDistance,
            LocalHighSource: "last-2-closed-15m",
            BreakoutBufferPct: 0.05m,
            LivePriceVsSignalClosePct: drift);

    // Absolute 24h range [low, high] with `candleCount` candles and an executable ask.
    private static InstrumentMarketState RangeMarket(decimal low, decimal high, decimal ask, int candleCount)
    {
        var mid = (low + high) / 2m;
        var candles = new List<Candle>();
        for (var i = 0; i < candleCount; i++)
        {
            var candleLow = i == 0 ? low : mid;
            var candleHigh = i == candleCount - 1 ? high : mid;
            candles.Add(new Candle(T.AddMinutes(-15 * (candleCount - i)), mid, candleHigh, candleLow, mid, 1m, 1));
        }

        return new InstrumentMarketState
        {
            Instrument = new InstrumentOptions { Pair = "RNG/USD", KrakenPair = "PF_RNGUSD", Enabled = true },
            Candles = candles,
            Quote = new Quote(ask - 0.01m, ask, ask, 1_000_000m)
        };
    }

    // Normal trading band [normalLow, normalHigh] with a single high spike far above it.
    // Every normal candle spans the band, so the 5/95 percentile range is a clean
    // [normalLow, normalHigh] and the lone spike sits above the 95th percentile.
    private static InstrumentMarketState SpikeMarket(decimal normalLow, decimal normalHigh, decimal spikeHigh, decimal ask, int candleCount)
    {
        var mid = (normalLow + normalHigh) / 2m;
        var candles = new List<Candle>();
        for (var i = 0; i < candleCount; i++)
        {
            var candleHigh = i == candleCount / 2 ? spikeHigh : normalHigh;
            candles.Add(new Candle(T.AddMinutes(-15 * (candleCount - i)), mid, candleHigh, normalLow, mid, 1m, 1));
        }

        return new InstrumentMarketState
        {
            Instrument = new InstrumentOptions { Pair = "SPK/USD", KrakenPair = "PF_SPKUSD", Enabled = true },
            Candles = candles,
            Quote = new Quote(ask - 0.01m, ask, ask, 1_000_000m)
        };
    }
}
