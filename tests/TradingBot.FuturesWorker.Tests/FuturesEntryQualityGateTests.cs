using TradingBot.Core.Signals;
using TradingBot.FuturesWorker;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

// Market-quality gate parity with the spot worker: spread limit, anti-lag price
// action (direction-aware for shorts), warm-up requirement, anti-extension.
public class FuturesEntryQualityGateTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-11T12:00:00Z");

    private static InstrumentMarketState MarketState(decimal bid, decimal ask, decimal last) => new()
    {
        Instrument = new InstrumentOptions { Pair = "XBT/USD", KrakenPair = "PF_XBTUSD", Venue = "KrakenFutures", Enabled = true },
        Candles = [new Candle(Now, last, last, last, last, 100m, 10)],
        Quote = new Quote(bid, ask, last, 1_000_000m)
    };

    private static StrategyOptions Strategy() => new()
    {
        MaxEntrySpreadPercent = 0.15m,
        MaxEntryExtensionPercent = 0.6m,
        MaxEntryRunupPercent = 2.5m,
        PriceActionLookbackSnapshots = 6,
        PriceActionMinSnapshots = 4,
        PriceActionMaxDeclinePercent = 0.5m,
        PriceActionMaxNonRisingSnapshots = 3
    };

    private static IndicatorSnapshot Indicators(decimal fastEma = 100m) => new(fastEma, fastEma * 0.995m, 55m);

    private static PriceActionAssessment Assessment(decimal trendPercent, int nonRising = 0) =>
        new("XBT/USD", 6, true, 100m, trendPercent, 100m, nonRising, 4, Now.AddMinutes(-25), Now);

    [Fact]
    public void Wide_spread_blocks_the_entry()
    {
        var result = FuturesEntryQualityGate.Evaluate(
            MarketState(bid: 99.5m, ask: 100.5m, last: 100m), // 1% spread
            Indicators(),
            FuturesDesiredExposure.Long,
            Assessment(0.1m),
            Strategy());

        Assert.False(result.Approved);
        Assert.Contains(result.Reasons, reason => reason.Contains("spread"));
    }

    [Fact]
    public void Falling_tape_blocks_a_long_and_rising_tape_blocks_a_short()
    {
        var tightBook = MarketState(bid: 99.99m, ask: 100.01m, last: 100m);

        var longBlocked = FuturesEntryQualityGate.Evaluate(
            tightBook, Indicators(), FuturesDesiredExposure.Long, Assessment(-0.8m), Strategy());
        Assert.False(longBlocked.Approved);
        Assert.Contains(longBlocked.Reasons, reason => reason.Contains("blocks long"));

        var shortBlocked = FuturesEntryQualityGate.Evaluate(
            tightBook, Indicators(), FuturesDesiredExposure.Short, Assessment(0.8m), Strategy());
        Assert.False(shortBlocked.Approved);
        Assert.Contains(shortBlocked.Reasons, reason => reason.Contains("blocks short"));

        // Mirror directions pass: falling tape is fine for a short, rising for a long.
        Assert.True(FuturesEntryQualityGate.Evaluate(
            tightBook, Indicators(fastEma: 100.2m), FuturesDesiredExposure.Short, Assessment(-0.3m), Strategy()).Approved);
        Assert.True(FuturesEntryQualityGate.Evaluate(
            tightBook, Indicators(), FuturesDesiredExposure.Long, Assessment(0.3m), Strategy()).Approved);
    }

    [Fact]
    public void Extended_price_blocks_in_the_trade_direction_only()
    {
        var tightBook = MarketState(bid: 99.99m, ask: 100.01m, last: 100m);

        // Last 100 vs fast EMA 99 -> +1.01% above: a long is a chase, a short is not.
        var longBlocked = FuturesEntryQualityGate.Evaluate(
            tightBook, Indicators(fastEma: 99m), FuturesDesiredExposure.Long, Assessment(0.1m), Strategy());
        Assert.False(longBlocked.Approved);
        Assert.Contains(longBlocked.Reasons, reason => reason.Contains("above fast EMA"));

        var shortAllowed = FuturesEntryQualityGate.Evaluate(
            tightBook, Indicators(fastEma: 99m), FuturesDesiredExposure.Short, Assessment(-0.1m), Strategy());
        Assert.True(shortAllowed.Approved);

        // Mirror: last 100 vs fast EMA 101 -> -0.99% below blocks the short.
        var shortBlocked = FuturesEntryQualityGate.Evaluate(
            tightBook, Indicators(fastEma: 101m), FuturesDesiredExposure.Short, Assessment(-0.1m), Strategy());
        Assert.False(shortBlocked.Approved);
        Assert.Contains(shortBlocked.Reasons, reason => reason.Contains("below fast EMA"));
    }

    [Fact]
    public void Excessive_runup_blocks_in_the_trade_direction()
    {
        var tightBook = MarketState(bid: 99.99m, ask: 100.01m, last: 100m);

        var longBlocked = FuturesEntryQualityGate.Evaluate(
            tightBook, Indicators(), FuturesDesiredExposure.Long, Assessment(3.0m), Strategy());
        Assert.False(longBlocked.Approved);
        Assert.Contains(longBlocked.Reasons, reason => reason.Contains("run-up"));

        var shortBlocked = FuturesEntryQualityGate.Evaluate(
            tightBook, Indicators(fastEma: 100.2m), FuturesDesiredExposure.Short, Assessment(-3.0m), Strategy());
        Assert.False(shortBlocked.Approved);
        Assert.Contains(shortBlocked.Reasons, reason => reason.Contains("sell-off"));
    }

    [Fact]
    public void Warmup_requirement_blocks_entries_without_price_action_history()
    {
        var strategy = Strategy();
        strategy.RequirePriceActionData = true;

        var result = FuturesEntryQualityGate.Evaluate(
            MarketState(bid: 99.99m, ask: 100.01m, last: 100m),
            Indicators(),
            FuturesDesiredExposure.Long,
            priceAction: null,
            strategy);

        Assert.False(result.Approved);
        Assert.Contains(result.Reasons, reason => reason.Contains("warm-up"));
    }

    [Fact]
    public void Flat_desire_always_passes()
    {
        var result = FuturesEntryQualityGate.Evaluate(
            MarketState(bid: 99.5m, ask: 100.5m, last: 100m),
            Indicators(),
            FuturesDesiredExposure.Flat,
            priceAction: null,
            Strategy());

        Assert.True(result.Approved);
    }
}
