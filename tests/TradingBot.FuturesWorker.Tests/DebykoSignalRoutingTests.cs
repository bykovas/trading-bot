using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class DebykoSignalRoutingTests
{
    [Theory]
    [InlineData("BYKO_FLUSH_LONG", "Long")]
    [InlineData("BYKO_BREAKOUT_LONG", "Long")]
    [InlineData("BYKO_FLUSH_SHORT", "Short")]
    [InlineData("BYKO_BREAKOUT_SHORT", "Short")]
    public void Supported_rule_tags_map_to_the_expected_side(
        string tag,
        string expected)
    {
        Assert.True(FuturesDecisionWorker.TryResolveDebykoRule(tag, out var actual, out var rule));
        Assert.Equal(expected, actual.ToString());
        Assert.Equal(tag, rule);
    }

    [Fact]
    public void Unknown_rule_tag_is_rejected()
    {
        Assert.False(FuturesDecisionWorker.TryResolveDebykoRule(
            "SOMETHING_ELSE",
            out var actual,
            out _));
        Assert.Equal(FuturesDesiredExposure.Flat, actual);
    }

    [Theory]
    [InlineData("BYKO_V2_FLUSH_LONG", "BYKO_FLUSH_LONG", "Long")]
    [InlineData("BYKO_V2_FLUSH_SHORT", "BYKO_FLUSH_SHORT", "Short")]
    [InlineData("BYKO_V2_BREAKOUT_LONG", "BYKO_BREAKOUT_LONG", "Long")]
    [InlineData("BYKO_V2_BREAKOUT_SHORT", "BYKO_BREAKOUT_SHORT", "Short")]
    public void Agent_labels_resolve_to_canonical_shadow_rules(string tag, string canonical, string side)
    {
        Assert.True(FuturesDecisionWorker.TryResolveDebykoRule(tag, out var actual, out var rule));
        Assert.Equal(canonical, rule);
        Assert.Equal(side, actual.ToString());
    }

    [Theory]
    [InlineData("BTC-PERP", "XBT/USD")]
    [InlineData("DOGE-PERP", "XDG/USD")]
    [InlineData("AAVE-PERP", "AAVE/USD")]
    public void Debyko_instruments_map_to_the_configured_Kraken_pair(
        string debykoInstrument,
        string expectedPair)
    {
        var universe = new[]
        {
            new InstrumentOptions { Pair = expectedPair, KrakenPair = "PF_TESTUSD", Enabled = true }
        };

        var resolved = FuturesDecisionWorker.ResolveDebykoInstrument(debykoInstrument, universe);

        Assert.NotNull(resolved);
        Assert.Equal(expectedPair, resolved.Pair);
    }
}
