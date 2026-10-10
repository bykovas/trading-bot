using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class BykoV2StrategyTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Flush_long_fires_on_forced_sell_and_confirmed_reclaim()
    {
        var oneMinute = Enumerable.Range(0, 60)
            .Select(index => Bar(Start.AddMinutes(index), 102m, 102.2m, 101.8m, 102m, 100m))
            .ToArray();
        oneMinute[^4] = Bar(Start.AddMinutes(56), 101m, 101.1m, 100m, 100.5m, 100m);
        oneMinute[^3] = Bar(Start.AddMinutes(57), 100.5m, 100.6m, 99m, 99.5m, 100m);
        oneMinute[^2] = Bar(Start.AddMinutes(58), 99.5m, 99.6m, 98m, 98.5m, 100m);
        oneMinute[^1] = Bar(Start.AddMinutes(59), 98.6m, 99.8m, 98.4m, 99.7m, 100m);

        var frame = new BykoV2MarketFrame(
            "SOL/USD",
            oneMinute,
            Array.Empty<Candle>(),
            Array.Empty<Candle>(),
            Array.Empty<Candle>(),
            99.69m,
            99.71m,
            100_000_000m,
            0m,
            new BykoV2LiquidationWindow(200_000m, 0m, true));
        var btc = frame with
        {
            Pair = "XBT/USD",
            FiveMinute = Enumerable.Range(0, 13)
                .Select(index => Bar(Start.AddMinutes(index * 5), 100m, 100.1m, 99.9m, 100m, 1m))
                .ToArray()
        };

        var verdict = BykoV2Strategy.Evaluate(BykoV2Rule.FlushLong, frame, btc, new BykoV2Options());

        Assert.True(verdict.Fired);
        Assert.False(verdict.IsUnknown);
    }

    [Fact]
    public void Liquidation_feed_gap_is_unknown_not_false()
    {
        var candles = Enumerable.Range(0, 60)
            .Select(index => Bar(Start.AddMinutes(index), 100m, 101m, 99m, 100m, 1m))
            .ToArray();
        var fiveMinute = Enumerable.Range(0, 13)
            .Select(index => Bar(Start.AddMinutes(index * 5), 100m, 101m, 99m, 100m, 1m))
            .ToArray();
        var frame = new BykoV2MarketFrame(
            "SOL/USD", candles, fiveMinute, [], [], 99m, 100m,
            1_000_000m, 0m, new BykoV2LiquidationWindow(0m, 0m, false));

        var verdict = BykoV2Strategy.Evaluate(BykoV2Rule.FlushLong, frame, frame, new BykoV2Options());

        Assert.Null(verdict.Fired);
        Assert.Contains("gap", verdict.Reason);
    }

    [Fact]
    public void Exact_window_indicators_use_the_documented_four_period_history()
    {
        var constant = Enumerable.Range(0, 205)
            .Select(index => Bar(Start.AddHours(index), 100m, 101m, 99m, 100m, 1m))
            .ToArray();
        Assert.Equal(100m, BykoV2Strategy.EmaAt(constant, 50, 0));
        Assert.Null(BykoV2Strategy.Rsi(constant.Take(56).ToArray(), 14));
        Assert.Equal(2m, BykoV2Strategy.Atr(constant.Take(57).ToArray(), 14));

        var rising = Enumerable.Range(0, 57)
            .Select(index => Bar(Start.AddMinutes(index), index, index + 1m, index - 1m, index + 1m, 1m))
            .ToArray();
        Assert.Equal(100m, BykoV2Strategy.Rsi(rising, 14));
    }

    [Fact]
    public void Candle_gap_inside_required_window_makes_breakout_unknown()
    {
        var fifteen = Enumerable.Range(0, 60)
            .Select(index => Bar(Start.AddMinutes(index * 15), 100m, 101m, 99m, 100m, 1m))
            .ToArray();
        fifteen[^3] = fifteen[^3] with { OpenTime = fifteen[^3].OpenTime.AddMinutes(1) };
        var hours = Enumerable.Range(0, 203)
            .Select(index => Bar(Start.AddHours(index), 100m, 101m, 99m, 100m, 1m))
            .ToArray();
        var frame = new BykoV2MarketFrame(
            "SOL/USD", [], [], fifteen, hours, 99m, 100m, 1_000_000m, 0m,
            new BykoV2LiquidationWindow(0m, 0m, true));

        var verdict = BykoV2Strategy.Evaluate(BykoV2Rule.BreakoutLong, frame, frame, new BykoV2Options());

        Assert.Null(verdict.Fired);
        Assert.Contains("non-contiguous", verdict.Reason);
    }

    private static Candle Bar(
        DateTimeOffset openTime,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        decimal volume) => new(openTime, open, high, low, close, volume, 0);
}
