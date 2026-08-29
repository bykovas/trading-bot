using Xunit;

namespace TradingBot.FuturesWorker.Tests;

// A trailing stop only a hair wider than the spread is closed by the bid-ask bouncing
// rather than by the move reversing. MSTRX on 2026-08-28 armed a 0.25% trail against a
// 0.17% spread - 1.5x - which is noise, not protection. These pin the floor that fixes
// it, and every case where it must NOT widen anything.
public sealed class TrailingStopSpreadFloorTests
{
    private static TpSlOptions Options(decimal multiple = 2m, decimal stopLoss = 1.5m) => new()
    {
        TrailingStopMinSpreadMultiple = multiple,
        StopLossPercent = stopLoss
    };

    // The real MSTRX book: bid 129.78 / ask 130.00 is a 0.169% spread, so a 0.25% trail
    // is widened to 2 x 0.169 = 0.3387%, then rounded UP to 0.34% - Kraken rejects a
    // deviation with more than two decimal places.
    [Fact]
    public void A_trail_narrower_than_twice_the_spread_is_widened_and_rounded()
    {
        var spread = (130.00m - 129.78m) / ((130.00m + 129.78m) / 2m) * 100m;
        var effective = Options().EffectiveTrailingStopPercent(0.25m, spread);

        Assert.Equal(0.34m, effective);
        Assert.True(effective >= 2m * spread);
    }

    // Whatever the arithmetic, the value sent to the exchange never has more than two
    // decimal places - the HTTP 400 that fired the alert on 2026-08-29.
    [Fact]
    public void The_result_never_exceeds_two_decimal_places()
    {
        foreach (var spread in new[] { 0.169374m, 0.3m, 0.4211m, 0.5555m, 0.71m, 1.001m })
        {
            var effective = Options().EffectiveTrailingStopPercent(0.25m, spread);
            Assert.Equal(effective, decimal.Round(effective, 2));
        }
    }

    // A tight book leaves the configured distance exactly as it is - the floor is a
    // floor, never a target.
    [Fact]
    public void A_tight_spread_leaves_the_configured_distance_alone()
    {
        Assert.Equal(0.25m, Options().EffectiveTrailingStopPercent(0.25m, 0.05m));
        Assert.Equal(0.25m, Options().EffectiveTrailingStopPercent(0.25m, 0.125m));
    }

    // A trail wider than the stop it replaced protects nothing, so the widening stops
    // at the working stop however wide the book gets.
    [Fact]
    public void The_widened_distance_is_capped_at_the_working_stop()
    {
        Assert.Equal(1.5m, Options().EffectiveTrailingStopPercent(0.25m, 5m));
        Assert.Equal(1.5m, Options().EffectiveTrailingStopPercent(0.25m, 0.75m));
    }

    // A configured trail already wider than the cap is never shrunk - the cap bounds
    // the WIDENING, it does not override an explicit setting.
    [Fact]
    public void A_configured_trail_wider_than_the_cap_survives()
    {
        Assert.Equal(2m, Options().EffectiveTrailingStopPercent(2m, 5m));
        Assert.Equal(2m, Options().EffectiveTrailingStopPercent(2m, 0.01m));
    }

    // Off by default, and a spread we cannot trust changes nothing: widening on a bad
    // reading is worse than not widening.
    [Fact]
    public void The_floor_is_inert_without_a_multiple_or_a_usable_spread()
    {
        Assert.Equal(0m, new TpSlOptions().TrailingStopMinSpreadMultiple);
        Assert.Equal(0.25m, Options(multiple: 0m).EffectiveTrailingStopPercent(0.25m, 5m));
        Assert.Equal(0.25m, Options().EffectiveTrailingStopPercent(0.25m, null));
        Assert.Equal(0.25m, Options().EffectiveTrailingStopPercent(0.25m, 0m));
        Assert.Equal(0.25m, Options().EffectiveTrailingStopPercent(0.25m, -1m));
    }

    // The max-hold handoff arms its own, wider distance; the floor must respect it the
    // same way and not quietly pull it back to the take-profit trail.
    [Fact]
    public void The_max_hold_handoff_distance_is_floored_the_same_way()
    {
        Assert.Equal(0.5m, Options().EffectiveTrailingStopPercent(0.5m, 0.1m));
        Assert.Equal(0.6m, Options().EffectiveTrailingStopPercent(0.5m, 0.3m), 6);
    }
}
