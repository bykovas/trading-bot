using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class BykoV2DqlStrategyTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Golden_fixed_windows_match_deployed_series_math()
    {
        // n=2: SMA seed followed by six updates, exactly as SeriesMath.Smooth.
        var bars = Bars(9, 1, i => i + 1m);
        double ema = 2.5, atr = 2;
        for (var i = 4; i <= 9; i++) ema = (2.0 / 3) * i + (1 - 2.0 / 3) * ema;
        Assert.Equal(ema, BykoV2DqlStrategy.EmaAt(bars, 2, 0));
        Assert.Equal(100.0, BykoV2DqlStrategy.Rsi(bars, 2));
        Assert.Equal(atr, BykoV2DqlStrategy.Atr(bars, 2));
        var alternating = Bars(9, 1, i => i % 2 == 0 ? 100m : 102m);
        double gain = 1, loss = 1;
        for (var i = 3; i <= 8; i++)
        {
            gain = 0.5 * (i % 2 == 1 ? 2 : 0) + 0.5 * gain;
            loss = 0.5 * (i % 2 == 0 ? 2 : 0) + 0.5 * loss;
        }
        Assert.Equal(100 * gain / (gain + loss), BykoV2DqlStrategy.Rsi(alternating, 2));
        Assert.Equal(3.0, BykoV2DqlStrategy.Atr(alternating, 2));
    }

    [Fact]
    public void Roc_uses_subtraction_before_division_at_exact_threshold()
    {
        var bars = Bars(13, 5, i => i == 12 ? 99m : 100m);
        Assert.Equal(-0.01, BykoV2DqlStrategy.Roc(bars, 12));
        Assert.NotEqual(99.0 / 100 - 1, BykoV2DqlStrategy.Roc(bars, 12));
        Assert.Null(BykoV2DqlStrategy.Roc(Bars(13, 5, _ => 0m), 12));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Flush_strict_liquidation_boundary_and_raw_double_neighbour(bool longSide)
    {
        var (pair, btc) = Flush(longSide);
        var rule = longSide ? BykoV2Rule.FlushLong : BykoV2Rule.FlushShort;
        var options = new BykoV2Options { FlushLiquidationMinimumUsd = 200_000m };
        Assert.False(BykoV2DqlStrategy.Evaluate(rule, pair, btc, options).Fired);
        options.FlushLiquidationMinimumUsd = 199_999.9999999999m;
        Assert.True(BykoV2DqlStrategy.Evaluate(rule, pair, btc, options).Fired);
        options.FlushLiquidationMinimumUsd = 200_000.0000000001m;
        Assert.False(BykoV2DqlStrategy.Evaluate(rule, pair, btc, options).Fired);
    }

    [Fact]
    public void Unknown_conditions_obey_kleene_false_wins()
    {
        var (pair, btc) = Flush(true);
        pair = pair with { Liquidations = null };
        Assert.Null(BykoV2DqlStrategy.Evaluate(BykoV2Rule.FlushLong, pair, btc, new()).Fired);
        pair = pair with { Bid = 90m, Ask = 110m };
        Assert.False(BykoV2DqlStrategy.Evaluate(BykoV2Rule.FlushLong, pair, btc, new()).Fired);
    }

    [Fact]
    public void Missing_counts_are_unknown_known_gaps_are_false_and_older_history_is_ignored()
    {
        var (pair, btc) = Flush(true);
        var expected = BykoV2DqlStrategy.Evaluate(BykoV2Rule.FlushLong, pair, btc, new());
        Assert.True(expected.Fired);
        var older = new Candle(Start.AddDays(-5), 9000m, 9001m, 8999m, 9000m, 1m, 0);
        var extra = pair with { OneMinute = new[] { older }.Concat(pair.OneMinute).ToArray() };
        Assert.Equal(expected.Values.OrderBy(v => v.Key), BykoV2DqlStrategy.Evaluate(BykoV2Rule.FlushLong, extra, btc, new()).Values.OrderBy(v => v.Key));
        Assert.Null(BykoV2DqlStrategy.Evaluate(BykoV2Rule.FlushLong, pair with { OneMinute = pair.OneMinute.Skip(1).ToArray() }, btc, new()).Fired);
        var gap = pair.OneMinute.ToArray();
        gap[10] = gap[10] with { OpenTime = gap[10].OpenTime.AddSeconds(1) };
        Assert.False(BykoV2DqlStrategy.Evaluate(BykoV2Rule.FlushLong, pair with { OneMinute = gap }, btc, new()).Fired);
        Assert.False(BykoV2DqlStrategy.Evaluate(BykoV2Rule.FlushLong, pair with { OneMinute = gap, Liquidations = null }, btc, new()).Fired);
        var btcGap = btc.FiveMinute.ToArray();
        btcGap[5] = btcGap[5] with { OpenTime = btcGap[5].OpenTime.AddSeconds(1) };
        Assert.False(BykoV2DqlStrategy.Evaluate(BykoV2Rule.FlushLong, pair with { Liquidations = null }, btc with { FiveMinute = btcGap }, new()).Fired);
        Assert.Null(BykoV2DqlStrategy.Evaluate(BykoV2Rule.FlushLong, pair, btc with { FiveMinute = btc.FiveMinute.Skip(1).ToArray() }, new()).Fired);
    }

    [Fact]
    public void Warmup_is_consumed_and_storage_uses_roundtrip_digits()
    {
        var bars = Bars(60, 1, i => 100m + i % 3);
        var changed = bars.ToArray();
        changed[3] = changed[3] with { Close = 10000m };
        Assert.NotEqual(BykoV2DqlStrategy.Rsi(bars, 14), BykoV2DqlStrategy.Rsi(changed, 14));
        changed[3] = bars[3]; changed[2] = changed[2] with { Close = 10000m };
        Assert.Equal(BykoV2DqlStrategy.Rsi(bars, 14), BykoV2DqlStrategy.Rsi(changed, 14));
        Assert.Equal(0.30000000000000004m, BykoV2DqlStrategy.Stored(0.1 + 0.2));
        Assert.Null(BykoV2DqlStrategy.Stored(double.MaxValue));
        Assert.Null(BykoV2DqlStrategy.Stored(double.NaN));
    }

    [Fact]
    public void Spread_rounds_decimal_fraction_half_even_to_twelve_places()
    {
        var (pair, _) = Flush(true);
        Assert.Equal(0.000000000002, BykoV2DqlStrategy.Spread(pair with { Bid = 0.99999999999875m, Ask = 1.00000000000125m }));
        Assert.Equal(0.000000000004, BykoV2DqlStrategy.Spread(pair with { Bid = 0.99999999999825m, Ask = 1.00000000000175m }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Breakout_strict_funding_boundary_and_required_windows(bool longSide)
    {
        var fifteen = Bars(57, 15, i => 100m + i * 0.1m + (i % 2 == 0 ? 0m : 1m));
        fifteen[^1] = fifteen[^1] with { Close = 108m, High = 109m, Volume = 1000m };
        var hours = Bars(203, 60, i => 100m + i);
        if (!longSide)
        {
            fifteen = fifteen.Select(c => c with { Open = 400m - c.Open, High = 400m - c.Low, Low = 400m - c.High, Close = 400m - c.Close }).ToArray();
            hours = hours.Select(c => c with { Open = 400m - c.Open, High = 400m - c.Low, Low = 400m - c.High, Close = 400m - c.Close }).ToArray();
        }
        var pair = new BykoV2MarketFrame("SOL/USD", [], [], fifteen, hours, 100m, 100.01m, null, 0m, null);
        var btc = pair with { FifteenMinute = Bars(1, 15, _ => longSide ? 400m : 50m) };
        var rule = longSide ? BykoV2Rule.BreakoutLong : BykoV2Rule.BreakoutShort;
        var options = new BykoV2Options { AtrRatioMinimum = 0m, AtrRatioMaximum = 1m };
        Assert.True(BykoV2DqlStrategy.Evaluate(rule, pair, btc, options).Fired);
        var boundary = longSide ? options.Funding8hMaximumAbs : -options.Funding8hMaximumAbs;
        Assert.False(BykoV2DqlStrategy.Evaluate(rule, pair with { Funding8hFraction = boundary }, btc, options).Fired);
        var inside = boundary + (longSide ? -0.000000000000000001m : 0.000000000000000001m);
        Assert.True(BykoV2DqlStrategy.Evaluate(rule, pair with { Funding8hFraction = inside }, btc, options).Fired);
        Assert.Null(BykoV2DqlStrategy.Evaluate(rule, pair with { FifteenMinute = fifteen.Skip(1).ToArray() }, btc, options).Fired);
        Assert.Null(BykoV2DqlStrategy.Evaluate(rule, pair with { OneHour = hours.Skip(1).ToArray() }, btc, options).Fired);
        Assert.Null(BykoV2DqlStrategy.Evaluate(rule, pair, btc with { OneHour = hours.Skip(1).ToArray() }, options).Fired);
        Assert.Null(BykoV2DqlStrategy.Evaluate(rule, pair, btc with { FifteenMinute = [] }, options).Fired);
        var gap = fifteen.ToArray(); gap[5] = gap[5] with { OpenTime = gap[5].OpenTime.AddSeconds(1) };
        Assert.False(BykoV2DqlStrategy.Evaluate(rule, pair with { FifteenMinute = gap }, btc, options).Fired);
        var hourGap = hours.ToArray(); hourGap[5] = hourGap[5] with { OpenTime = hourGap[5].OpenTime.AddSeconds(1) };
        Assert.False(BykoV2DqlStrategy.Evaluate(rule, pair with { OneHour = hourGap, Funding8hFraction = null }, btc, options).Fired);
        Assert.False(BykoV2DqlStrategy.Evaluate(rule, pair with { Funding8hFraction = null }, btc with { OneHour = hourGap }, options).Fired);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Flush_liquidation_multiplication_threshold_is_strict(bool longSide, bool dailyThreshold)
    {
        var (pair, btc) = Flush(longSide);
        var rule = longSide ? BykoV2Rule.FlushLong : BykoV2Rule.FlushShort;
        var options = new BykoV2Options
        {
            FlushLiquidationMinimumUsd = 0m,
            FlushLiquidationTurnoverRatio = dailyThreshold ? 0m : 0.25m,
            FlushLiquidationDailyTurnoverRatio = dailyThreshold ? 0.001m : 0m
        };
        pair = pair with { Turnover24hUsd = 264535359.13770002m };
        double turnover = 0;
        foreach (var c in pair.OneMinute.TakeLast(4)) turnover += (double)c.Volume * (double)c.Close;
        var threshold = dailyThreshold ? 264535.35913770006 : 0.25 * turnover;
        if (dailyThreshold)
            Assert.True(threshold / 264535359.13770002 > 0.001);
        BykoV2MarketFrame WithLiquidation(double value)
        {
            var stored = BykoV2DqlStrategy.Stored(value)!.Value;
            return pair with { Liquidations = new(stored, stored, true) };
        }
        Assert.False(BykoV2DqlStrategy.Evaluate(rule, WithLiquidation(threshold), btc, options).Fired);
        Assert.True(BykoV2DqlStrategy.Evaluate(rule, WithLiquidation(Math.BitIncrement(threshold)), btc, options).Fired);
        Assert.False(BykoV2DqlStrategy.Evaluate(rule, WithLiquidation(Math.BitDecrement(threshold)), btc, options).Fired);
    }

    private static (BykoV2MarketFrame Pair, BykoV2MarketFrame Btc) Flush(bool longSide)
    {
        var bars = Bars(57, 1, _ => 102m);
        bars[^4] = new(Start.AddMinutes(53), 101m, 101.1m, 100m, 100.5m, 100m, 0);
        bars[^3] = new(Start.AddMinutes(54), 100.5m, 100.6m, 99m, 99.5m, 100m, 0);
        bars[^2] = new(Start.AddMinutes(55), 99.5m, 99.6m, 98m, 98.5m, 100m, 0);
        bars[^1] = new(Start.AddMinutes(56), 98.6m, 99.8m, 98.4m, 99.7m, 100m, 0);
        if (!longSide) bars = bars.Select(c => c with { Open = 204m - c.Open, High = 204m - c.Low, Low = 204m - c.High, Close = 204m - c.Close }).ToArray();
        var pair = new BykoV2MarketFrame("SOL/USD", bars, [], [], [], 100m, 100.01m, 100_000_000m, 0m, new(200_000m, 200_000m, true));
        return (pair, pair with { FiveMinute = Bars(13, 5, _ => 100m) });
    }

    private static Candle[] Bars(int count, int minutes, Func<int, decimal> close) => Enumerable.Range(0, count)
        .Select(i => new Candle(Start.AddMinutes(i * minutes), close(i), close(i) + 1, close(i) - 1, close(i), 100m, 0)).ToArray();
}
