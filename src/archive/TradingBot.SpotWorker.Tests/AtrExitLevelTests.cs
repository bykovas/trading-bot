using System.Reflection;
using TradingBot.SpotWorker;
using Xunit;

namespace TradingBot.SpotWorker.Tests;

public class AtrExitLevelTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-07-06T00:00:00Z");

    [Fact]
    public void Atr_uses_wilder_smoothing_over_closed_candles_only()
    {
        var candles = new[]
        {
            Candle(0, high: 10m, low: 10m, close: 10m),
            Candle(1, high: 12m, low: 9m, close: 11m),  // TR 3
            Candle(2, high: 13m, low: 10m, close: 12m), // TR 3
            Candle(3, high: 15m, low: 11m, close: 14m), // TR 4, first ATR 10 / 3
            Candle(4, high: 16m, low: 13m, close: 15m), // TR 3, Wilder ATR 3.2222222222
            Candle(5, high: 100m, low: 1m, close: 50m)  // current open candle, ignored
        };

        var atr = AtrIndicator.CalculateLatestClosedAtr(candles, period: 3);

        Assert.Equal(3.2222222222m, atr);
    }

    [Fact]
    public void Atr_exit_levels_are_calculated_for_long_entries()
    {
        var levels = PositionExitLevelCalculator.FromAtr(
            side: "LONG",
            entryPrice: 100m,
            atr: 2m,
            stopLossMultiplier: 2m,
            takeProfitMultiplier: 3m);

        Assert.Equal(PositionExitOptions.ModeAtr, levels.ExitMode);
        Assert.Equal(2m, levels.EntryAtr);
        Assert.Equal(96m, levels.StopLossPrice);
        Assert.Equal(106m, levels.TakeProfitPrice);
    }

    [Fact]
    public void Atr_exit_levels_are_mirrored_for_short_entries()
    {
        var levels = PositionExitLevelCalculator.FromAtr(
            side: "SHORT",
            entryPrice: 100m,
            atr: 2m,
            stopLossMultiplier: 2m,
            takeProfitMultiplier: 3m);

        Assert.Equal(PositionExitOptions.ModeAtr, levels.ExitMode);
        Assert.Equal(2m, levels.EntryAtr);
        Assert.Equal(104m, levels.StopLossPrice);
        Assert.Equal(94m, levels.TakeProfitPrice);
    }

    [Fact]
    public void Atr_mode_falls_back_to_fixed_levels_when_closed_history_is_insufficient()
    {
        var options = new PositionExitOptions
        {
            Mode = PositionExitOptions.ModeAtr,
            AtrPeriod = 14,
            FixedStopLossPercent = 2.5m,
            FixedTakeProfitPercent = 2.0m
        };

        var levels = PositionExitLevelCalculator.Calculate(
            "LONG",
            entryPrice: 100m,
            candles: [Candle(0, 101m, 99m, 100m), Candle(1, 102m, 100m, 101m)],
            options);

        Assert.Equal(PositionExitOptions.ModeFixedPercent, levels.ExitMode);
        Assert.False(levels.UsedAtr);
        Assert.Equal(97.5m, levels.StopLossPrice);
        Assert.Equal(102m, levels.TakeProfitPrice);
        Assert.Contains("ATR unavailable", levels.Reason);
    }

    [Fact]
    public void Atr_mode_rejects_take_profit_multiplier_below_stop_loss_multiplier()
    {
        var config = new BotConfiguration
        {
            PositionExit = new PositionExitOptions
            {
                Mode = PositionExitOptions.ModeAtr,
                StopLossAtrMultiplier = 2m,
                TakeProfitAtrMultiplier = 1.5m
            }
        };

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeNormalize(config));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("risk/reward < 1", exception.InnerException!.Message);
    }

    [Fact]
    public void Saved_long_exit_levels_trigger_on_conservative_exit_price()
    {
        var stop = PositionExitPolicy.EvaluateHeldPosition(
            desiredLong: true,
            positionAgeSeconds: 60,
            conservativeUnrealizedPnlPercent: -0.5m,
            canValuePosition: true,
            killSwitchActive: false,
            new ExecutionPolicyOptions(),
            new PositionExitOptions { StopLossPercent = 0m, TakeProfitPercent = 0m },
            exitLevels: new PositionExitLevelsSnapshot("LONG", StopLossPrice: 96m, TakeProfitPrice: 106m, ConservativeExitPrice: 95.9m));

        Assert.True(stop.ShouldSell);
        Assert.Equal(ExitReason.StopLoss, stop.ExitReason);

        var takeProfit = PositionExitPolicy.EvaluateHeldPosition(
            desiredLong: true,
            positionAgeSeconds: 60,
            conservativeUnrealizedPnlPercent: 0.5m,
            canValuePosition: true,
            killSwitchActive: false,
            new ExecutionPolicyOptions(),
            new PositionExitOptions { StopLossPercent = 0m, TakeProfitPercent = 0m },
            exitLevels: new PositionExitLevelsSnapshot("LONG", StopLossPrice: 96m, TakeProfitPrice: 106m, ConservativeExitPrice: 106.1m));

        Assert.True(takeProfit.ShouldSell);
        Assert.Equal(ExitReason.TakeProfit, takeProfit.ExitReason);
    }

    private static Candle Candle(int offset, decimal high, decimal low, decimal close) =>
        new(T0.AddMinutes(offset), close, high, low, close, 100m, 1);

    private static void InvokeNormalize(BotConfiguration config) =>
        typeof(BotConfiguration).GetMethod(
            "Normalize",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(config, null);
}
