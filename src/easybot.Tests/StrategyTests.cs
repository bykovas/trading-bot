using EasyBot.Trading;
using Skender.Stock.Indicators;
using Xunit;

namespace EasyBot.Tests;

public class StrategyTests
{
    private static List<Quote> BuildQuotes(IEnumerable<decimal> closes)
    {
        var date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var quotes = new List<Quote>();
        foreach (var close in closes)
        {
            quotes.Add(new Quote
            {
                Date = date,
                Open = close,
                High = close,
                Low = close,
                Close = close,
                Volume = 1m
            });
            date = date.AddHours(4);
        }
        return quotes;
    }

    [Fact]
    public void DetectCrossoverSignal_ReturnsLong_WhenFastCrossesAboveSlow()
    {
        // Flat, then a strong upward move on the final candle should push the fast EMA
        // above the slow EMA for the first time.
        var closes = Enumerable.Repeat(100m, 55).ToList();
        closes.Add(140m);

        var signal = Strategy.DetectCrossoverSignal(BuildQuotes(closes), emaFastPeriods: 20, emaSlowPeriods: 50);

        Assert.Equal(Signal.Long, signal);
    }

    [Fact]
    public void DetectCrossoverSignal_ReturnsShort_WhenFastCrossesBelowSlow()
    {
        var closes = Enumerable.Repeat(100m, 55).ToList();
        closes.Add(60m);

        var signal = Strategy.DetectCrossoverSignal(BuildQuotes(closes), emaFastPeriods: 20, emaSlowPeriods: 50);

        Assert.Equal(Signal.Short, signal);
    }

    [Fact]
    public void DetectCrossoverSignal_ReturnsNone_WhenNoCrossoverOccurs()
    {
        // A flat, unchanging price series never crosses.
        var closes = Enumerable.Repeat(100m, 60).ToList();

        var signal = Strategy.DetectCrossoverSignal(BuildQuotes(closes), emaFastPeriods: 20, emaSlowPeriods: 50);

        Assert.Equal(Signal.None, signal);
    }

    [Fact]
    public void DetectCrossoverSignal_ReturnsNone_WhenNotEnoughHistory()
    {
        var closes = Enumerable.Repeat(100m, 10).ToList();

        var signal = Strategy.DetectCrossoverSignal(BuildQuotes(closes), emaFastPeriods: 20, emaSlowPeriods: 50);

        Assert.Equal(Signal.None, signal);
    }
}
