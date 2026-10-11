using System.Globalization;

namespace TradingBot.FuturesWorker;

internal static class BykoV2DqlStrategy
{
    public static BykoV2Verdict Evaluate(BykoV2Rule rule, BykoV2MarketFrame pair, BykoV2MarketFrame btc, BykoV2Options options)
    {
        if (!Enum.IsDefined(rule)) throw new ArgumentOutOfRangeException(nameof(rule));
        var longSide = rule is BykoV2Rule.FlushLong or BykoV2Rule.BreakoutLong;
        var flush = rule is BykoV2Rule.FlushLong or BykoV2Rule.FlushShort;
        var values = new Dictionary<string, decimal?>();
        var conditions = new List<bool?>();
        double? Put(string key, double? value) { values[key] = Stored(value); return value; }
        void Greater(double? a, double? b) => conditions.Add(a.HasValue && b.HasValue ? a > b : null);
        void Less(double? a, double? b) => conditions.Add(a.HasValue && b.HasValue ? a < b : null);
        void Direction(double? a, double? b) { if (longSide) Greater(a, b); else Less(a, b); }
        var spread = Put("spread", Spread(pair));
        if (flush)
        {
            conditions.Add(GapGuard(pair, "1m", pair.OneMinute, 1, 57));
            conditions.Add(GapGuard(btc, "5m", btc.FiveMinute, 5, 13));
            var bars = Window(pair.OneMinute, 1, Math.Max(10, options.RsiPeriod * 4 + 1));
            var btcBars = Window(btc.FiveMinute, 5, 13);
            double? Extreme(int skip, int count, bool high) => bars is null ? null : high
                ? bars.Skip(bars.Count - skip - count).Take(count).Max(c => D(c.High))
                : bars.Skip(bars.Count - skip - count).Take(count).Min(c => D(c.Low));
            var low = Extreme(0, 4, false);
            var high = Extreme(0, 4, true);
            var move = Put("flush_move", Divide(longSide ? low : high, Extreme(4, 6, longSide)) - 1);
            var close = bars is null ? null : Number(bars[^1].Close);
            var reclaim = Put("reclaim", Divide(close, longSide ? low : high) - 1);
            double? turnover = null;
            if (bars is not null)
            {
                turnover = D(bars[^4].Volume) * D(bars[^4].Close);
                for (var i = bars.Count - 3; i < bars.Count; i++) turnover += D(bars[i].Volume) * D(bars[i].Close);
            }
            Put("four_minute_turnover_usd", turnover);
            var daily = Put("turnover_24h_usd", Number(pair.Turnover24hUsd));
            var liquidation = Put("liquidation_value_usd", pair.Liquidations is { Complete: true } l
                ? D(longSide ? l.LongValueUsd : l.ShortValueUsd) : null);
            var rsi = Put("rsi", bars is null ? null : Rsi(bars, options.RsiPeriod));
            var roc = Put("btc_roc_1h", btcBars is null ? null : Roc(btcBars, 12));
            var atrBars = Window(pair.OneMinute, 1, options.AtrPeriod * 4 + 1);
            Put("atr_ratio", Divide(atrBars is null ? null : Atr(atrBars, options.AtrPeriod), close));
            Greater(liquidation, D(options.FlushLiquidationTurnoverRatio) * turnover);
            Greater(liquidation, D(options.FlushLiquidationDailyTurnoverRatio) * daily);
            Greater(liquidation, D(options.FlushLiquidationMinimumUsd));
            if (longSide) Less(move, -D(options.FlushMoveMinimum)); else Greater(move, D(options.FlushMoveMinimum));
            Direction(close, bars is null ? null : D(bars[^1].Open));
            Direction(close, bars is null ? null : D(longSide ? bars[^2].High : bars[^2].Low));
            if (longSide) Greater(reclaim, D(options.FlushReclaimMinimum)); else Less(reclaim, -D(options.FlushReclaimMinimum));
            if (longSide) Less(rsi, 55); else Greater(rsi, 45);
            if (longSide) Greater(roc, -D(options.BtcOneHourMoveMaximumAbs)); else Less(roc, D(options.BtcOneHourMoveMaximumAbs));
            Less(spread, D(options.FlushSpreadMaximum));
        }
        else
        {
            conditions.Add(GapGuard(pair, "15m", pair.FifteenMinute, 15, 57));
            conditions.Add(GapGuard(pair, "1h", pair.OneHour, 60, 203));
            conditions.Add(GapGuard(btc, "15m", btc.FifteenMinute, 15, 1));
            conditions.Add(GapGuard(btc, "1h", btc.OneHour, 60, 203));
            var fifteen = Window(pair.FifteenMinute, 15, Math.Max(options.DonchianPeriod + 1,
                Math.Max(options.VolumeSmaPeriod, Math.Max(options.RsiPeriod, options.AtrPeriod) * 4 + 1)));
            var hours = Window(pair.OneHour, 60, Math.Max(options.FastEmaPeriod * 4, options.SlowEmaPeriod * 4 + 3));
            var btcHours = Window(btc.OneHour, 60, options.SlowEmaPeriod * 4 + 3);
            var btcFifteen = Window(btc.FifteenMinute, 15, 1);
            var btcClose = Put("btc_close", btcFifteen is null ? null : D(btcFifteen[^1].Close));
            var btcEma = Put("btc_ema50_1h", btcHours is null ? null : EmaAt(btcHours, options.SlowEmaPeriod, 0));
            var btcPrev = Put("btc_ema50_1h_prev3", btcHours is null ? null : EmaAt(btcHours, options.SlowEmaPeriod, 3));
            var fast = Put("pair_ema20_1h", hours is null ? null : EmaAt(hours, options.FastEmaPeriod, 0));
            var slow = Put("pair_ema50_1h", hours is null ? null : EmaAt(hours, options.SlowEmaPeriod, 0));
            var donchian = Put("donchian20", fifteen is null ? null : longSide
                ? fifteen.Skip(fifteen.Count - options.DonchianPeriod - 1).Take(options.DonchianPeriod).Max(c => D(c.High))
                : fifteen.Skip(fifteen.Count - options.DonchianPeriod - 1).Take(options.DonchianPeriod).Min(c => D(c.Low)));
            var close = fifteen is null ? null : Number(fifteen[^1].Close);
            double? average = null;
            if (fifteen is not null)
            {
                var start = fifteen.Count - options.VolumeSmaPeriod;
                double sum = D(fifteen[start].Volume);
                for (var i = start + 1; i < fifteen.Count; i++) sum += D(fifteen[i].Volume);
                average = sum / options.VolumeSmaPeriod;
            }
            var volume = Put("volume_ratio", Divide(fifteen is null ? null : D(fifteen[^1].Volume), average));
            var rsi = Put("rsi", fifteen is null ? null : Rsi(fifteen, options.RsiPeriod));
            var atr = Put("atr_ratio", Divide(fifteen is null ? null : Atr(fifteen, options.AtrPeriod), close));
            var funding = Put("funding_8h", Number(pair.Funding8hFraction));
            Direction(btcClose, btcEma); Direction(btcEma, btcPrev); Direction(fast, slow); Direction(close, donchian);
            Greater(volume, D(options.VolumeRatioMinimum));
            if (longSide) { Greater(rsi, 50); Less(rsi, 75); } else { Less(rsi, 50); Greater(rsi, 25); }
            Greater(atr, D(options.AtrRatioMinimum)); Less(atr, D(options.AtrRatioMaximum));
            Less(spread, D(options.BreakoutSpreadMaximum));
            if (longSide) Less(funding, D(options.Funding8hMaximumAbs)); else Greater(funding, -D(options.Funding8hMaximumAbs));
        }
        bool? fired = conditions.Contains(false) ? false : conditions.Contains(null) ? null : true;
        var name = flush ? "flush" : "breakout";
        return new(rule, fired, fired is null ? "missing or non-contiguous data, indicator or feed gap" : fired.Value
            ? $"all {name} conditions passed" : $"one or more {name} conditions failed", values);
    }

