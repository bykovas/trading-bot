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
        PositionExitOptions options,
        decimal roundTripFrictionPercent = 0m)
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
                var reason = $"ATR exit levels: ATR({options.AtrPeriod})={value:0.##########}";
                return ApplyFrictionFloor(atrLevels with { Reason = reason }, side, entryPrice, options, roundTripFrictionPercent);
            }

            var fixedFallback = FromFixedPercent(side, entryPrice, options.EffectiveFixedStopLossPercent, options.EffectiveFixedTakeProfitPercent);
            return fixedFallback with { Reason = $"ATR unavailable from closed candles ({candles.Count} candles, period {options.AtrPeriod}); fixed-percent exit levels used" };
        }

        var fixedLevels = FromFixedPercent(side, entryPrice, options.EffectiveFixedStopLossPercent, options.EffectiveFixedTakeProfitPercent);
        return fixedLevels with { Reason = "fixed-percent exit levels" };
    }

    // Friction floor: a stop tighter than K x round-trip friction loses more in
    // guaranteed costs than it saves in avoided drawdown. When the floor lifts the
    // stop distance, the take-profit distance is scaled by the same factor so the
    // configured TP/SL ratio is preserved instead of silently degrading.
    private static PositionExitLevels ApplyFrictionFloor(
        PositionExitLevels levels,
        string side,
        decimal entryPrice,
        PositionExitOptions options,
        decimal roundTripFrictionPercent)
    {
        if (options.MinStopFrictionMultiplier <= 0m
            || roundTripFrictionPercent <= 0m
            || levels.StopLossPrice is not { } stop
            || levels.TakeProfitPrice is not { } takeProfit)
        {
            return levels;
        }

        var currentStopDistance = Math.Abs(entryPrice - stop);
        var floorDistance = entryPrice * roundTripFrictionPercent * options.MinStopFrictionMultiplier / 100m;
        if (currentStopDistance >= floorDistance || currentStopDistance <= 0m)
        {
            return levels;
        }

        var scale = floorDistance / currentStopDistance;
        var takeProfitDistance = Math.Abs(takeProfit - entryPrice) * scale;
        var isShort = NormalizeSide(side) == "SHORT";
        return levels with
        {
            StopLossPrice = isShort ? entryPrice + floorDistance : entryPrice - floorDistance,
            TakeProfitPrice = isShort ? entryPrice - takeProfitDistance : entryPrice + takeProfitDistance,
            Reason = $"{levels.Reason}; stop floored at {options.MinStopFrictionMultiplier:0.##}x round-trip friction {roundTripFrictionPercent:0.###}% (TP scaled to keep R:R)"
        };
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
