using TradingBot.Worker;
using Xunit;

namespace TradingBot.Worker.Tests;

public class TechnicalDecisionEngineTests
{
    private static readonly TradingOptions Trading = new()
    {
        TargetOrderEur = 10m
    };

    private static StrategyOptions Strategy(decimal minimumEmaGapPercent) => new()
    {
        MinimumEmaGapPercent = minimumEmaGapPercent,
        MinimumLongScore = 0.65m
    };

    private static InstrumentMarketState MarketState() => new()
    {
        Instrument = new InstrumentOptions { Pair = "UNI/EUR", KrakenPair = "UNIEUR", Venue = "Kraken", Enabled = true },
        Candles = Enumerable.Range(0, 30)
            .Select(index => new Candle(
                DateTimeOffset.UtcNow.AddMinutes(-index),
                Open: 100m,
                High: 100m,
                Low: 100m,
                Close: 100m,
                Volume: 100m,
                TradeCount: 10))
            .ToArray()
    };

    private static DecisionProposal Decide(decimal fastEma, decimal slowEma, decimal minimumEmaGapPercent)
    {
        var engine = new TechnicalDecisionEngine();
        return engine.Decide(
            MarketState(),
            new IndicatorSnapshot(fastEma, slowEma, Rsi: 50m),
            Trading,
            Strategy(minimumEmaGapPercent));
    }

    [Fact]
    public void Ema_gap_below_threshold_has_no_ema_contribution_and_blocks_long()
    {
        var proposal = Decide(fastEma: 100.049m, slowEma: 100m, minimumEmaGapPercent: 0.05m);

        var ema = Assert.Single(proposal.Contributions, contribution => contribution.Name == "EMA");
        Assert.Equal(0m, ema.Value);
        Assert.Contains("EMA crossover ignored because gap 0.049% < configured minimum 0.050%", ema.Reason);
        Assert.Equal("NONE", proposal.DesiredPosition);
        Assert.Equal(0.55m, proposal.Score);
    }

    [Fact]
    public void Ema_gap_exactly_on_threshold_contributes_and_allows_long()
    {
        var proposal = Decide(fastEma: 100.05m, slowEma: 100m, minimumEmaGapPercent: 0.05m);

        var ema = Assert.Single(proposal.Contributions, contribution => contribution.Name == "EMA");
        Assert.Equal(0.30m, ema.Value);
        Assert.Equal("LONG_MICRO", proposal.DesiredPosition);
        Assert.Equal(0.85m, proposal.Score);
    }

    [Fact]
    public void Ema_gap_above_threshold_contributes_and_allows_long()
    {
        var proposal = Decide(fastEma: 100.051m, slowEma: 100m, minimumEmaGapPercent: 0.05m);

        var ema = Assert.Single(proposal.Contributions, contribution => contribution.Name == "EMA");
        Assert.Equal(0.30m, ema.Value);
        Assert.Equal("LONG_MICRO", proposal.DesiredPosition);
        Assert.Equal(0.85m, proposal.Score);
    }

    [Fact]
    public void Zero_threshold_disables_ema_gap_filter()
    {
        var proposal = Decide(fastEma: 100.001m, slowEma: 100m, minimumEmaGapPercent: 0m);

        var ema = Assert.Single(proposal.Contributions, contribution => contribution.Name == "EMA");
        Assert.Equal(0.30m, ema.Value);
        Assert.Equal("LONG_MICRO", proposal.DesiredPosition);
        Assert.Equal(0.85m, proposal.Score);
    }

    [Fact]
    public void Bearish_ema_gap_below_threshold_has_no_negative_ema_contribution()
    {
        var proposal = Decide(fastEma: 99.951m, slowEma: 100m, minimumEmaGapPercent: 0.05m);

        var ema = Assert.Single(proposal.Contributions, contribution => contribution.Name == "EMA");
        Assert.Equal(0m, ema.Value);
        Assert.Contains("EMA crossover ignored because gap 0.049% < configured minimum 0.050%", ema.Reason);
        Assert.Equal("NONE", proposal.DesiredPosition);
        Assert.Equal(0.55m, proposal.Score);
    }
}
