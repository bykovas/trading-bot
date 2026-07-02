using System.Globalization;

namespace TradingBot.Worker;

// Result of evaluating a held LONG position against the deterministic exit policy.
// This is intentionally a pure value type so the decision logic can be unit tested
// without market data, files, or fills.
internal sealed record ExitEvaluation(
    bool ShouldSell,
    ExitReason? ExitReason,
    string? HoldReasonCode,
    string Reason);

// Deterministic exit policy for a currently held LONG position.
//
// The evaluation is a pure function of the position age, conservative unrealized
// PnL, desired position and the configured policies. Keeping it free of side
// effects makes the priority ordering easy to reason about and to test.
//
// Priority (highest first):
//   1. Kill switch / emergency exit (hard exit)
//   2. Stop-loss                    (hard exit)
//   3. Take-profit                  (hard exit)
//   4. Max-hold                     (hard exit)
//   5. Normal desired-position reconciliation:
//        desired LONG_MICRO -> HOLD (already matches)
//        desired NONE       -> signal-flip exit, but only after the soft guards:
//            - MinHoldSeconds
//            - MinProfitToExitOnSignalFlipPercent
//
// Hard exits (1-4) bypass MinHoldSeconds and MinProfitToExitOnSignalFlipPercent.
internal static class PositionExitPolicy
{
    public static ExitEvaluation EvaluateHeldPosition(
        bool desiredLong,
        double positionAgeSeconds,
        decimal conservativeUnrealizedPnlPercent,
        bool canValuePosition,
        bool killSwitchActive,
        ExecutionPolicyOptions executionPolicy,
        PositionExitOptions positionExit)
    {
        var pnl = conservativeUnrealizedPnlPercent;

        // 1. Kill switch / emergency exit. This uses the existing RiskOptions.KillSwitch
        //    and flattens an open position regardless of hold/profit guards.
        //    TODO: broker/account safety exits and other emergency signals plug in here.
        if (killSwitchActive)
        {
            return Sell(ExitReason.KillSwitch, "kill-switch exit: flattening position while kill switch is active");
        }

        // 2. Stop-loss. Only evaluated when the position can be valued.
        if (canValuePosition && pnl <= -positionExit.StopLossPercent)
        {
            return Sell(
                ExitReason.StopLoss,
                $"stop-loss exit: unrealized PnL {Percent(pnl)}% <= -{Percent(positionExit.StopLossPercent)}%");
        }

        // 3. Take-profit. Fires even if the strategy still wants LONG_MICRO.
        if (canValuePosition && pnl >= positionExit.TakeProfitPercent)
        {
            return Sell(
                ExitReason.TakeProfit,
                $"take-profit exit: unrealized PnL {Percent(pnl)}% >= {Percent(positionExit.TakeProfitPercent)}%");
        }

        // 4. Max-hold. Age-based, independent of valuation. Zero disables the guard.
        if (positionExit.MaxHoldMinutes > 0 && positionAgeSeconds >= positionExit.MaxHoldMinutes * 60.0)
        {
            var ageMinutes = positionAgeSeconds / 60.0;
            return Sell(
                ExitReason.MaxHold,
                $"max-hold exit: position age {ageMinutes.ToString("0", CultureInfo.InvariantCulture)}m >= {positionExit.MaxHoldMinutes}m");
        }

        // 5. Normal desired-position reconciliation.
        if (desiredLong)
        {
            return Hold("DESIRED_LONG", "current position already matches desired long exposure");
        }

        // desired == NONE: this is an ordinary signal-flip exit subject to soft guards.
        if (!executionPolicy.AllowImmediateExitOnSignalFlip && positionAgeSeconds < executionPolicy.MinHoldSeconds)
        {
            return Hold(
                "MIN_HOLD_BLOCK",
                $"minimum hold active: signal flip ignored until position age reaches {executionPolicy.MinHoldSeconds}s");
        }

        if (canValuePosition && pnl < positionExit.MinProfitToExitOnSignalFlipPercent)
        {
            return Hold(
                "MIN_PROFIT_BLOCK",
                $"signal flip ignored: unrealized PnL {Percent(pnl)}% is below minimum exit profit {Percent(positionExit.MinProfitToExitOnSignalFlipPercent)}%");
        }

        return Sell(ExitReason.SignalFlip, "signal-flip exit: desired position is none and soft-hold guards passed");
    }

    // Machine-readable code for the JSON event / console, so dashboards can tell the
    // different sell and hold reasons apart without parsing free text.
    public static string ExitReasonCode(ExitReason reason) => reason switch
    {
        Worker.ExitReason.StopLoss => "SELL_STOP_LOSS",
        Worker.ExitReason.TakeProfit => "SELL_TAKE_PROFIT",
        Worker.ExitReason.MaxHold => "SELL_MAX_HOLD",
        Worker.ExitReason.KillSwitch => "SELL_KILL_SWITCH",
        Worker.ExitReason.EmergencyRisk => "SELL_EMERGENCY_RISK",
        Worker.ExitReason.BrokerSafety => "SELL_BROKER_SAFETY",
        Worker.ExitReason.TrailingStop => "SELL_TRAILING_STOP",
        _ => "SELL_SIGNAL_FLIP"
    };

    private static ExitEvaluation Sell(ExitReason reason, string text) => new(true, reason, null, text);

    private static ExitEvaluation Hold(string holdReasonCode, string text) => new(false, null, holdReasonCode, text);

    private static string Percent(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
