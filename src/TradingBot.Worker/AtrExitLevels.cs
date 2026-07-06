namespace TradingBot.Worker;

internal static class AtrIndicator
{
    public static decimal? CalculateLatestClosedAtr(IReadOnlyList<Candle> candles, int period)
    {
        if (period <= 0 || candles.Count < period + 2)
        {
            return null;
        }

        // The newest candle can still be forming in live trading, so ATR uses only
        // completed candles. True Range needs a previous close, hence period + 1
        // closed candles are required for the first Wilder ATR.
        var closed = candles.Take(candles.Count - 1).ToList();
        if (closed.Count < period + 1)
        {
            return null;
        }

        var trueRanges = new List<decimal>(closed.Count - 1);
        for (var i = 1; i < closed.Count; i++)
        {
            var candle = closed[i];
            var previousClose = closed[i - 1].Close;
            var highLow = candle.High - candle.Low;
            var highPreviousClose = Math.Abs(candle.High - previousClose);
            var lowPreviousClose = Math.Abs(candle.Low - previousClose);
            trueRanges.Add(Math.Max(highLow, Math.Max(highPreviousClose, lowPreviousClose)));
        }

        if (trueRanges.Count < period)
        {
            return null;
        }

        var atr = trueRanges.Take(period).Average();
        for (var i = period; i < trueRanges.Count; i++)
        {
            atr = ((atr * (period - 1)) + trueRanges[i]) / period;
        }

        return decimal.Round(atr, 10);
    }
}

internal sealed record PositionExitLevels(
    string ExitMode,
    decimal? EntryAtr,
    decimal? StopLossPrice,
    decimal? TakeProfitPrice,
    bool UsedAtr,
    string Reason);

internal static class PositionExitLevelCalculator
{
    public static PositionExitLevels Calculate(
        string side,
        decimal entryPrice,
        IReadOnlyList<Candle> candles,
        PositionExitOptions options)
    {
        if (entryPrice <= 0m)
        {
            return new PositionExitLevels(PositionExitOptions.ModeFixedPercent, null, null, null, false, "exit levels unavailable: entry price is zero");
        }

        if (options.IsAtrMode)
        {
            var atr = AtrIndicator.CalculateLatestClosedAtr(candles, options.AtrPeriod);
            if (atr is { } value && value > 0m)
            {
                var atrLevels = FromAtr(side, entryPrice, value, options.StopLossAtrMultiplier, options.TakeProfitAtrMultiplier);
                return atrLevels with { Reason = $"ATR exit levels: ATR({options.AtrPeriod})={value:0.##########}" };
            }

            var fixedFallback = FromFixedPercent(side, entryPrice, options.EffectiveFixedStopLossPercent, options.EffectiveFixedTakeProfitPercent);
            return fixedFallback with { Reason = $"ATR unavailable from closed candles ({candles.Count} candles, period {options.AtrPeriod}); fixed-percent exit levels used" };
        }

        var fixedLevels = FromFixedPercent(side, entryPrice, options.EffectiveFixedStopLossPercent, options.EffectiveFixedTakeProfitPercent);
        return fixedLevels with { Reason = "fixed-percent exit levels" };
    }

    public static PositionExitLevels FromAtr(
        string side,
        decimal entryPrice,
        decimal atr,
        decimal stopLossMultiplier,
        decimal takeProfitMultiplier)
    {
        var normalizedSide = NormalizeSide(side);
        var stopDistance = atr * stopLossMultiplier;
        var takeProfitDistance = atr * takeProfitMultiplier;
        return normalizedSide == "SHORT"
            ? new PositionExitLevels(PositionExitOptions.ModeAtr, atr, entryPrice + stopDistance, entryPrice - takeProfitDistance, true, string.Empty)
            : new PositionExitLevels(PositionExitOptions.ModeAtr, atr, entryPrice - stopDistance, entryPrice + takeProfitDistance, true, string.Empty);
    }

    public static PositionExitLevels FromFixedPercent(
        string side,
        decimal entryPrice,
        decimal stopLossPercent,
        decimal takeProfitPercent)
    {
        var normalizedSide = NormalizeSide(side);
        var stopDistance = entryPrice * stopLossPercent / 100m;
        var takeProfitDistance = entryPrice * takeProfitPercent / 100m;
        return normalizedSide == "SHORT"
            ? new PositionExitLevels(PositionExitOptions.ModeFixedPercent, null, entryPrice + stopDistance, entryPrice - takeProfitDistance, false, string.Empty)
            : new PositionExitLevels(PositionExitOptions.ModeFixedPercent, null, entryPrice - stopDistance, entryPrice + takeProfitDistance, false, string.Empty);
    }

    private static string NormalizeSide(string side) =>
        side.Equals("SHORT", StringComparison.OrdinalIgnoreCase) ? "SHORT" : "LONG";
}
