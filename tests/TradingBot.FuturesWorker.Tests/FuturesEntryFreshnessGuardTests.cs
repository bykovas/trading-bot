using TradingBot.Core.Signals;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class FuturesEntryFreshnessGuardTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-07-13T14:26:23Z");

    private static FuturesFreshnessOptions Thresholds() => new();
    private static FuturesFreshnessOptions ThresholdsWithoutDriftGuard() => new()
    {
        MaxEntryDriftFromSignalPct = 1m
    };

    [Fact]
    public void River_fixture_blocks_late_long_near_high()
    {
        var result = FuturesEntryFreshnessGuard.Evaluate(
            Market(3.49456893782m, recentHigh: 3.496715m, low24: 3.14234m, quoteLast: 3.48918946617m),
            Observations(3.47926733887m, 3.49071002836m, 3.48918946617m),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.True(result.Blocked);
        Assert.True(result.IsNearHigh);
        Assert.False(result.HasFreshUpwardTape);
        Assert.Equal(FuturesEntryFreshnessGuard.HoldReasonCode, "ENTRY_STALE_NEAR_HIGH");
    }

    [Fact]
    public void Mid_range_long_without_fresh_tape_or_breakout_is_blocked()
    {
        var result = FuturesEntryFreshnessGuard.Evaluate(
            Market(0.21000247245m, recentHigh: 0.21243989865m, low24: 0.196145m, quoteLast: 0.21035227177m),
            Observations(0.21011995958m, 0.21002458073m, 0.21035227177m),
            FuturesDesiredExposure.Long,
            ThresholdsWithoutDriftGuard());

        Assert.False(result.IsNearHigh);
        Assert.False(result.HasFreshUpwardTape);
        Assert.False(result.HasFreshBreakout);
        Assert.True(result.Blocked);
        Assert.Contains("stale continuation", result.BlockReason);
    }

    [Fact]
    public void Mid_range_long_with_fresh_continuation_tape_is_not_blocked()
    {
        var result = FuturesEntryFreshnessGuard.Evaluate(
            Market(0.21075m, recentHigh: 0.21243989865m, low24: 0.196145m, quoteLast: 0.21085227177m),
            Observations(0.21011995958m, 0.21042458073m, 0.21085227177m),
            FuturesDesiredExposure.Long,
            ThresholdsWithoutDriftGuard());

        Assert.False(result.Blocked);
        Assert.True(result.HasFreshUpwardTape);
        Assert.False(result.HasFreshBreakout);
    }

    [Fact]
    public void Genuine_breakout_with_confirming_tape_is_not_blocked()
    {
        var result = FuturesEntryFreshnessGuard.Evaluate(
            Market(100m, recentHigh: 100m, low24: 80m, quoteLast: 100.08m),
            Observations(99.8m, 100.06m, 100.08m),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.False(result.Blocked);
        Assert.True(result.HasFreshBreakout);
    }

    [Fact]
    public void One_negative_tick_inside_positive_tape_stays_fresh()
    {
        var thresholds = Thresholds();
        thresholds.FreshTapeSnapshotCount = 4;
        var result = FuturesEntryFreshnessGuard.Evaluate(
            Market(100.00m, recentHigh: 100.40m, low24: 80m, quoteLast: 100.02m),
            Observations(99.80m, 100.05m, 100.02m, 100.08m),
            FuturesDesiredExposure.Long,
            thresholds);

        Assert.False(result.Blocked);
        Assert.True(result.HasFreshUpwardTape);
        Assert.Equal(2, result.PositiveStepsInLast3);
    }

    [Fact]
    public void Btc_score_override_cannot_bypass_the_freshness_block()
    {
        var result = FuturesEntryFreshnessGuard.Evaluate(
            Market(3.49456893782m, recentHigh: 3.496715m, low24: 3.14234m, quoteLast: 3.48918946617m),
            Observations(3.47926733887m, 3.49071002836m, 3.48918946617m),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.True(result.Blocked);
    }

    [Fact]
    public void Doge_fresh_microtape_into_falling_candles_is_blocked()
    {
        // DOGE case: the last 3 snapshots tick UP (fresh micro-tape), but the recent
        // 15m candles are rolling over (~-0.9% over 4 bars). The micro-tape must NOT
        // rescue the continuation LONG.
        var result = FuturesEntryFreshnessGuard.Evaluate(
            FallingCandleMarket(close: 0.074361m, recentHigh: 0.0752m, low24: 0.0710m, quoteLast: 0.074361m),
            Observations(0.074300m, 0.074340m, 0.074361m), // micro-tape rising
            FuturesDesiredExposure.Long,
            ThresholdsWithoutDriftGuard());

        Assert.True(result.Blocked);
        Assert.False(result.HasFreshUpwardTape); // fresh continuation denied by candle momentum
        Assert.False(result.HasFreshBreakout);
        Assert.Contains("micro-tape ignored", result.BlockReason);
    }

    [Fact]
    public void Continuation_tape_with_rising_candles_still_passes()
    {
        // Same fresh micro-tape but candles are rising over the last 4 bars -> allowed.
        var result = FuturesEntryFreshnessGuard.Evaluate(
            RisingCandleMarket(close: 0.21082m, recentHigh: 0.2124m, low24: 0.196145m, quoteLast: 0.21085227177m),
            Observations(0.21011995958m, 0.21042458073m, 0.21085227177m),
            FuturesDesiredExposure.Long,
            ThresholdsWithoutDriftGuard());

        Assert.False(result.Blocked);
        Assert.True(result.HasFreshUpwardTape);
    }

    [Fact]
    public void Near_low_bounce_is_fresh_and_admits_dip_channel()
    {
        // Price in the lower quarter of the 24h range, last 4 candles ticking up and
        // a fresh rising micro-tape: this is the dip-bounce setup the worker promotes.
        var result = FuturesEntryFreshnessGuard.Evaluate(
            NearLowMarket(rising: true),
            Observations(0.11480m, 0.11490m, 0.11500m),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.False(result.Blocked);
        Assert.True(result.HasFreshUpwardTape); // fresh tape + non-negative candle momentum
        Assert.False(result.IsNearHigh);
        Assert.NotNull(result.PositionIn24hRangePct);
        Assert.True(result.PositionIn24hRangePct < 25m); // lower quarter of the 24h range
        // Candle momentum is exposed and clearly positive here, so a small positive
        // Dip.MinCandleMomentumPct floor (0.10%) still admits this genuine bounce.
        Assert.NotNull(result.RecentCandleMomentumPct);
        Assert.True(result.RecentCandleMomentumPct > 0.10m);
    }

    [Fact]
    public void Inj_fixture_blocks_continuation_long_at_local_execution_high()
    {
        var result = FuturesEntryFreshnessGuard.Evaluate(
            InjMarket(),
            Observations(5.13988029707m, 5.14331980142m, 5.14550421218m),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.True(result.Blocked);
        Assert.True(result.HasFreshUpwardTape);
        Assert.False(result.HasFreshBreakout);
        Assert.InRange(result.PositionIn24hRangePct!.Value, 75m, 90m);
        Assert.True(result.EntryDistanceFromLocalHighPct <= 0.12m);
        Assert.True(result.LivePriceVsSignalClosePct > 0.10m);
        Assert.Equal("last-2-closed-15m", result.LocalHighSource);
        Assert.Contains("local-high chase", result.BlockReason);
    }

    [Fact]
    public void Confirmed_breakout_above_local_high_is_not_blocked_by_local_high_guard()
    {
        var thresholds = Thresholds();
        var result = FuturesEntryFreshnessGuard.Evaluate(
            BreakoutMarket(signalClose: 100.08m, localHigh: 100m, quoteLast: 100.16m),
            Observations(99.95m, 100.08m, 100.16m),
            FuturesDesiredExposure.Long,
            thresholds);

        Assert.False(result.Blocked);
        Assert.True(result.HasFreshBreakout);
        Assert.True(result.EntryDistanceFromLocalHighPct < 0m);
        Assert.True(result.LivePriceVsSignalClosePct <= thresholds.MaxEntryDriftFromSignalPct);
    }

    [Fact]
    public void Executable_price_drift_from_signal_close_blocks_chasing()
    {
        var result = FuturesEntryFreshnessGuard.Evaluate(
            Market(100m, recentHigh: 101m, low24: 80m, quoteLast: 100.20m),
            Observations(99.90m, 100.05m, 100.20m),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.True(result.Blocked);
        Assert.True(result.LivePriceVsSignalClosePct > 0.10m);
        Assert.Contains("chased signal", result.BlockReason);
    }

    [Fact]
    public void Near_low_with_falling_candles_is_not_fresh_no_dip_knife()
    {
        // Same near-low zone but the 15m candles are rolling over. Even with a rising
        // micro-tape the candle momentum denies freshness, so the dip-bounce channel
        // will NOT fire — the guard refuses to catch a falling knife.
        var result = FuturesEntryFreshnessGuard.Evaluate(
            NearLowMarket(rising: false),
            Observations(0.11480m, 0.11490m, 0.11500m),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.False(result.HasFreshUpwardTape);
        Assert.NotNull(result.PositionIn24hRangePct);
        Assert.True(result.PositionIn24hRangePct < 25m);
    }

    // Wide 24h range (high spike far back, low floor) with the last 5 candles sitting
    // in the lower quarter. rising -> a confirmed bounce; falling -> rolling over.
    private static InstrumentMarketState NearLowMarket(bool rising)
    {
        var candles = new List<Candle>();
        for (var i = 95; i >= 5; i--)
        {
            var high = i == 50 ? 0.20m : 0.1040m;
            candles.Add(new Candle(T.AddMinutes(-15 * i), 0.1020m, high, 0.1000m, 0.1020m, 1m, 1));
        }

        decimal[] closes = rising
            ? [0.1080m, 0.1100m, 0.1120m, 0.1140m, 0.1150m]
            : [0.1210m, 0.1190m, 0.1170m, 0.1155m, 0.1150m];
        for (var i = 0; i < closes.Length; i++)
        {
            var c = closes[i];
            candles.Add(new Candle(T.AddMinutes(-15 * (5 - i)), c - 0.0005m, c + 0.0003m, c - 0.0006m, c, 1m, 1));
        }

        return new InstrumentMarketState
        {
            Instrument = new InstrumentOptions { Pair = "DIP/USD", KrakenPair = "PF_DIPUSD", Enabled = true },
            Candles = candles,
            Quote = new Quote(0.11490m, 0.11510m, 0.11500m, 1_000_000m)
        };
    }

    private static InstrumentMarketState InjMarket()
    {
        var candles = new List<Candle>();
        var rows = new (string Time, decimal O, decimal H, decimal L, decimal C)[]
        {
            ("2026-07-15T15:30:00Z", 5.12186150908m, 5.16894486624m, 5.11748323981m, 5.16076794636m),
            ("2026-07-15T15:45:00Z", 5.16076794636m, 5.16079719801m, 5.14069905384m, 5.15399956518m),
            ("2026-07-15T16:00:00Z", 5.15399956518m, 5.15628898325m, 5.09242994277m, 5.09624968766m),
            ("2026-07-15T16:15:00Z", 5.09624968766m, 5.10955397291m, 5.08445704498m, 5.10840313285m),
            ("2026-07-15T16:30:00Z", 5.10840313285m, 5.11608831418m, 5.06019399534m, 5.09503059383m),
            ("2026-07-15T16:45:00Z", 5.09503059383m, 5.12134037360m, 5.09383393840m, 5.11902135956m),
            ("2026-07-15T17:00:00Z", 5.11902135956m, 5.12667082737m, 5.09084612952m, 5.09372897437m),
            ("2026-07-15T17:15:00Z", 5.09372897437m, 5.11364683740m, 5.08975375126m, 5.10908071794m),
            ("2026-07-15T17:30:00Z", 5.10908071794m, 5.12379925133m, 5.10378773254m, 5.11432582846m),
            ("2026-07-15T17:45:00Z", 5.11432582846m, 5.13223957102m, 5.10825048261m, 5.12241819287m),
            ("2026-07-15T18:00:00Z", 5.12241819287m, 5.13461852165m, 5.11647215951m, 5.13350282643m),
            ("2026-07-15T18:15:00Z", 5.13350282643m, 5.14911849499m, 5.12876739496m, 5.14074727046m)
        };

        for (var i = 0; i < 84; i++)
        {
            candles.Add(new Candle(T.AddMinutes(-15 * (96 - i)), 5.04m, 5.05m, 4.99m, 5.03m, 1m, 1));
        }

        candles.AddRange(rows.Select(row => new Candle(DateTimeOffset.Parse(row.Time), row.O, row.H, row.L, row.C, 1m, 1)));
        return new InstrumentMarketState
        {
            Instrument = new InstrumentOptions { Pair = "INJ/USD", KrakenPair = "PF_INJUSD", Enabled = true },
            Candles = candles,
            Quote = new Quote(5.142m, 5.148m, 5.14550421218m, 727_506m)
        };
    }

    private static InstrumentMarketState BreakoutMarket(decimal signalClose, decimal localHigh, decimal quoteLast)
    {
        var candles = new List<Candle>();
        for (var i = 95; i >= 2; i--)
        {
            candles.Add(new Candle(T.AddMinutes(-15 * i), 90m, 91m, 80m, 90m, 1m, 1));
        }

        candles.Add(new Candle(T.AddMinutes(-30), 99.2m, localHigh, 98.8m, 99.8m, 1m, 1));
        candles.Add(new Candle(T.AddMinutes(-15), 99.8m, localHigh, 99.4m, signalClose, 1m, 1));

        return new InstrumentMarketState
        {
            Instrument = new InstrumentOptions { Pair = "BREAK/USD", KrakenPair = "PF_BREAKUSD", Enabled = true },
            Candles = candles,
            Quote = new Quote(quoteLast - 0.01m, quoteLast, quoteLast, 1_000_000m)
        };
    }

    // 96 flat candles then the last 4 declining into the entry (fresh micro-tape,
    // falling 15m structure).
    private static InstrumentMarketState FallingCandleMarket(decimal close, decimal recentHigh, decimal low24, decimal quoteLast)
    {
        var candles = new List<Candle>();
        for (var i = 95; i >= 5; i--)
        {
            candles.Add(new Candle(T.AddMinutes(-15 * i), low24 + 0.003m, low24 + 0.004m, low24, low24 + 0.003m, 1m, 1));
        }

        // Last 5 candles decline from ~0.0751 down to `close` (~-0.9% over 4 bars).
        decimal[] closes = [0.075050m, 0.074850m, 0.074650m, 0.074500m, close];
        for (var i = 0; i < closes.Length; i++)
        {
            var c = closes[i];
            candles.Add(new Candle(T.AddMinutes(-15 * (5 - i)), c + 0.0002m, i == 0 ? recentHigh : c + 0.0003m, c - 0.0003m, c, 1m, 1));
        }

        return new InstrumentMarketState
        {
            Instrument = new InstrumentOptions { Pair = "DOGE/USD", KrakenPair = "PF_DOGEUSD", Enabled = true },
            Candles = candles,
            Quote = new Quote(quoteLast - 0.0001m, quoteLast + 0.0001m, quoteLast, 1_000_000m)
        };
    }

    // 96 flat candles then the last 4 rising into the entry.
    private static InstrumentMarketState RisingCandleMarket(decimal close, decimal recentHigh, decimal low24, decimal quoteLast)
    {
        var candles = new List<Candle>();
        for (var i = 95; i >= 5; i--)
        {
            candles.Add(new Candle(T.AddMinutes(-15 * i), low24 + 0.003m, low24 + 0.004m, low24, low24 + 0.003m, 1m, 1));
        }

        decimal[] closes = [0.20800m, 0.20900m, 0.20980m, 0.21040m, close];
        for (var i = 0; i < closes.Length; i++)
        {
            var c = closes[i];
            candles.Add(new Candle(T.AddMinutes(-15 * (5 - i)), c - 0.0003m, i == closes.Length - 1 ? recentHigh : c + 0.0003m, c - 0.0004m, c, 1m, 1));
        }

        return new InstrumentMarketState
        {
            Instrument = new InstrumentOptions { Pair = "TEST/USD", KrakenPair = "PF_TESTUSD", Enabled = true },
            Candles = candles,
            Quote = new Quote(quoteLast - 0.0001m, quoteLast + 0.0001m, quoteLast, 1_000_000m)
        };
    }

    private static InstrumentMarketState Market(decimal close, decimal recentHigh, decimal low24, decimal quoteLast)
    {
        var candles = new List<Candle>();
        for (var i = 95; i >= 1; i--)
        {
            candles.Add(new Candle(T.AddMinutes(-15 * i), low24 + 0.01m, low24 + 0.02m, low24, low24 + 0.01m, 1m, 1));
        }

        candles.Add(new Candle(T.AddMinutes(-15), close - 0.03m, recentHigh, close - 0.04m, close, 1m, 1));
        return new InstrumentMarketState
        {
            Instrument = new InstrumentOptions { Pair = "RIVER/USD", KrakenPair = "PF_RIVERUSD", Enabled = true },
            Candles = candles,
            Quote = new Quote(quoteLast - 0.001m, quoteLast + 0.001m, quoteLast, 100_000m)
        };
    }

    private static IReadOnlyList<PriceObservation> Observations(params decimal[] prices) =>
        prices.Select((price, index) => new PriceObservation(T.AddMinutes(index), price, price - 0.001m, price + 0.001m)).ToList();
}