    internal static double? EmaAt(IReadOnlyList<Candle> candles, int period, int offsetFromLatest)
    {
        if (period < 1 || offsetFromLatest < 0 || candles.Count - offsetFromLatest < period * 4) return null;
        return Smooth(candles.Skip(candles.Count - offsetFromLatest - period * 4).Take(period * 4).Select(c => D(c.Close)).ToArray(), period, 2.0 / (period + 1));
    }

    internal static double? Rsi(IReadOnlyList<Candle> candles, int period)
    {
        if (period < 1 || candles.Count < period * 4 + 1) return null;
        var gains = new double[period * 4];
        var losses = new double[gains.Length];
        var start = candles.Count - gains.Length;
        for (var i = 0; i < gains.Length; i++)
        {
            var change = D(candles[start + i].Close) - D(candles[start + i - 1].Close);
            gains[i] = Math.Max(change, 0); losses[i] = Math.Max(-change, 0);
        }
        var ag = Smooth(gains, period, 1.0 / period);
        var al = Smooth(losses, period, 1.0 / period);
        return Divide(100 * ag, ag + al);
    }

    internal static double? Atr(IReadOnlyList<Candle> candles, int period)
    {
        if (period < 1 || candles.Count < period * 4 + 1) return null;
        var ranges = new double[period * 4];
        var start = candles.Count - ranges.Length;
        for (var i = 0; i < ranges.Length; i++)
        {
            var c = candles[start + i]; var prev = D(candles[start + i - 1].Close);
            ranges[i] = Math.Max(Math.Max(D(c.High) - D(c.Low), Math.Abs(D(c.High) - prev)), Math.Abs(D(c.Low) - prev));
        }
        return Smooth(ranges, period, 1.0 / period);
    }

