namespace TradingBot.SpotWorker;

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
