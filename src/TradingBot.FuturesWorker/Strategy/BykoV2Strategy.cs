namespace TradingBot.FuturesWorker;

internal enum BykoV2Rule
{
    FlushLong,
    FlushShort,
    BreakoutLong,
    BreakoutShort
}

internal sealed record BykoV2LiquidationWindow(
    decimal LongValueUsd,
    decimal ShortValueUsd,
    bool Complete);

internal sealed record BykoV2MarketFrame(
    string Pair,
    IReadOnlyList<Candle> OneMinute,
    IReadOnlyList<Candle> FiveMinute,
    IReadOnlyList<Candle> FifteenMinute,
    IReadOnlyList<Candle> OneHour,
    decimal? Bid,
    decimal? Ask,
    decimal? Turnover24hUsd,
    decimal? Funding8hFraction,
    BykoV2LiquidationWindow? Liquidations);

internal sealed record BykoV2Verdict(
    BykoV2Rule Rule,
    bool? Fired,
    string Reason,
    IReadOnlyDictionary<string, decimal?> Values)
{
    public bool IsUnknown => Fired is null;
}

internal static class BykoV2Strategy
{
    public static BykoV2Verdict Evaluate(
        BykoV2Rule rule,
        BykoV2MarketFrame pair,
        BykoV2MarketFrame btc,
        BykoV2Options options) => rule switch
        {
            BykoV2Rule.FlushLong => EvaluateFlush(pair, btc, options, longSide: true),
            BykoV2Rule.FlushShort => EvaluateFlush(pair, btc, options, longSide: false),
            BykoV2Rule.BreakoutLong => EvaluateBreakout(pair, btc, options, longSide: true),
            BykoV2Rule.BreakoutShort => EvaluateBreakout(pair, btc, options, longSide: false),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, null)
        };

    private static BykoV2Verdict EvaluateFlush(
        BykoV2MarketFrame pair,
        BykoV2MarketFrame btc,
        BykoV2Options options,
        bool longSide)
    {
        var rule = longSide ? BykoV2Rule.FlushLong : BykoV2Rule.FlushShort;
        var requiredOneMinute = Math.Max(10, options.RsiPeriod * 4 + 1);
        if (!TryClosedSeries(pair.OneMinute, 1, requiredOneMinute, out var oneMinute)
            || !TryClosedSeries(btc.FiveMinute, 5, 13, out var btcFiveMinute))
        {
            return Unknown(rule, "missing or non-contiguous closed candles");
        }

        if (pair.Liquidations is not { Complete: true } liquidations)
        {
            return Unknown(rule, "liquidation window is missing or crossed a feed gap");
        }

        if (pair.Turnover24hUsd is not > 0m || !TrySpread(pair, out var spread))
        {
            return Unknown(rule, "turnover or best bid/ask unavailable");
        }

        var rsi = Rsi(oneMinute, options.RsiPeriod);
        var btcRoc = Roc(btcFiveMinute, 12);
        if (rsi is null || btcRoc is null)
        {
            return Unknown(rule, "RSI or BTC one-hour ROC unavailable");
        }

        var last = oneMinute[^1];
        var previous = oneMinute[^2];
        var latestFour = oneMinute.TakeLast(4).ToArray();
        var preFlush = oneMinute.Skip(oneMinute.Count - 10).Take(6).ToArray();
        var flushLow = latestFour.Min(candle => candle.Low);
        var flushHigh = latestFour.Max(candle => candle.High);
        var preFlushHigh = preFlush.Max(candle => candle.High);
        var preFlushLow = preFlush.Min(candle => candle.Low);
        var fourMinuteTurnover = latestFour.Sum(candle => candle.Volume * candle.Close);
        var liquidationValue = longSide ? liquidations.LongValueUsd : liquidations.ShortValueUsd;
        var move = longSide
            ? flushLow / preFlushHigh - 1m
            : flushHigh / preFlushLow - 1m;
        var reclaim = longSide
            ? last.Close / flushLow - 1m
            : last.Close / flushHigh - 1m;

        var fired = liquidationValue > options.FlushLiquidationTurnoverRatio * fourMinuteTurnover
            && liquidationValue > options.FlushLiquidationDailyTurnoverRatio * pair.Turnover24hUsd.Value
            && liquidationValue > options.FlushLiquidationMinimumUsd
            && (longSide ? move < -options.FlushMoveMinimum : move > options.FlushMoveMinimum)
            && (longSide ? last.Close > last.Open : last.Close < last.Open)
            && (longSide ? last.Close > previous.High : last.Close < previous.Low)
            && (longSide ? reclaim > options.FlushReclaimMinimum : reclaim < -options.FlushReclaimMinimum)
            && (longSide ? rsi.Value < 55m : rsi.Value > 45m)
            && (longSide ? btcRoc.Value > -options.BtcOneHourMoveMaximumAbs : btcRoc.Value < options.BtcOneHourMoveMaximumAbs)
            && spread < options.FlushSpreadMaximum;

        return new BykoV2Verdict(
            rule,
            fired,
            fired ? "all flush conditions passed" : "one or more flush conditions failed",
            new Dictionary<string, decimal?>
            {
                ["liquidation_value_usd"] = liquidationValue,
                ["four_minute_turnover_usd"] = fourMinuteTurnover,
                ["turnover_24h_usd"] = pair.Turnover24hUsd,
                ["flush_move"] = move,
                ["reclaim"] = reclaim,
                ["rsi"] = rsi,
                ["btc_roc_1h"] = btcRoc,
                ["spread"] = spread,
                ["atr_ratio"] = last.Close > 0m ? Atr(oneMinute, options.AtrPeriod) / last.Close : null
            });
    }

    private static BykoV2Verdict EvaluateBreakout(
        BykoV2MarketFrame pair,
        BykoV2MarketFrame btc,
        BykoV2Options options,
        bool longSide)
    {
        var rule = longSide ? BykoV2Rule.BreakoutLong : BykoV2Rule.BreakoutShort;
        var requiredFifteen = Math.Max(
            options.DonchianPeriod + 1,
            Math.Max(options.VolumeSmaPeriod, Math.Max(options.RsiPeriod * 4 + 1, options.AtrPeriod * 4 + 1)));
        var requiredHours = options.SlowEmaPeriod * 4 + 3;
        if (!TryClosedSeries(pair.FifteenMinute, 15, requiredFifteen, out var fifteen)
            || !TryClosedSeries(pair.OneHour, 60, requiredHours, out var hours)
            || !TryClosedSeries(btc.FifteenMinute, 15, 1, out var btcFifteen)
            || !TryClosedSeries(btc.OneHour, 60, requiredHours, out var btcHours))
        {
            return Unknown(rule, "missing or non-contiguous closed candles");
        }

        if (!TrySpread(pair, out var spread) || pair.Funding8hFraction is null)
        {
            return Unknown(rule, "spread or funding unavailable");
        }

        var pairFastEma = EmaAt(hours, options.FastEmaPeriod, 0);
        var pairSlowEma = EmaAt(hours, options.SlowEmaPeriod, 0);
        var btcEma = EmaAt(btcHours, options.SlowEmaPeriod, 0);
        var btcEmaPrevious = EmaAt(btcHours, options.SlowEmaPeriod, 3);
        var rsi = Rsi(fifteen, options.RsiPeriod);
        var atr = Atr(fifteen, options.AtrPeriod);
        if (pairFastEma is null || pairSlowEma is null || btcEma is null
            || btcEmaPrevious is null || rsi is null || atr is null)
        {
            return Unknown(rule, "one or more exact-window indicators unavailable");
        }

        var last = fifteen[^1];
        var previousDonchian = fifteen
            .Skip(fifteen.Count - options.DonchianPeriod - 1)
            .Take(options.DonchianPeriod)
            .ToArray();
        var donchian = longSide
            ? previousDonchian.Max(candle => candle.High)
            : previousDonchian.Min(candle => candle.Low);
        var averageVolume = fifteen.TakeLast(options.VolumeSmaPeriod).Average(candle => candle.Volume);
        var volumeRatio = averageVolume <= 0m ? (decimal?)null : last.Volume / averageVolume;
        var atrRatio = last.Close <= 0m ? (decimal?)null : atr.Value / last.Close;
        if (volumeRatio is null || atrRatio is null)
        {
            return Unknown(rule, "volume ratio or ATR ratio unavailable");
        }

        var btcClose = btcFifteen[^1].Close;
        var fired = (longSide ? btcClose > btcEma : btcClose < btcEma)
            && (longSide ? btcEma > btcEmaPrevious : btcEma < btcEmaPrevious)
            && (longSide ? pairFastEma > pairSlowEma : pairFastEma < pairSlowEma)
            && (longSide ? last.Close > donchian : last.Close < donchian)
            && volumeRatio.Value > options.VolumeRatioMinimum
            && (longSide ? rsi.Value > 50m && rsi.Value < 75m : rsi.Value < 50m && rsi.Value > 25m)
            && atrRatio.Value > options.AtrRatioMinimum
            && atrRatio.Value < options.AtrRatioMaximum
            && spread < options.BreakoutSpreadMaximum
            && (longSide
                ? pair.Funding8hFraction.Value < options.Funding8hMaximumAbs
                : pair.Funding8hFraction.Value > -options.Funding8hMaximumAbs);

        return new BykoV2Verdict(
            rule,
            fired,
            fired ? "all breakout conditions passed" : "one or more breakout conditions failed",
            new Dictionary<string, decimal?>
            {
                ["btc_close"] = btcClose,
                ["btc_ema50_1h"] = btcEma,
                ["btc_ema50_1h_prev3"] = btcEmaPrevious,
                ["pair_ema20_1h"] = pairFastEma,
                ["pair_ema50_1h"] = pairSlowEma,
                ["donchian20"] = donchian,
                ["volume_ratio"] = volumeRatio,
                ["rsi"] = rsi,
                ["atr_ratio"] = atrRatio,
                ["spread"] = spread,
                ["funding_8h"] = pair.Funding8hFraction
            });
    }

    internal static decimal? EmaAt(IReadOnlyList<Candle> candles, int period, int offsetFromLatest)
    {
        var endExclusive = candles.Count - offsetFromLatest;
        var window = period * 4;
        if (period < 1 || endExclusive < window)
        {
            return null;
        }

        var start = endExclusive - window;
        var value = candles.Skip(start).Take(period).Average(candle => candle.Close);
        var alpha = 2m / (period + 1m);
        for (var index = start + period; index < endExclusive; index++)
        {
            value = alpha * candles[index].Close + (1m - alpha) * value;
        }

        return value;
    }

    internal static decimal? Rsi(IReadOnlyList<Candle> candles, int period)
    {
        var changes = period * 4;
        if (period < 1 || candles.Count < changes + 1)
        {
            return null;
        }

        var start = candles.Count - changes - 1;
        var gains = new decimal[changes];
        var losses = new decimal[changes];
        for (var index = 0; index < changes; index++)
        {
            var change = candles[start + index + 1].Close - candles[start + index].Close;
            gains[index] = Math.Max(change, 0m);
            losses[index] = Math.Max(-change, 0m);
        }

        var averageGain = Rma(gains, period);
        var averageLoss = Rma(losses, period);
        return averageGain + averageLoss == 0m
            ? null
            : 100m * averageGain / (averageGain + averageLoss);
    }

    internal static decimal? Atr(IReadOnlyList<Candle> candles, int period)
    {
        var count = period * 4;
        if (period < 1 || candles.Count < count + 1)
        {
            return null;
        }

        var start = candles.Count - count;
        var ranges = new decimal[count];
        for (var index = 0; index < count; index++)
        {
            var candle = candles[start + index];
            var previousClose = candles[start + index - 1].Close;
            ranges[index] = Math.Max(
                candle.High - candle.Low,
                Math.Max(Math.Abs(candle.High - previousClose), Math.Abs(candle.Low - previousClose)));
        }

        return Rma(ranges, period);
    }

    private static decimal Rma(IReadOnlyList<decimal> values, int period)
    {
        var value = values.Take(period).Average();
        var alpha = 1m / period;
        for (var index = period; index < values.Count; index++)
        {
            value = alpha * values[index] + (1m - alpha) * value;
        }

        return value;
    }

    private static decimal? Roc(IReadOnlyList<Candle> candles, int periods)
    {
        if (candles.Count < periods + 1)
        {
            return null;
        }

        var previous = candles[^(periods + 1)].Close;
        return previous == 0m ? null : candles[^1].Close / previous - 1m;
    }

    private static bool TrySpread(BykoV2MarketFrame frame, out decimal spread)
    {
        spread = 0m;
        if (frame.Bid is not > 0m || frame.Ask is not > 0m || frame.Ask < frame.Bid)
        {
            return false;
        }

        var mid = (frame.Bid.Value + frame.Ask.Value) / 2m;
        spread = mid <= 0m ? 0m : (frame.Ask.Value - frame.Bid.Value) / mid;
        return mid > 0m;
    }

    private static bool TryClosedSeries(
        IReadOnlyList<Candle> source,
        int timeframeMinutes,
        int minimumCount,
        out IReadOnlyList<Candle> ordered)
    {
        ordered = source.OrderBy(candle => candle.OpenTime).ToArray();
        if (ordered.Count < minimumCount)
        {
            return false;
        }

        var expected = TimeSpan.FromMinutes(timeframeMinutes);
        for (var index = ordered.Count - minimumCount + 1; index < ordered.Count; index++)
        {
            if (ordered[index].OpenTime - ordered[index - 1].OpenTime != expected)
            {
                return false;
            }
        }

        return true;
    }

    private static BykoV2Verdict Unknown(BykoV2Rule rule, string reason) =>
        new(rule, null, reason, new Dictionary<string, decimal?>());
}
