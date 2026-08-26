using Xunit;

namespace TradingBot.Core.Tests;

// EMA precision on sub-cent instruments. The engine used to round the EMA to six
// decimal places, which for anything priced below ~0.0001 collapsed the fast and slow
// EMAs onto the same number: the gap read exactly 0%, so neither the bullish nor the
// bearish branch could ever turn on and the symbol was untradeable in both directions
// with nothing in the log to say so.
public sealed class IndicatorPrecisionTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-08-26T00:00:00Z");

    private static IReadOnlyList<Candle> RisingSeries(decimal start, decimal stepPct, int count)
    {
        var candles = new List<Candle>();
        var close = start;
        for (var i = 0; i < count; i++)
        {
            candles.Add(new Candle(T.AddMinutes(15 * i), close, close, close, close, 1000m, 1));
            close *= 1m + stepPct / 100m;
        }

        return candles;
    }

    private static StrategyOptions Strategy() => new() { FastEmaPeriod = 9, SlowEmaPeriod = 21, RsiPeriod = 14 };

    [Theory]
    [InlineData("SHIB", 0.00000502)]
    [InlineData("PEPE", 0.00000330)]
    [InlineData("BONK", 0.00000271)]
    [InlineData("FLOKI", 0.00002390)]
    public void Sub_cent_symbols_produce_a_usable_ema_gap(string symbol, double price)
    {
        var candles = RisingSeries((decimal)price, stepPct: 0.5m, count: 60);

        var indicators = new IndicatorEngine().Calculate(candles, Strategy());

        Assert.NotNull(indicators.FastEma);
        Assert.NotNull(indicators.SlowEma);
        Assert.True(indicators.FastEma > indicators.SlowEma, $"{symbol}: fast EMA did not separate from slow");

        var gap = SignalScorer.CalculateEmaGapPercent(indicators.FastEma!.Value, indicators.SlowEma!.Value);
        Assert.NotNull(gap);
        Assert.True(gap > 0.2m, $"{symbol}: gap {gap} did not clear the 0.2% entry threshold on a steady 0.5%/bar rise");
    }

    // A normally priced instrument is unaffected - the same rise gives the same gap.
    [Fact]
    public void Normal_priced_symbols_are_unchanged()
    {
        var cheap = new IndicatorEngine().Calculate(RisingSeries(0.00000502m, 0.5m, 60), Strategy());
        var rich = new IndicatorEngine().Calculate(RisingSeries(73_733m, 0.5m, 60), Strategy());

        var cheapGap = SignalScorer.CalculateEmaGapPercent(cheap.FastEma!.Value, cheap.SlowEma!.Value)!.Value;
        var richGap = SignalScorer.CalculateEmaGapPercent(rich.FastEma!.Value, rich.SlowEma!.Value)!.Value;

        Assert.Equal(decimal.Round(richGap, 3), decimal.Round(cheapGap, 3));
    }
}
