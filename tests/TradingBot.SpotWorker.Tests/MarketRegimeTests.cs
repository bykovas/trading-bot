using TradingBot.SpotWorker;
using Xunit;

namespace TradingBot.SpotWorker.Tests;

// Market-regime entry filter: a long-only book must not add risk into a broadly
// falling market. The regime is BAD only when every ENABLED condition reads bad;
// missing data never reads bad (block on evidence, not on ignorance).
public class MarketRegimeTests
{
    private static InstrumentMarketState Snapshot(string pair, decimal changePercent) => new()
    {
        Instrument = new InstrumentOptions { Pair = pair, KrakenPair = pair.Replace("/", string.Empty, StringComparison.Ordinal), Venue = "Kraken", Enabled = true },
        Candles = Array.Empty<Candle>(),
        Quote = new Quote(Bid: 0.999m, Ask: 1.001m, Last: 1m, VolumeToday: 1000m, ChangePercent: changePercent)
    };

    private static StrategyOptions Filter(decimal btcDecline = 1.0m, decimal minBreadth = 40m) => new()
    {
        RegimeFilterMaxBtcDeclinePercent = btcDecline,
        RegimeFilterMinBreadthPercent = minBreadth,
        RegimeFilterReferencePair = "XBT/EUR"
    };

    [Fact]
    public void Falling_btc_with_weak_breadth_blocks_new_entries()
    {
        var snapshots = new[]
        {
            Snapshot("XBT/EUR", -1.5m),
            Snapshot("AAA/EUR", -2.0m),
            Snapshot("BBB/EUR", -1.0m),
            Snapshot("CCC/EUR", 0.5m)
        };

        var regime = DecisionWorker.EvaluateMarketRegime(snapshots, Filter());

        Assert.True(regime.BlockNewEntries);
    }

    [Fact]
    public void Falling_btc_with_healthy_breadth_does_not_block()
    {
        var snapshots = new[]
        {
            Snapshot("XBT/EUR", -1.5m),
            Snapshot("AAA/EUR", 2.0m),
            Snapshot("BBB/EUR", 1.0m),
            Snapshot("CCC/EUR", -0.5m)
        };

        var regime = DecisionWorker.EvaluateMarketRegime(snapshots, Filter());

        Assert.False(regime.BlockNewEntries);
    }

    [Fact]
    public void Rising_btc_never_blocks_even_with_weak_breadth()
    {
        var snapshots = new[]
        {
            Snapshot("XBT/EUR", 0.5m),
            Snapshot("AAA/EUR", -2.0m),
            Snapshot("BBB/EUR", -1.0m),
            Snapshot("CCC/EUR", -0.5m)
        };

        var regime = DecisionWorker.EvaluateMarketRegime(snapshots, Filter());

        Assert.False(regime.BlockNewEntries);
    }

    [Fact]
    public void Disabled_knobs_turn_the_filter_off()
    {
        var snapshots = new[] { Snapshot("XBT/EUR", -9m), Snapshot("AAA/EUR", -9m) };

        var regime = DecisionWorker.EvaluateMarketRegime(snapshots, Filter(btcDecline: 0m, minBreadth: 0m));

        Assert.False(regime.BlockNewEntries);
    }

    [Fact]
    public void Missing_reference_pair_falls_back_to_the_breadth_condition_alone()
    {
        var snapshots = new[]
        {
            Snapshot("AAA/EUR", -2.0m),
            Snapshot("BBB/EUR", -1.0m),
            Snapshot("CCC/EUR", -0.5m)
        };

        var regime = DecisionWorker.EvaluateMarketRegime(snapshots, Filter());

        // Reference pair missing -> its condition abstains; breadth 0% < 40% blocks.
        Assert.True(regime.BlockNewEntries);
    }
}
