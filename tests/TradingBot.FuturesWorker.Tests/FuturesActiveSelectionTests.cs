using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class FuturesActiveSelectionTests
{
    [Fact]
    public void Force_include_pair_is_appended_after_the_normal_active_selection()
    {
        var states = new[]
        {
            State("ETH/USD", -0.67m, volume: 88_000_000m),
            State("UNI/USD", 10.35m),
            State("SYN/USD", -13.47m),
            State("RLC/USD", 11.73m)
        };
        var trading = new TradingOptions
        {
            MaxActiveInstruments = 2,
            StrongMoverMinChangePercent = 3m,
            StrongMoverMinDailyVolumeEur = 10_000m
        };

        var selected = FuturesDecisionWorker.SelectActiveInstruments(
            states,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new[] { "ETH/USD" },
            trading);

        Assert.Equal(3, selected.Count);
        Assert.Equal("SYN/USD", selected[0].Pair);
        Assert.Equal("RLC/USD", selected[1].Pair);
        Assert.Contains(selected, instrument => instrument.Pair == "ETH/USD");
    }

    [Fact]
    public void Force_include_only_appends_missing_pairs()
    {
        var states = new[]
        {
            State("ETH/USD", -0.67m),
            State("SOL/USD", 12.42m),
            State("UNI/USD", 10.35m)
        };
        var trading = new TradingOptions
        {
            MaxActiveInstruments = 1,
            StrongMoverMinChangePercent = 3m,
            StrongMoverMinDailyVolumeEur = 10_000m
        };

        var selected = FuturesDecisionWorker.SelectActiveInstruments(
            states,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new[] { "PF_ETHUSD", "SOL/USD" },
            trading);

        Assert.Equal(2, selected.Count);
        Assert.Equal("SOL/USD", selected[0].Pair);
        Assert.Contains(selected, instrument => instrument.Pair == "ETH/USD");
        Assert.Contains(selected, instrument => instrument.Pair == "SOL/USD");
    }

    private static InstrumentMarketState State(string pair, decimal changePercent, decimal last = 1m, decimal volume = 20_000m) =>
        new()
        {
            Instrument = new InstrumentOptions
            {
                Pair = pair,
                KrakenPair = $"PF_{pair.Replace("/", string.Empty, StringComparison.OrdinalIgnoreCase)}",
                Venue = "KrakenFutures",
                Enabled = true
            },
            Candles = Array.Empty<Candle>(),
            Quote = new Quote(last, last, last, volume, changePercent)
        };
}
