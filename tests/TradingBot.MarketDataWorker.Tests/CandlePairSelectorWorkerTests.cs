using Xunit;

namespace TradingBot.MarketDataWorker.Tests;

public sealed class CandlePairSelectorWorkerTests
{
    [Fact]
    public void Held_pair_is_prioritized_in_selection_order()
    {
        var universe = new[]
        {
            new InstrumentOptions { Pair = "AAA/USD", KrakenPair = "PF_AAAUSD", Enabled = true },
            new InstrumentOptions { Pair = "BBB/USD", KrakenPair = "PF_BBBUSD", Enabled = true }
        };
        var lightStates = new[]
        {
            State("AAA/USD", 100m, 1m),
            State("BBB/USD", 1_000_000m, 10m)
        };

        var selected = CandlePairSelector.Select(
            MarketDataVenue.Futures,
            universe,
            lightStates,
            ["AAA/USD"],
            new MarketDataIngestionOptions { TopVolumePairs = 1, MaxCandlePairs = 2 });

        Assert.Equal("AAA/USD", selected[0].Pair);
    }

    private static InstrumentMarketState State(string pair, decimal volume, decimal last) => new()
    {
        Instrument = new InstrumentOptions { Pair = pair, KrakenPair = $"PF_{pair.Replace("/", string.Empty)}", Enabled = true },
        Candles = Array.Empty<Candle>(),
        Quote = new Quote(last, last, last, volume, 1m)
    };
}
