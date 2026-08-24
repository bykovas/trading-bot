using Xunit;

namespace TradingBot.FuturesWorker.Tests;

// The mirror decides per trade whether a copied entry is turned around, from BTC's 24h
// change. Unlike the own-signal flip this rule is symmetric: it inverts SHORT into LONG
// as readily as LONG into SHORT, because a mirror copies both directions and inverting
// only one of them would leave the follower neither a copy nor an opposite.
public sealed class FuturesMirrorFlipGateTests
{
    [Fact]
    public void Inversion_disabled_means_the_gate_never_turns_anything_around()
    {
        // The switch off is the state both live accounts run in: behaviour must be
        // identical to having no gate at all.
        var decision = FuturesMirrorFlipGate.Evaluate(Options(invert: false), btc24hChangePct: -9m);

        Assert.False(decision.Permitted);
        Assert.False(decision.Invert);
    }

    [Theory]
    [InlineData(-4.2)]
    [InlineData(-0.01)]
    [InlineData(0)]
    public void Btc_flat_or_falling_inverts_the_copy(decimal btc24h)
    {
        var decision = FuturesMirrorFlipGate.Evaluate(Options(invert: true), btc24h);

        Assert.True(decision.Permitted);
        Assert.True(decision.Invert);
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(7.3)]
    public void Btc_rising_copies_the_source_as_it_is(decimal btc24h)
    {
        var decision = FuturesMirrorFlipGate.Evaluate(Options(invert: true), btc24h);

        Assert.True(decision.Permitted);
        Assert.False(decision.Invert);
    }

    // 19-21 August: BTC ran +7%, +5%, +7% while the mirror inverted every entry
    // unconditionally and the follower lost 26.91 over four days. Under this rule
    // those three days would have been copied, not inverted.
    [Fact]
    public void A_rally_is_not_a_countertrend_regime()
    {
        foreach (var btc24h in new[] { 6.95m, 5.25m, 6.66m })
        {
            Assert.False(FuturesMirrorFlipGate.Evaluate(Options(invert: true), btc24h).Invert);
        }
    }

    // No reading is not a licence to guess: copying is what the follower does on every
    // day the switch is off, so it is the safe answer when the regime is unknown.
    [Fact]
    public void A_missing_btc_reading_copies_rather_than_inverts()
    {
        var decision = FuturesMirrorFlipGate.Evaluate(Options(invert: true), btc24hChangePct: null);

        Assert.True(decision.Permitted);
        Assert.False(decision.Invert);
        Assert.Contains("unavailable", decision.Reason);
    }

    [Fact]
    public void The_threshold_is_configurable_and_defaults_to_zero()
    {
        Assert.Equal(0m, new FuturesEntryMirrorOptions().InvertMaxBtc24hRisePercent);

        var lenient = Options(invert: true);
        lenient.InvertMaxBtc24hRisePercent = 3m;
        Assert.True(FuturesMirrorFlipGate.Evaluate(lenient, 2.5m).Invert);
        Assert.False(FuturesMirrorFlipGate.Evaluate(lenient, 3.5m).Invert);
    }

    private static FuturesEntryMirrorOptions Options(bool invert) => new()
    {
        FollowSourceBotInstanceId = "futures-lukas-live",
        InvertSide = invert
    };
}