    internal static double? Roc(IReadOnlyList<Candle> candles, int periods) => periods < 0 || candles.Count <= periods ? null
        : Divide(D(candles[^1].Close) - D(candles[^(periods + 1)].Close), D(candles[^(periods + 1)].Close));

    private static double? Smooth(double[] values, int period, double alpha)
    {
        double seed = values[0];
        for (var i = 1; i < period; i++) seed += values[i];
        var value = seed / period;
        var oneMinus = 1 - alpha;
        for (var i = period; i < values.Length; i++) value = alpha * values[i] + oneMinus * value;
        return double.IsFinite(value) ? value : null;
    }

    private static bool? GapGuard(BykoV2MarketFrame frame, string timeframe, IReadOnlyList<Candle> source, int minutes, int count)
    {
        if (frame.CandleGapFree is { } gaps && gaps.TryGetValue(timeframe, out var present)) return present;
        var bars = source.OrderBy(c => c.OpenTime).TakeLast(count).ToArray();
        for (var i = 1; i < bars.Length; i++)
            if (bars[i].OpenTime - bars[i - 1].OpenTime != TimeSpan.FromMinutes(minutes)) return false;
        // Missing history alone does not establish a gap in the source feed.
        return bars.Length < count ? null : true;
    }

    private static IReadOnlyList<Candle>? Window(IReadOnlyList<Candle> source, int minutes, int count)
    {
        if (count < 1 || source.Count < count) return null;
        var bars = source.OrderBy(c => c.OpenTime).TakeLast(count).ToArray();
        for (var i = 1; i < bars.Length; i++)
            if (bars[i].OpenTime - bars[i - 1].OpenTime != TimeSpan.FromMinutes(minutes)) return null;
        return bars;
    }

    internal static double? Spread(BykoV2MarketFrame frame)
    {
        if (frame.Bid is not { } bid || frame.Ask is not { } ask) return null;
        try
        {
            var mid = (bid + ask) / 2m;
            return mid == 0 ? null : D(decimal.Round((ask - bid) / mid, 12, MidpointRounding.ToEven));
        }
        catch (OverflowException) { return null; }
    }

    private static double D(decimal value) => double.Parse(value.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    private static double? Number(decimal? value) => value is { } v ? D(v) : null;
    private static double? Divide(double? a, double? b) => a is null || b is null || b == 0 ? null
        : double.IsFinite(a.Value / b.Value) ? a.Value / b.Value : null;
    internal static decimal? Stored(double? value) => value is { } v && double.IsFinite(v)
        && decimal.TryParse(v.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;
}
