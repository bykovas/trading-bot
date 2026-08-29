namespace EasyBot.Trading;

public enum PositionSide
{
    Long,
    Short
}

public readonly record struct PositionSizeResult(
    decimal Size,
    decimal StopDistance,
    decimal StopPrice,
    decimal Leverage);

/// <summary>
/// Pure sizing/risk math. Stop distance is atrMultiplier * ATR; size is capped so notional
/// exposure never exceeds equity * leverageMax.
/// </summary>
public static class PositionSizer
{
    public static PositionSizeResult Calculate(
        decimal equity,
        decimal riskPercent,
        decimal atr,
        decimal atrMultiplier,
        decimal leverageMax,
        decimal price,
        PositionSide side)
    {
        if (equity <= 0) throw new ArgumentOutOfRangeException(nameof(equity), "Equity must be positive.");
        if (riskPercent <= 0) throw new ArgumentOutOfRangeException(nameof(riskPercent), "Risk percent must be positive.");
        if (atr <= 0) throw new ArgumentOutOfRangeException(nameof(atr), "ATR must be positive.");
        if (atrMultiplier <= 0) throw new ArgumentOutOfRangeException(nameof(atrMultiplier), "ATR multiplier must be positive.");
        if (leverageMax <= 0) throw new ArgumentOutOfRangeException(nameof(leverageMax), "Leverage max must be positive.");
        if (price <= 0) throw new ArgumentOutOfRangeException(nameof(price), "Price must be positive.");

        var stopDistance = atrMultiplier * atr;
        var riskAmount = equity * (riskPercent / 100m);
        var sizeByRisk = riskAmount / stopDistance;

        var sizeByLeverageCap = (equity * leverageMax) / price;
        var size = Math.Min(sizeByRisk, sizeByLeverageCap);

        var stopPrice = side == PositionSide.Long
            ? price - stopDistance
            : price + stopDistance;

        var notional = size * price;
        var leverage = notional / equity;

        return new PositionSizeResult(size, stopDistance, stopPrice, leverage);
    }

    /// <summary>
    /// Recomputes the trailing stop candidate and only allows it to move in the favorable
    /// direction: up for longs, down for shorts. Returns the unchanged current stop otherwise.
    /// </summary>
    public static decimal ComputeTrailingStop(decimal currentStop, decimal candidateStop, PositionSide side)
    {
        return side == PositionSide.Long
            ? Math.Max(currentStop, candidateStop)
            : Math.Min(currentStop, candidateStop);
    }
}
