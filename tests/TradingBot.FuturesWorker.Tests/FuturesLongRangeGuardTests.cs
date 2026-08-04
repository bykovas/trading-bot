using System.Reflection;
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
    public void Low_zone_with_two_confirmations_and_price_near_local_high_is_allowed()
    {
        var result = FuturesLongRangeGuard.Evaluate(
            PercentileMarket(entryPrice: 20m, lastCandleGreen: false),
            Freshness(freshTape: true, lastStep: -0.1m, momentum: 0.2m, localHighDistance: 0.05m, drift: 0.20m, breakout: false),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.True(result.Evaluated);
        Assert.False(result.Blocked);
        Assert.Equal("LOW", result.Zone);
        Assert.False(result.AntiChaseApplied);
        Assert.Equal(2, result.ConfirmationsMet);
        Assert.Equal(2, result.ConfirmationsRequired);
    }

    [Fact]
    public void Low_zone_with_one_confirmation_is_blocked()
    {
        var result = FuturesLongRangeGuard.Evaluate(
            PercentileMarket(entryPrice: 20m, lastCandleGreen: false),
            Freshness(freshTape: true, lastStep: -0.1m, momentum: -0.1m),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.True(result.Blocked);
        Assert.Equal(FuturesLongRangeGuard.FreshTapeNotConfirmed, result.BlockReasonCode);
        Assert.Equal("LOW", result.Zone);
        Assert.Equal(1, result.ConfirmationsMet);
    }

    [Fact]
    public void Low_zone_two_weak_confirmations_without_a_strong_one_is_blocked()
    {
        // Dead-cat bounce: one positive snapshot step + one green candle satisfy the
        // count, but the tape is not fresh and multi-candle momentum is negative.
        var result = FuturesLongRangeGuard.Evaluate(
            PercentileMarket(entryPrice: 20m, lastCandleGreen: true),
            Freshness(freshTape: false, lastStep: 0.1m, momentum: -0.4m),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.True(result.Blocked);
        Assert.Equal(FuturesLongRangeGuard.StrongConfirmationMissing, result.BlockReasonCode);
        Assert.Equal("LOW", result.Zone);
        Assert.Equal(2, result.ConfirmationsMet); // count satisfied, strength not
    }

    [Fact]
    public void Low_zone_two_confirmations_including_candle_momentum_passes()
    {
        // Same count, but one of them is structural (multi-candle momentum), so the
        // reversal is confirmed even without a fresh tape.
        var result = FuturesLongRangeGuard.Evaluate(
            PercentileMarket(entryPrice: 20m, lastCandleGreen: false),
            Freshness(freshTape: false, lastStep: 0.1m, momentum: 0.4m),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.False(result.Blocked);
        Assert.Equal("LOW", result.Zone);
        Assert.Equal(2, result.ConfirmationsMet);
    }

    [Fact]
    public void Disabled_relaxation_still_blocks_a_peak_entry()
    {
        // LongRangeGuardEnabled only switches off the low-range relaxation: the upper
        // range must stay breakout-only, otherwise one flag disables peak protection.
        var thresholds = Thresholds();
        thresholds.LongRangeGuardEnabled = false;
        var result = FuturesLongRangeGuard.Evaluate(
            PercentileMarket(entryPrice: 85m),
            Freshness(freshTape: true, breakout: false),
            FuturesDesiredExposure.Long,
            thresholds);

        Assert.True(result.Evaluated);
        Assert.True(result.Blocked);
        Assert.Equal(FuturesLongRangeGuard.UpperRangeFreshTapeNotEnough, result.BlockReasonCode);
    }

    [Fact]
    public void Disabled_relaxation_applies_anti_chase_in_the_low_zone()
    {
        // With the relaxation off, a LOW zone behaves like MID: anti-chase applies again.
        var thresholds = Thresholds();
        thresholds.LongRangeGuardEnabled = false;
        var result = FuturesLongRangeGuard.Evaluate(
            PercentileMarket(entryPrice: 20m),
            Freshness(freshTape: true, localHighDistance: 0.05m, breakout: false),
            FuturesDesiredExposure.Long,
            thresholds);

        Assert.True(result.Blocked);
        Assert.Equal(FuturesLongRangeGuard.EntryTooCloseToLocalHigh, result.BlockReasonCode);
        Assert.True(result.AntiChaseApplied);
    }

    [Fact]
    public void Low_zone_without_required_rebound_from_24h_low_is_blocked()
    {
        var result = FuturesLongRangeGuard.Evaluate(
            ReboundMarket(low: 100m, high: 140m, ask: 100.10m),
            Freshness(freshTape: true, lastStep: 0.1m, momentum: 0.2m),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.True(result.Blocked);
        Assert.Equal(FuturesLongRangeGuard.ReboundTooSmall, result.BlockReasonCode);
        Assert.True(result.DistanceFrom24hLowPct < Thresholds().MinReboundFrom24hLowPct);
    }

    [Fact]
    public void Mid_zone_entry_near_local_high_without_breakout_is_blocked()
    {
        var result = FuturesLongRangeGuard.Evaluate(
            PercentileMarket(entryPrice: 50m),
            Freshness(freshTape: true, localHighDistance: 0.05m, drift: 0.02m, breakout: false),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.True(result.Blocked);
        Assert.Equal(FuturesLongRangeGuard.EntryTooCloseToLocalHigh, result.BlockReasonCode);
        Assert.Equal("MID", result.Zone);
        Assert.True(result.AntiChaseApplied);
    }

    [Fact]
    public void Upper_zone_with_fresh_tape_without_breakout_is_blocked()
    {
        var result = FuturesLongRangeGuard.Evaluate(
            PercentileMarket(entryPrice: 85m),
            Freshness(freshTape: true, localHighDistance: 1m, drift: 0.02m, breakout: false),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.True(result.Blocked);
        Assert.Equal(FuturesLongRangeGuard.UpperRangeFreshTapeNotEnough, result.BlockReasonCode);
        Assert.Equal("UPPER", result.Zone);
    }

    [Fact]
    public void Upper_zone_with_confirmed_breakout_is_allowed()
    {
        var result = FuturesLongRangeGuard.Evaluate(
            PercentileMarket(entryPrice: 85m),
            Freshness(freshTape: true, localHighDistance: -0.05m, drift: 0.20m, breakout: true),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.True(result.Evaluated);
        Assert.False(result.Blocked);
        Assert.Equal("UPPER", result.Zone);
    }

    [Fact]
    public void Drift_limit_scales_with_atr()
    {
        var highAtr = FuturesLongRangeGuard.Evaluate(
            AtrMarket(entryPrice: 50m, atrWidthPct: 3.0m),
            Freshness(freshTape: true, localHighDistance: 1m, drift: 0.20m, breakout: false),
            FuturesDesiredExposure.Long,
            Thresholds());
        var lowAtr = FuturesLongRangeGuard.Evaluate(
            AtrMarket(entryPrice: 50m, atrWidthPct: 0.2m),
            Freshness(freshTape: true, localHighDistance: 1m, drift: 0.20m, breakout: false),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.False(highAtr.Blocked);
        Assert.True(highAtr.EffectiveMaxDriftPct > 0.20m);
        Assert.True(lowAtr.Blocked);
        Assert.Equal(FuturesLongRangeGuard.EntryDriftTooHigh, lowAtr.BlockReasonCode);
        Assert.Equal(0.10m, lowAtr.EffectiveMaxDriftPct);
    }

    [Fact]
    public void Short_side_is_not_evaluated_by_long_range_guard()
    {
        var result = FuturesLongRangeGuard.Evaluate(
            PercentileMarket(entryPrice: 50m),
            Freshness(freshTape: true, localHighDistance: 0.05m, drift: 0.20m),
            FuturesDesiredExposure.Short,
            Thresholds());

        Assert.False(result.Evaluated);
        Assert.False(result.Blocked);
    }

    [Fact]
    public void Invalid_freshness_config_values_reset_to_defaults()
    {
        var config = new FuturesBotConfiguration
        {
            Freshness = new FuturesFreshnessOptions
            {
                AntiChaseMinRangePositionPct = 120m,
                LowRangeMinConfirmations = 9,
                DriftAtrMultiple = -1m,
                UpperBreakoutMinFollowThroughPct = 9m,
                MidRangeReclaimMinPriceActionTrendPct = -1m
            }
        };

        InvokeNormalize(config);

        Assert.Equal(35m, config.Freshness.AntiChaseMinRangePositionPct);
        Assert.Equal(2, config.Freshness.LowRangeMinConfirmations);
        Assert.Equal(0.25m, config.Freshness.DriftAtrMultiple);
        Assert.Equal(0.60m, config.Freshness.UpperBreakoutMinFollowThroughPct);
        Assert.Equal(0.50m, config.Freshness.MidRangeReclaimMinPriceActionTrendPct);
    }

    [Fact]
    public void Lower_range_but_still_falling_is_blocked()
    {
        var result = FuturesLongRangeGuard.Evaluate(
            PercentileMarket(entryPrice: 20m, lastCandleGreen: false),
            Freshness(risingSteps: 0, slope: -0.3m, freshTape: false, lastStep: -0.1m, momentum: -0.1m),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.True(result.Blocked);
        Assert.Equal(FuturesLongRangeGuard.FreshTapeNotConfirmed, result.BlockReasonCode);
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
        decimal lastStep = 0.1m,
        decimal momentum = 0.2m,
        decimal localHighDistance = 1.0m,
        decimal drift = 0.02m,
        bool breakout = false) =>
        new(
            PositionIn24hRangePct: 20m,
            DistanceFromRecentHighPct: 1m,
            LastSnapshotStepPct: lastStep,
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
            RecentCandleMomentumPct: momentum);

    private static void InvokeNormalize(FuturesBotConfiguration config)
    {
        var method = typeof(FuturesBotConfiguration).GetMethod("Normalize", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(config, null);
    }

    private static InstrumentMarketState PercentileMarket(decimal entryPrice, bool lastCandleGreen = true)
    {
        var candles = new List<Candle>();
        for (var i = 1; i <= 96; i++)
        {
            var close = (decimal)i;
            candles.Add(Candle(close, high: close + 1m, low: close - 1m, i));
        }

        candles[^1] = lastCandleGreen
            ? new Candle(candles[^1].OpenTime, entryPrice - 0.2m, entryPrice + 1m, entryPrice - 1m, entryPrice, 1000m, 1)
            : new Candle(candles[^1].OpenTime, entryPrice + 0.2m, entryPrice + 1m, entryPrice - 1m, entryPrice, 1000m, 1);

        return new InstrumentMarketState
        {
            Instrument = new InstrumentOptions { Pair = "TEST/USD", KrakenPair = "PF_TESTUSD", Enabled = true },
            Candles = candles,
            Quote = new Quote(entryPrice - 0.01m, entryPrice, entryPrice, 1_000_000m, MarkPrice: entryPrice)
        };
    }

    private static InstrumentMarketState ReboundMarket(decimal low, decimal high, decimal ask)
    {
        var candles = new List<Candle>();
        for (var i = 0; i < 96; i++)
        {
            var close = i == 95 ? ask : low + 1m;
            candles.Add(Candle(close, high: high, low: low, i));
        }

        return new InstrumentMarketState
        {
            Instrument = new InstrumentOptions { Pair = "REBOUND/USD", KrakenPair = "PF_REBOUNDUSD", Enabled = true },
            Candles = candles,
            Quote = new Quote(ask - 0.01m, ask, ask, 1_000_000m, MarkPrice: ask)
        };
    }

    private static InstrumentMarketState AtrMarket(decimal entryPrice, decimal atrWidthPct)
    {
        var halfWidth = entryPrice * atrWidthPct / 100m / 2m;
        var candles = new List<Candle>();
        for (var i = 0; i < 20; i++)
        {
            var low = i == 0 ? entryPrice - 1m : entryPrice - halfWidth;
            candles.Add(new Candle(
                T.AddMinutes(15 * i),
                entryPrice,
                entryPrice + halfWidth,
                low,
                entryPrice,
                1000m,
                1));
        }

        return new InstrumentMarketState
        {
            Instrument = new InstrumentOptions { Pair = "ATR/USD", KrakenPair = "PF_ATRUSD", Enabled = true },
            Candles = candles,
            Quote = new Quote(entryPrice - 0.01m, entryPrice, entryPrice, 1_000_000m, MarkPrice: entryPrice)
        };
    }

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
