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
        int maxHoldMinutes = 240,
        decimal trailingActivation = 0m,
        decimal trailingDistance = 0m) => new()
    {
        MinProfitToExitOnSignalFlipPercent = minProfit,
        StopLossPercent = stopLoss,
        TakeProfitPercent = takeProfit,
        MaxHoldMinutes = maxHoldMinutes,
        TrailingActivationPercent = trailingActivation,
        TrailingDistancePercent = trailingDistance
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

    // Regression coverage for the reported execution-priority bug: a stop-loss (or
    // any hard/forced exit) must never be suppressed by the min-profit filter. The
    // four scenarios below mirror the acceptance criteria in the bug report.

    // Scenario 1: PnL = -2%, StopLoss = 1.5%, Signal = HOLD (strategy still long) => SELL (STOP_LOSS).
    [Fact]
    public void Scenario1_StopLoss_sells_even_when_strategy_wants_hold()
    {
        var result = PositionExitPolicy.EvaluateHeldPosition(
            desiredLong: true,
            positionAgeSeconds: 60,
            conservativeUnrealizedPnlPercent: -2m,
            canValuePosition: true,
            killSwitchActive: false,
            Execution(),
            Exit(stopLoss: 1.5m));

        Assert.True(result.ShouldSell);
        Assert.Equal(ExitReason.StopLoss, result.ExitReason);
    }

    // Scenario 2: PnL = -2%, Signal = EXIT (desired NONE) => SELL (STOP_LOSS).
    // The position is young and below the min-profit threshold, proving stop-loss
    // outranks both MIN_HOLD_BLOCK and MIN_PROFIT_BLOCK.
    [Fact]
    public void Scenario2_StopLoss_sells_on_signal_flip_below_min_profit()
    {
        var result = PositionExitPolicy.EvaluateHeldPosition(
            desiredLong: false,
            positionAgeSeconds: 30,
            conservativeUnrealizedPnlPercent: -2m,
            canValuePosition: true,
            killSwitchActive: false,
            Execution(minHoldSeconds: 900),
            Exit(stopLoss: 1.5m, minProfit: 1m));

        Assert.True(result.ShouldSell);
        Assert.Equal(ExitReason.StopLoss, result.ExitReason);
        Assert.Null(result.HoldReasonCode);
    }

    // Scenario 3: PnL = +0.4%, Signal = EXIT, MinProfit = 1% => HOLD (MIN_PROFIT_BLOCK).
    [Fact]
    public void Scenario3_MinProfit_blocks_signal_flip_when_profit_too_small()
    {
        var result = PositionExitPolicy.EvaluateHeldPosition(
            desiredLong: false,
            positionAgeSeconds: 1000,
            conservativeUnrealizedPnlPercent: 0.4m,
            canValuePosition: true,
            killSwitchActive: false,
            Execution(minHoldSeconds: 900),
            Exit(minProfit: 1m));

        Assert.False(result.ShouldSell);
        Assert.Equal("MIN_PROFIT_BLOCK", result.HoldReasonCode);
    }

    // Scenario 4: PnL = +1.5%, Signal = EXIT, MinProfit = 1% => SELL (signal flip).
    [Fact]
    public void Scenario4_SignalFlip_sells_when_profit_above_min_profit()
    {
        var result = PositionExitPolicy.EvaluateHeldPosition(
            desiredLong: false,
            positionAgeSeconds: 1000,
            conservativeUnrealizedPnlPercent: 1.5m,
            canValuePosition: true,
            killSwitchActive: false,
            Execution(minHoldSeconds: 900),
            Exit(minProfit: 1m, takeProfit: 2m));

        Assert.True(result.ShouldSell);
        Assert.Equal(ExitReason.SignalFlip, result.ExitReason);
    }

    // The originally reported example verbatim: PnL = -2.2%, StopLoss = 1.5%,
    // Signal = EXIT => SELL (STOP_LOSS), never HOLD / MIN_PROFIT_BLOCK.
    [Fact]
    public void ReportedExample_losing_position_on_signal_flip_hits_stop_loss()
    {
        var result = PositionExitPolicy.EvaluateHeldPosition(
            desiredLong: false,
            positionAgeSeconds: 45,
            conservativeUnrealizedPnlPercent: -2.2m,
            canValuePosition: true,
            killSwitchActive: false,
            Execution(minHoldSeconds: 900),
            Exit(stopLoss: 1.5m, minProfit: 1.2m));

        Assert.True(result.ShouldSell);
        Assert.Equal(ExitReason.StopLoss, result.ExitReason);
        Assert.Equal("SELL_STOP_LOSS", PositionExitPolicy.ExitReasonCode(result.ExitReason!.Value));
    }

    // Trailing stop fires once the peak reaches activation and PnL falls the full
    // trailing distance below that peak (0.9 <= 2.0 - 1.0). Still LONG so it proves
    // the trailing stop is a forced exit that bypasses the strategy tier.
    [Fact]
    public void TrailingStop_fires_after_peak_reaches_activation_and_pnl_retraces()
    {
        var result = PositionExitPolicy.EvaluateHeldPosition(
            desiredLong: true,
            positionAgeSeconds: 60,
            conservativeUnrealizedPnlPercent: 0.9m,
            canValuePosition: true,
            killSwitchActive: false,
            Execution(),
            Exit(takeProfit: 3.0m, trailingActivation: 1.5m, trailingDistance: 1.0m),
            peakPnlPercent: 2.0m);

        Assert.True(result.ShouldSell);
        Assert.Equal(ExitReason.TrailingStop, result.ExitReason);
        Assert.Equal("SELL_TRAILING_STOP", PositionExitPolicy.ExitReasonCode(result.ExitReason!.Value));
    }

    // Peak never reached activation (1.2% < 1.5%), so a retrace does NOT trail-fire.
    [Fact]
    public void TrailingStop_does_not_fire_when_peak_below_activation()
    {
        var result = PositionExitPolicy.EvaluateHeldPosition(
            desiredLong: true,
            positionAgeSeconds: 60,
            conservativeUnrealizedPnlPercent: 0.1m,
            canValuePosition: true,
            killSwitchActive: false,
            Execution(),
            Exit(takeProfit: 3.0m, trailingActivation: 1.5m, trailingDistance: 1.0m),
            peakPnlPercent: 1.2m);

        Assert.False(result.ShouldSell);
        Assert.Equal("DESIRED_LONG", result.HoldReasonCode);
    }

    // Stop-loss (TIER 1) outranks the trailing stop (TIER 2): even with an armed peak
    // and a retrace, a stop-loss loss reports StopLoss, not TrailingStop.
    [Fact]
    public void StopLoss_outranks_trailing_stop()
    {
        var result = PositionExitPolicy.EvaluateHeldPosition(
            desiredLong: true,
            positionAgeSeconds: 60,
            conservativeUnrealizedPnlPercent: -2m,
            canValuePosition: true,
            killSwitchActive: false,
            Execution(),
            Exit(stopLoss: 1.5m, takeProfit: 3.0m, trailingActivation: 1.5m, trailingDistance: 1.0m),
            peakPnlPercent: 2.0m);

        Assert.True(result.ShouldSell);
        Assert.Equal(ExitReason.StopLoss, result.ExitReason);
    }

    // A legacy position with no recorded peak (null) can never trail-fire.
    [Fact]
    public void TrailingStop_never_fires_without_a_recorded_peak()
    {
        var result = PositionExitPolicy.EvaluateHeldPosition(
            desiredLong: true,
            positionAgeSeconds: 60,
            conservativeUnrealizedPnlPercent: 0.1m,
            canValuePosition: true,
            killSwitchActive: false,
            Execution(),
            Exit(takeProfit: 3.0m, trailingActivation: 1.5m, trailingDistance: 1.0m),
            peakPnlPercent: null);

        Assert.False(result.ShouldSell);
        Assert.Equal("DESIRED_LONG", result.HoldReasonCode);
    }

    // A zero StopLossPercent disables the stop-loss guard (consistent with
    // MaxHoldMinutes = 0) rather than firing on every valued loss.
    [Fact]
    public void StopLoss_disabled_when_percent_is_zero()
    {
        var result = PositionExitPolicy.EvaluateHeldPosition(
            desiredLong: true,
            positionAgeSeconds: 60,
            conservativeUnrealizedPnlPercent: -5m,
            canValuePosition: true,
            killSwitchActive: false,
            Execution(),
            Exit(stopLoss: 0m));

        Assert.False(result.ShouldSell);
        Assert.Equal("DESIRED_LONG", result.HoldReasonCode);
    }
}
