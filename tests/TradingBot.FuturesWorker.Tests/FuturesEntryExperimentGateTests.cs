using Xunit;

namespace TradingBot.FuturesWorker.Tests;

// The own-strategy experiment gate subtracts the two entry classes that stayed negative
// on held-out 2026 data. It only ever removes entries; both knobs default off, so the
// control account is untouched by the gate existing.
public sealed class FuturesEntryExperimentGateTests
{
    [Fact]
    public void Both_knobs_default_off_so_the_control_is_untouched()
    {
        var futures = new FuturesOptions();
        var shorts = new FuturesShortOptions();

        Assert.Empty(futures.DisabledLongEntryChannels);
        Assert.Null(shorts.MaxBtc24hRisePercentForShort);
        Assert.Null(FuturesEntryExperimentGate.Block(
            FuturesDesiredExposure.Long, "Continuation", futures, shorts, btc24hChangePct: 5m));
        Assert.Null(FuturesEntryExperimentGate.Block(
            FuturesDesiredExposure.Short, "ShortReclaim", futures, shorts, btc24hChangePct: 5m));
    }

    [Fact]
    public void A_disabled_long_channel_is_refused_and_names_itself()
    {
        var futures = new FuturesOptions { DisabledLongEntryChannels = ["Continuation"] };

        var reason = FuturesEntryExperimentGate.Block(
            FuturesDesiredExposure.Long, "Continuation", futures, new FuturesShortOptions(), null);

        Assert.NotNull(reason);
        Assert.StartsWith("EXPERIMENT_CHANNEL_DISABLED", reason);
    }

    // The gate blocks the channel, not the side: a Breakout long walks through, and a
    // SHORT whose channel happens to share a word with a disabled long one does too.
    [Fact]
    public void Other_channels_and_the_other_side_pass()
    {
        var futures = new FuturesOptions { DisabledLongEntryChannels = ["Continuation"] };

        Assert.Null(FuturesEntryExperimentGate.Block(
            FuturesDesiredExposure.Long, "Breakout", futures, new FuturesShortOptions(), null));
        Assert.Null(FuturesEntryExperimentGate.Block(
            FuturesDesiredExposure.Short, "ShortContinuation", futures, new FuturesShortOptions(), null));
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(6.95)]
    public void A_short_into_a_rising_btc_is_refused(decimal btc24h)
    {
        var shorts = new FuturesShortOptions { MaxBtc24hRisePercentForShort = 0m };

        var reason = FuturesEntryExperimentGate.Block(
            FuturesDesiredExposure.Short, "ShortReclaim", new FuturesOptions(), shorts, btc24h);

        Assert.NotNull(reason);
        Assert.StartsWith("EXPERIMENT_SHORT_BTC_RISING", reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3.02)]
    public void A_short_with_btc_flat_or_falling_passes(decimal btc24h)
    {
        var shorts = new FuturesShortOptions { MaxBtc24hRisePercentForShort = 0m };

        Assert.Null(FuturesEntryExperimentGate.Block(
            FuturesDesiredExposure.Short, "ShortReclaim", new FuturesOptions(), shorts, btc24h));
    }

    // The validated rule was "BTC demonstrably up", not "BTC unknown": a missing reading
    // allows the entry rather than quietly turning shorts off on every data hiccup.
    [Fact]
    public void A_missing_btc_reading_allows_the_short()
    {
        var shorts = new FuturesShortOptions { MaxBtc24hRisePercentForShort = 0m };

        Assert.Null(FuturesEntryExperimentGate.Block(
            FuturesDesiredExposure.Short, "ShortReclaim", new FuturesOptions(), shorts, null));
    }
}

// The reversal-exit switch defaults ON: a config that never mentions it keeps closing
// on faded signals, which is what the control account runs.
public sealed class FuturesSignalReversalSwitchTests
{
    [Fact]
    public void Reversal_exit_defaults_on()
    {
        Assert.True(new FuturesExitOptions().SignalReversalExitEnabled);
    }
}
