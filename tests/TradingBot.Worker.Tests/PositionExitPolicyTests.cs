using TradingBot.Worker;
using Xunit;

namespace TradingBot.Worker.Tests;

// Unit tests for the deterministic exit/hold reconciliation logic. These map
// directly onto acceptance criteria A-E plus the priority ordering of hard exits.
public class PositionExitPolicyTests
{
    private static ExecutionPolicyOptions Execution(
        int minHoldSeconds = 900,
        bool allowImmediateExit = false) => new()
    {
        CooldownAfterBuySeconds = 900,
        CooldownAfterSellSeconds = 1800,
        MinHoldSeconds = minHoldSeconds,
        AllowImmediateExitOnSignalFlip = allowImmediateExit
    };

    private static PositionExitOptions Exit(
        decimal minProfit = 1.2m,
        decimal stopLoss = 1.5m,
        decimal takeProfit = 2.0m,
        int maxHoldMinutes = 240) => new()
    {
        MinProfitToExitOnSignalFlipPercent = minProfit,
        StopLossPercent = stopLoss,
        TakeProfitPercent = takeProfit,
        MaxHoldMinutes = maxHoldMinutes
    };

    // A. Opened 2 minutes ago, desired flips to NONE, MinHoldSeconds = 900 => HOLD.
    [Fact]
    public void MinHold_blocks_signal_flip_when_position_is_too_young()
    {
        var result = PositionExitPolicy.EvaluateHeldPosition(
            desiredLong: false,
            positionAgeSeconds: 120,
            conservativeUnrealizedPnlPercent: 0.5m,
            canValuePosition: true,
            killSwitchActive: false,
            Execution(minHoldSeconds: 900),
            Exit());

        Assert.False(result.ShouldSell);
        Assert.Equal("MIN_HOLD_BLOCK", result.HoldReasonCode);
        Assert.Contains("minimum hold active", result.Reason);
    }

    // B. Older than MinHoldSeconds, desired NONE, PnL 0.3% < 1.2% => HOLD.
    [Fact]
    public void MinProfit_blocks_signal_flip_when_pnl_below_threshold()
    {
        var result = PositionExitPolicy.EvaluateHeldPosition(
            desiredLong: false,
            positionAgeSeconds: 1000,
            conservativeUnrealizedPnlPercent: 0.3m,
            canValuePosition: true,
            killSwitchActive: false,
            Execution(minHoldSeconds: 900),
            Exit(minProfit: 1.2m));

        Assert.False(result.ShouldSell);
        Assert.Equal("MIN_PROFIT_BLOCK", result.HoldReasonCode);
        Assert.Contains("below minimum exit profit", result.Reason);
    }

    // Signal flip allowed once both guards pass (age ok + profit above threshold).
    [Fact]
    public void SignalFlip_sells_when_age_and_profit_pass()
    {
        var result = PositionExitPolicy.EvaluateHeldPosition(
            desiredLong: false,
            positionAgeSeconds: 1000,
            conservativeUnrealizedPnlPercent: 1.5m,
            canValuePosition: true,
            killSwitchActive: false,
            Execution(minHoldSeconds: 900),
            Exit(minProfit: 1.2m));

        Assert.True(result.ShouldSell);
        Assert.Equal(ExitReason.SignalFlip, result.ExitReason);
    }

    // C. Stop-loss triggers even when the position is younger than MinHoldSeconds.
    [Fact]
    public void StopLoss_sells_even_when_young()
    {
        var result = PositionExitPolicy.EvaluateHeldPosition(
            desiredLong: false,
            positionAgeSeconds: 30,
            conservativeUnrealizedPnlPercent: -1.5m,
            canValuePosition: true,
            killSwitchActive: false,
            Execution(minHoldSeconds: 900),
            Exit(stopLoss: 1.5m));

        Assert.True(result.ShouldSell);
        Assert.Equal(ExitReason.StopLoss, result.ExitReason);
        Assert.Contains("stop-loss exit", result.Reason);
    }

    // D. Take-profit triggers even when the strategy still wants LONG_MICRO.
    [Fact]
    public void TakeProfit_sells_even_when_strategy_still_long()
    {
        var result = PositionExitPolicy.EvaluateHeldPosition(
            desiredLong: true,
            positionAgeSeconds: 60,
            conservativeUnrealizedPnlPercent: 2.0m,
            canValuePosition: true,
            killSwitchActive: false,
            Execution(minHoldSeconds: 900),
            Exit(takeProfit: 2.0m));

        Assert.True(result.ShouldSell);
        Assert.Equal(ExitReason.TakeProfit, result.ExitReason);
        Assert.Contains("take-profit exit", result.Reason);
    }

    // E. Max-hold triggers once the position age reaches MaxHoldMinutes.
    [Fact]
    public void MaxHold_sells_when_age_reached()
    {
        var result = PositionExitPolicy.EvaluateHeldPosition(
            desiredLong: true,
            positionAgeSeconds: 240 * 60,
            conservativeUnrealizedPnlPercent: 0.1m,
            canValuePosition: true,
            killSwitchActive: false,
            Execution(minHoldSeconds: 900),
            Exit(maxHoldMinutes: 240));

        Assert.True(result.ShouldSell);
        Assert.Equal(ExitReason.MaxHold, result.ExitReason);
        Assert.Contains("max-hold exit", result.Reason);
    }

    // Holding when the desired position still matches (no hard exit).
    [Fact]
    public void Holds_when_desired_still_long_and_no_hard_exit()
    {
        var result = PositionExitPolicy.EvaluateHeldPosition(
            desiredLong: true,
            positionAgeSeconds: 60,
            conservativeUnrealizedPnlPercent: 0.5m,
            canValuePosition: true,
            killSwitchActive: false,
            Execution(),
            Exit());

        Assert.False(result.ShouldSell);
        Assert.Equal("DESIRED_LONG", result.HoldReasonCode);
    }

    // Kill switch is the highest priority hard exit and bypasses the soft guards.
    [Fact]
    public void KillSwitch_sells_even_when_young_and_flat()
    {
        var result = PositionExitPolicy.EvaluateHeldPosition(
            desiredLong: true,
            positionAgeSeconds: 5,
            conservativeUnrealizedPnlPercent: 0m,
            canValuePosition: true,
            killSwitchActive: true,
            Execution(minHoldSeconds: 900),
            Exit());

        Assert.True(result.ShouldSell);
        Assert.Equal(ExitReason.KillSwitch, result.ExitReason);
    }

    // AllowImmediateExitOnSignalFlip disables the minimum-hold guard.
    [Fact]
    public void Immediate_exit_flag_disables_min_hold()
    {
        var result = PositionExitPolicy.EvaluateHeldPosition(
            desiredLong: false,
            positionAgeSeconds: 10,
            conservativeUnrealizedPnlPercent: 1.5m,
            canValuePosition: true,
            killSwitchActive: false,
            Execution(minHoldSeconds: 900, allowImmediateExit: true),
            Exit());

        Assert.True(result.ShouldSell);
        Assert.Equal(ExitReason.SignalFlip, result.ExitReason);
    }
}
