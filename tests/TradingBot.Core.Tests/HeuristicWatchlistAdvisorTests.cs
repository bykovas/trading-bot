
using Xunit;

namespace TradingBot.Core.Tests;

public class HeuristicWatchlistAdvisorTests
{
    [Fact]
    public async Task Ranks_by_eur_notional_not_raw_base_volume()
    {
        var advisor = new HeuristicWatchlistAdvisor();
        var candidates = new[]
        {
            // High price, low unit volume => large EUR notional (600,000).
            LightState("BTC/EUR", last: 60000m, volumeToday: 10m),
            // Tiny price, huge unit volume => smaller EUR notional (100,000).
            LightState("DOGE/EUR", last: 0.1m, volumeToday: 1_000_000m)
        };

        var advice = await advisor.SelectAsync(candidates, 20, CancellationToken.None);

        Assert.Equal("BTC/EUR", advice.Recommendations[0].Pair);
        Assert.Equal("DOGE/EUR", advice.Recommendations[1].Pair);
    }

    private static InstrumentMarketState LightState(string pair, decimal last, decimal volumeToday) => new()
    {
        Instrument = new InstrumentOptions { Pair = pair, KrakenPair = pair.Replace("/", string.Empty, StringComparison.Ordinal), Venue = "Kraken", Enabled = true },
        Candles = Array.Empty<Candle>(),
        Quote = new Quote(last * 0.999m, last * 1.001m, last, volumeToday, 0m)
    };
}
