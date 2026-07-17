using EasyBot.Trading;
using Xunit;

namespace EasyBot.Tests;

public class PositionSizerTests
{
    [Fact]
    public void Calculate_SizesByRiskPercentAndStopDistance_WhenBelowLeverageCap()
    {
        // equity=10000, risk=1% => riskAmount=100. atr=50, atrMultiplier=2 => stopDistance=100.
        // sizeByRisk = 100 / 100 = 1. Leverage cap allows up to (10000*10)/20000 = 5, so risk sizing wins.
        var result = PositionSizer.Calculate(
            equity: 10000m,
            riskPercent: 1m,
            atr: 50m,
            atrMultiplier: 2m,
            leverageMax: 10m,
            price: 20000m,
            side: PositionSide.Long);

        Assert.Equal(100m, result.StopDistance);
        Assert.Equal(1m, result.Size);
        Assert.Equal(19900m, result.StopPrice);
    }

    [Fact]
    public void Calculate_CapsSizeAtMaxLeverage_WhenRiskSizingWouldExceedIt()
    {
        // A very tight stop would produce a huge risk-based size; leverage cap must win.
        var result = PositionSizer.Calculate(
            equity: 10000m,
            riskPercent: 1m,
            atr: 1m,
            atrMultiplier: 1m,
            leverageMax: 2m,
            price: 20000m,
            side: PositionSide.Long);

        // Max notional = equity * leverageMax = 20000; size = 20000 / price = 1.
        Assert.Equal(1m, result.Size);
        Assert.True(result.Leverage <= 2m);
    }

    [Fact]
    public void Calculate_ShortStopIsAbovePrice()
    {
        var result = PositionSizer.Calculate(
            equity: 10000m,
            riskPercent: 1m,
            atr: 50m,
            atrMultiplier: 2m,
            leverageMax: 10m,
            price: 20000m,
            side: PositionSide.Short);

        Assert.Equal(20100m, result.StopPrice);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Calculate_Throws_WhenEquityNotPositive(decimal equity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PositionSizer.Calculate(equity, riskPercent: 1m, atr: 50m, atrMultiplier: 2m, leverageMax: 2m, price: 20000m, side: PositionSide.Long));
    }

    [Fact]
    public void ComputeTrailingStop_OnlyMovesUp_ForLongPositions()
    {
        // Favorable move (candidate higher) is applied.
        Assert.Equal(20000m, PositionSizer.ComputeTrailingStop(currentStop: 19900m, candidateStop: 20000m, side: PositionSide.Long));
        // Unfavorable move (candidate lower) is rejected; stop stays where it was.
        Assert.Equal(19900m, PositionSizer.ComputeTrailingStop(currentStop: 19900m, candidateStop: 19800m, side: PositionSide.Long));
    }

    [Fact]
    public void ComputeTrailingStop_OnlyMovesDown_ForShortPositions()
    {
        // Favorable move (candidate lower) is applied.
        Assert.Equal(20000m, PositionSizer.ComputeTrailingStop(currentStop: 20100m, candidateStop: 20000m, side: PositionSide.Short));
        // Unfavorable move (candidate higher) is rejected; stop stays where it was.
        Assert.Equal(20100m, PositionSizer.ComputeTrailingStop(currentStop: 20100m, candidateStop: 20200m, side: PositionSide.Short));
    }
}
