using TradingBot.SpotWorker;
using Xunit;

namespace TradingBot.SpotWorker.Tests;

public class MarketRegimeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-07T12:00:00Z");

    [Fact]
    public void Btc_fast_drawdown_blocks_new_longs()
    {
        var candles = BtcCandles(Enumerable.Range(0, 60).Select(i => i < 56 ? 100m : 97m).ToArray());

        var regime = DecisionWorker.EvaluateBtcRegime([State(candles)], new BtcRegimeOptions(), 15, Now);

        Assert.True(regime.BlockNewEntries);
        Assert.Contains("CRASH", regime.Description);
    }

    [Fact]
    public void Btc_below_falling_ma_blocks_new_longs()
    {
        var candles = BtcCandles(Enumerable.Range(0, 60).Select(i => 120m - i * 0.2m).ToArray());

        var regime = DecisionWorker.EvaluateBtcRegime([State(candles)], new BtcRegimeOptions(), 15, Now);

        Assert.True(regime.BlockNewEntries);
        Assert.Contains("DOWNTREND", regime.Description);
    }

    [Fact]
    public void Btc_above_nonfalling_ma_allows_new_longs()
    {
        var candles = BtcCandles(Enumerable.Range(0, 60).Select(i => 100m + i * 0.2m).ToArray());

        var regime = DecisionWorker.EvaluateBtcRegime([State(candles)], new BtcRegimeOptions(), 15, Now);

        Assert.False(regime.BlockNewEntries);
        Assert.Contains("OK", regime.Description);
    }

    [Fact]
    public void Missing_btc_candles_fail_closed()
    {
        var regime = DecisionWorker.EvaluateBtcRegime(Array.Empty<InstrumentMarketState>(), new BtcRegimeOptions(), 15, Now);

        Assert.True(regime.BlockNewEntries);
        Assert.Contains("fail-closed", regime.Description);
    }

    [Fact]
    public void Stale_btc_candles_fail_closed()
    {
        var staleNow = Now.AddHours(2);

        var regime = DecisionWorker.EvaluateBtcRegime([State(BtcCandles(Enumerable.Range(0, 60).Select(i => 100m + i).ToArray()))], new BtcRegimeOptions(), 15, staleNow);

        Assert.True(regime.BlockNewEntries);
        Assert.Contains("stale", regime.Description);
    }

    private static InstrumentMarketState State(IReadOnlyList<Candle> candles) => new()
    {
        Instrument = new InstrumentOptions { Pair = "XBT/EUR", KrakenPair = "XBTEUR", Venue = "Kraken", Enabled = true },
        Candles = candles,
        Quote = new Quote(Bid: candles[^1].Close - 1m, Ask: candles[^1].Close + 1m, Last: candles[^1].Close, VolumeToday: 1000m)
    };

    private static IReadOnlyList<Candle> BtcCandles(IReadOnlyList<decimal> closes)
    {
        var start = Now.AddMinutes(-15 * closes.Count);
        return closes.Select((close, index) => new Candle(
            start.AddMinutes(15 * index),
            close,
            close + 1m,
            close - 1m,
            close,
            100m,
            10)).ToList();
    }
}
