using Xunit;

namespace TradingBot.Core.Tests;

public sealed class ShortSignalScorerTests
{
    [Fact]
    public void Bearish_setup_with_downside_confirmation_emits_short_candidate()
    {
        var strategy = new StrategyOptions
        {
            FastEmaPeriod = 3,
            SlowEmaPeriod = 6,
            RsiPeriod = 3,
            MinimumEmaGapPercent = 0.05m,
            MinimumLongScore = 0.80m,
            MomentumLookbackBars = 3,
            MomentumMinPercent = 0.2m,
            TrendFilterMaPeriod = 8,
            VolumeConfirmationMultiple = 1.1m
        };
        var candles = FallingCandles();
        var indicators = new IndicatorEngine().Calculate(candles, strategy);
        var state = new InstrumentMarketState
        {
            Instrument = new InstrumentOptions { Pair = "AAA/EUR", KrakenPair = "PF_AAAEUR" },
            Candles = candles,
            Quote = new Quote(90m, 90.1m, 90m, 1000m)
        };

        var signal = SignalScorer.Evaluate(state, indicators, strategy);

        Assert.True(signal.AllowsShort);
        Assert.True(signal.HasBearishStructure);
        Assert.Equal(SignalIntent.ShortCandidate, SignalScorer.IntentOf(signal, strategy));
        Assert.Contains(signal.Contributions, contribution => contribution.Name == "ShortMomentum" && contribution.Value > 0m);
    }

    [Fact]
    public void Bearish_ema_without_confirmation_stays_none()
    {
        var strategy = new StrategyOptions
        {
            FastEmaPeriod = 3,
            SlowEmaPeriod = 6,
            RsiPeriod = 3,
            MinimumEmaGapPercent = 0.05m,
            MinimumLongScore = 0.80m,
            MomentumLookbackBars = 3,
            MomentumMinPercent = 5m,
            TrendFilterMaPeriod = 100,
            VolumeConfirmationMultiple = 50m
        };
        var candles = FallingCandles();
        var indicators = new IndicatorEngine().Calculate(candles, strategy);
        var state = new InstrumentMarketState
        {
            Instrument = new InstrumentOptions { Pair = "AAA/EUR", KrakenPair = "PF_AAAEUR" },
            Candles = candles,
            Quote = new Quote(90m, 90.1m, 90m, 1000m)
        };

        var signal = SignalScorer.Evaluate(state, indicators, strategy);

        Assert.False(signal.AllowsShort);
        Assert.Equal(SignalIntent.None, SignalScorer.IntentOf(signal, strategy));
    }

    private static IReadOnlyList<Candle> FallingCandles()
    {
        var start = DateTimeOffset.Parse("2026-07-06T00:00:00Z");
        return Enumerable.Range(0, 40)
            .Select(index =>
            {
                var close = 120m - index * 0.8m;
                return new Candle(
                    start.AddMinutes(index * 15),
                    close + 0.2m,
                    close + 0.6m,
                    close - 0.6m,
                    close,
                    100m + index * 5m,
                    10);
            })
            .ToList();
    }
}
