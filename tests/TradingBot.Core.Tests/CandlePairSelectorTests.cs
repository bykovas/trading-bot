using Xunit;

namespace TradingBot.Core.Tests;

public sealed class CandlePairSelectorTests
{
    [Fact]
    public void Select_includes_held_pairs_even_when_not_top_volume()
    {
        var universe = new[]
        {
            Instrument("LOW/EUR", "LOWEUR"),
            Instrument("HOLD/EUR", "HOLDEUR"),
            Instrument("TOP/EUR", "TOPEUR")
        };
        var lightStates = new[]
        {
            Light("TOP/EUR", volume: 1_000_000m, last: 10m, change: 0.5m),
            Light("LOW/EUR", volume: 1_000m, last: 1m, change: 0.1m),
            Light("HOLD/EUR", volume: 2_000m, last: 2m, change: 0.2m)
        };

        var selected = CandlePairSelector.Select(
            MarketDataVenue.Spot,
            universe,
            lightStates,
            ["HOLD/EUR"],
            new MarketDataIngestionOptions { TopVolumePairs = 1, MaxCandlePairs = 5 });

        Assert.Contains(selected, instrument => instrument.Pair == "HOLD/EUR");
        Assert.Contains(selected, instrument => instrument.Pair == "TOP/EUR");
    }

    [Fact]
    public void Select_includes_strong_movers()
    {
        var universe = new[] { Instrument("MOVE/EUR", "MOVEEUR") };
        var lightStates = new[] { Light("MOVE/EUR", volume: 10_000m, last: 5m, change: 4.5m) };

        var selected = CandlePairSelector.Select(
            MarketDataVenue.Spot,
            universe,
            lightStates,
            [],
            new MarketDataIngestionOptions { StrongMoverPercent = 3m, MaxCandlePairs = 5 });

        Assert.Equal("MOVE/EUR", Assert.Single(selected).Pair);
    }

    [Fact]
    public void Select_prioritizes_strong_movers_before_pure_volume_when_capped()
    {
        var universe = new[]
        {
            Instrument("VOLUME/EUR", "VOLUMEEUR"),
            Instrument("MOVE/EUR", "MOVEEUR"),
            Instrument("OTHER/EUR", "OTHEREUR")
        };
        var lightStates = new[]
        {
            Light("VOLUME/EUR", volume: 1_000_000m, last: 10m, change: 0.5m),
            Light("MOVE/EUR", volume: 10_000m, last: 1m, change: 12m),
            Light("OTHER/EUR", volume: 9_000m, last: 1m, change: 8m)
        };

        var selected = CandlePairSelector.Select(
            MarketDataVenue.Futures,
            universe,
            lightStates,
            [],
            new MarketDataIngestionOptions
            {
                TopVolumePairs = 1,
                TopMoverPairs = 2,
                StrongMoverPercent = 3m,
                MaxCandlePairs = 2
            });

        Assert.Equal(["MOVE/EUR", "OTHER/EUR"], selected.Select(instrument => instrument.Pair).ToArray());
    }

    private static InstrumentOptions Instrument(string pair, string krakenPair) => new()
    {
        Pair = pair,
        KrakenPair = krakenPair,
        Enabled = true
    };

    private static InstrumentMarketState Light(string pair, decimal volume, decimal last, decimal change) => new()
    {
        Instrument = Instrument(pair, pair.Replace("/", string.Empty)),
        Candles = Array.Empty<Candle>(),
        Quote = new Quote(last * 0.999m, last * 1.001m, last, volume, change)
    };
}
