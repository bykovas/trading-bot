using TradingBot.Core.Signals;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class FuturesEntryFreshnessGuardTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-07-13T14:26:23Z");

    private static FuturesFreshnessOptions Thresholds() => new();

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
            Thresholds());

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
            Market(0.21000247245m, recentHigh: 0.21243989865m, low24: 0.196145m, quoteLast: 0.21085227177m),
            Observations(0.21011995958m, 0.21042458073m, 0.21085227177m),
            FuturesDesiredExposure.Long,
            Thresholds());

        Assert.False(result.Blocked);
        Assert.True(result.HasFreshUpwardTape);
        Assert.False(result.HasFreshBreakout);
    }

    [Fact]
    public void Genuine_breakout_with_confirming_tape_is_not_blocked()
    {
        var result = FuturesEntryFreshnessGuard.Evaluate(
            Market(100m, recentHigh: 100m, low24: 80m, quoteLast: 100.08m),
            Observations(99.8m, 100.01m, 100.08m),
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
            Market(99.9m, recentHigh: 100m, low24: 80m, quoteLast: 100.02m),
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
