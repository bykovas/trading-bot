using System.Globalization;
using TradingBot.Core.Common;

namespace TradingBot.Core.Risk;

public static class PositionExitPolicy
{
    public static ExitEvaluation EvaluateHeldPosition(
        bool desiredLong,
        double positionAgeSeconds,
        decimal conservativeUnrealizedPnlPercent,
        bool canValuePosition,
        bool killSwitchActive,
        ExecutionPolicyOptions executionPolicy,
        PositionExitOptions positionExit,
        decimal? peakPnlPercent = null,
        bool exitHysteresisEnabled = false,
        ScoreDecaySnapshot? scoreDecay = null,
        bool recentPriceActionNegative = false,
        PositionExitLevelsSnapshot? exitLevels = null)
    {
        var pnl = conservativeUnrealizedPnlPercent;

        var hardRiskExit = EvaluateHardRiskRules(pnl, canValuePosition, killSwitchActive, positionExit, exitLevels);
        if (hardRiskExit is not null)
        {
            return hardRiskExit;
        }

        var forcedExit = EvaluateForcedExits(
            desiredLong,
            pnl,
            positionAgeSeconds,
            canValuePosition,
            peakPnlPercent,
            positionExit,
            scoreDecay,
            exitLevels);
        if (forcedExit is not null)
        {
            return forcedExit;
        }

        var defensiveExit = EvaluateDefensiveExits(pnl, positionAgeSeconds, canValuePosition, scoreDecay, recentPriceActionNegative, positionExit);
        if (defensiveExit is not null)
        {
            return defensiveExit;
        }

        return EvaluateStrategyExit(desiredLong, positionAgeSeconds, pnl, canValuePosition, exitHysteresisEnabled, executionPolicy, positionExit);
    }

    private static ExitEvaluation? EvaluateHardRiskRules(
        decimal pnl,
        bool canValuePosition,
        bool killSwitchActive,
        PositionExitOptions positionExit,
        PositionExitLevelsSnapshot? exitLevels)
    {
        if (killSwitchActive)
        {
            return Sell(ExitReason.KillSwitch, "kill-switch exit: flattening position while kill switch is active");
        }

        if (canValuePosition
            && exitLevels?.StopLossPrice is { } stopLossPrice
            && PriceCrossedStopLoss(exitLevels, stopLossPrice))
        {
            return Sell(
                ExitReason.StopLoss,
                $"stop-loss exit: conservative exit price {Price(exitLevels.ConservativeExitPrice)} crossed saved stop {Price(stopLossPrice)}");
        }

        var fixedStopLossPercent = positionExit.EffectiveFixedStopLossPercent;
        if (canValuePosition && fixedStopLossPercent > 0m && pnl <= -fixedStopLossPercent)
        {
            return Sell(
                ExitReason.StopLoss,
                $"stop-loss exit: unrealized PnL {Percent(pnl)}% <= -{Percent(fixedStopLossPercent)}%");
        }

        return null;
    }

    private static ExitEvaluation? EvaluateForcedExits(
        bool desiredLong,
        decimal pnl,
        double positionAgeSeconds,
        bool canValuePosition,
        decimal? peakPnlPercent,
        PositionExitOptions positionExit,
        ScoreDecaySnapshot? scoreDecay,
        PositionExitLevelsSnapshot? exitLevels)
    {
        if (canValuePosition
            && exitLevels?.TakeProfitPrice is { } takeProfitPrice
            && PriceCrossedTakeProfit(exitLevels, takeProfitPrice))
        {
            return Sell(
                ExitReason.TakeProfit,
                $"take-profit exit: conservative exit price {Price(exitLevels.ConservativeExitPrice)} crossed saved take-profit {Price(takeProfitPrice)}");
        }

        var fixedTakeProfitPercent = positionExit.EffectiveFixedTakeProfitPercent;
        if (canValuePosition && fixedTakeProfitPercent > 0m && pnl >= fixedTakeProfitPercent)
        {
            return Sell(
                ExitReason.TakeProfit,
                $"take-profit exit: unrealized PnL {Percent(pnl)}% >= {Percent(fixedTakeProfitPercent)}%");
        }

        if (canValuePosition
            && positionExit.TrailingActivationPercent > 0m
            && positionExit.TrailingDistancePercent > 0m
            && peakPnlPercent is { } peak
            && peak >= positionExit.TrailingActivationPercent
            && pnl <= peak - positionExit.TrailingDistancePercent)
        {
            return Sell(
                ExitReason.TrailingStop,
                $"trailing-stop exit: unrealized PnL {Percent(pnl)}% <= peak {Percent(peak)}% - {Percent(positionExit.TrailingDistancePercent)}%");
        }

        if (positionExit.MaxHoldMinutes > 0 && positionAgeSeconds >= positionExit.MaxHoldMinutes * 60.0)
        {
            var ageMinutes = positionAgeSeconds / 60.0;
            var thesisWeak =
                !desiredLong
                || scoreDecay is { ScoreConfirmsEntry: false }
                || scoreDecay is { EmaBullish: false };
            var losing = canValuePosition && pnl < 0m;

            if (losing || thesisWeak)
            {
                var details = new List<string>();
                if (losing)
                {
                    details.Add($"PnL {Percent(pnl)}% < 0%");
                }

                if (!desiredLong)
                {
                    details.Add("desired position is none");
                }

                if (scoreDecay is { ScoreConfirmsEntry: false })
                {
                    details.Add("score no longer confirms entry");
                }

                if (scoreDecay is { EmaBullish: false })
                {
                    details.Add("EMA structure is no longer bullish");
                }

                return Sell(
                    ExitReason.MaxHold,
                    $"conditional max-hold exit: position age {ageMinutes.ToString("0", CultureInfo.InvariantCulture)}m >= {positionExit.MaxHoldMinutes}m and stale-position condition met ({string.Join("; ", details)})");
            }

            return Hold(
                "MAX_HOLD_HEALTHY_HOLD",
                $"conditional max-hold hold: position age {ageMinutes.ToString("0", CultureInfo.InvariantCulture)}m >= {positionExit.MaxHoldMinutes}m but position is non-negative or unvalued and thesis still confirms");
        }

        return null;
    }

    private static ExitEvaluation? EvaluateDefensiveExits(
        decimal pnl,
        double positionAgeSeconds,
        bool canValuePosition,
        ScoreDecaySnapshot? scoreDecay,
        bool recentPriceActionNegative,
        PositionExitOptions positionExit)
    {
        if (!canValuePosition || scoreDecay is null)
        {
            return null;
        }

        var highConvictionEntry =
            positionExit.ScoreDecayMinEntryScore > 0m
            && scoreDecay.EntryScore is { } entryScore
            && entryScore >= positionExit.ScoreDecayMinEntryScore;

        if (highConvictionEntry
            && positionExit.ScoreDecayImmediateScore > 0m
            && scoreDecay.CurrentScore <= positionExit.ScoreDecayImmediateScore
            && pnl <= 0m)
        {
            return Sell(
                ExitReason.ScoreDecay,
                $"score-decay exit: entry score {scoreDecay.EntryScore:0.##} collapsed to {scoreDecay.CurrentScore:0.##} <= {positionExit.ScoreDecayImmediateScore:0.##} with PnL {Percent(pnl)}%; exiting before stop-loss");
        }

        if (highConvictionEntry
            && positionExit.ScoreDecayDefensiveScore > 0m
            && positionExit.ScoreDecayDefensiveCycles > 0
            && scoreDecay.ConsecutiveLowScoreCycles >= positionExit.ScoreDecayDefensiveCycles
            && pnl <= 0m)
        {
            return Sell(
                ExitReason.ScoreDecay,
                $"score-decay exit: score <= {positionExit.ScoreDecayDefensiveScore:0.##} for {scoreDecay.ConsecutiveLowScoreCycles} consecutive cycles (entry score {scoreDecay.EntryScore:0.##}) with PnL {Percent(pnl)}%; defensive exit before stop-loss");
        }

        var postEntryStructureDeteriorated = !scoreDecay.EmaBullish || !scoreDecay.MomentumPositive;
        if (positionExit.PostEntryAdverseWindowMinutes > 0
            && positionExit.PostEntryAdverseLossPercent > 0m
            && positionAgeSeconds <= positionExit.PostEntryAdverseWindowMinutes * 60.0
            && pnl <= -positionExit.PostEntryAdverseLossPercent
            && recentPriceActionNegative
            && postEntryStructureDeteriorated
            && scoreDecay.CurrentScore < 0.85m)
        {
            return Sell(
                ExitReason.PostEntryAdverse,
                $"post-entry adverse exit: PnL {Percent(pnl)}% <= -{Percent(positionExit.PostEntryAdverseLossPercent)}% within {positionExit.PostEntryAdverseWindowMinutes}m of entry, score {scoreDecay.CurrentScore:0.##} is below 0.85, recent price action is negative, and structure deteriorated (EMA bullish={scoreDecay.EmaBullish}, momentum positive={scoreDecay.MomentumPositive})");
        }

        return null;
    }

    private static ExitEvaluation EvaluateStrategyExit(
        bool desiredLong,
        double positionAgeSeconds,
        decimal pnl,
        bool canValuePosition,
        bool exitHysteresisEnabled,
        ExecutionPolicyOptions executionPolicy,
        PositionExitOptions positionExit)
    {
        if (desiredLong)
        {
            return Hold("DESIRED_LONG", "current position already matches desired long exposure");
        }

        if (!executionPolicy.AllowImmediateExitOnSignalFlip && positionAgeSeconds < executionPolicy.MinHoldSeconds)
        {
            return Hold(
                "MIN_HOLD_BLOCK",
                $"minimum hold active: signal flip ignored until position age reaches {executionPolicy.MinHoldSeconds}s");
        }

        if (exitHysteresisEnabled && positionExit.MaxSignalFlipLossExitPercent < 0m)
        {
            var floor = positionExit.MaxSignalFlipLossExitPercent;
            if (canValuePosition && pnl < floor)
            {
                return Hold(
                    "FLIP_LOSS_FLOOR_BLOCK",
                    $"confirmed bearish flip held: PnL {Percent(pnl)}% is below the controlled-loss floor {Percent(floor)}%; deeper losses are owned by stop-loss / max-hold");
            }

            return Sell(ExitReason.SignalFlip, $"confirmed bearish flip: PnL {Percent(pnl)}% is at or above the controlled-loss floor {Percent(floor)}%; exiting");
        }

        if (canValuePosition && pnl < positionExit.MinProfitToExitOnSignalFlipPercent)
        {
            return Hold(
                "MIN_PROFIT_BLOCK",
                $"signal flip ignored: unrealized PnL {Percent(pnl)}% is below minimum exit profit {Percent(positionExit.MinProfitToExitOnSignalFlipPercent)}%");
        }

        return Sell(ExitReason.SignalFlip, "signal-flip exit: desired position is none and soft-hold guards passed");
    }

    public static string ExitReasonCode(ExitReason reason) => reason switch
    {
        ExitReason.StopLoss => "SELL_STOP_LOSS",
        ExitReason.TakeProfit => "SELL_TAKE_PROFIT",
        ExitReason.MaxHold => "SELL_MAX_HOLD",
        ExitReason.KillSwitch => "SELL_KILL_SWITCH",
        ExitReason.EmergencyRisk => "SELL_EMERGENCY_RISK",
        ExitReason.BrokerSafety => "SELL_BROKER_SAFETY",
        ExitReason.TrailingStop => "SELL_TRAILING_STOP",
        ExitReason.ScoreDecay => "SELL_SCORE_DECAY",
        ExitReason.PostEntryAdverse => "SELL_POST_ENTRY_ADVERSE",
        _ => "SELL_SIGNAL_FLIP"
    };

    private static ExitEvaluation Sell(ExitReason reason, string text) => new(true, reason, null, text);

    private static ExitEvaluation Hold(string holdReasonCode, string text) => new(false, null, holdReasonCode, text);

    private static string Percent(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Price(decimal value) => value.ToString("0.##########", CultureInfo.InvariantCulture);

    private static bool PriceCrossedStopLoss(PositionExitLevelsSnapshot levels, decimal stopLossPrice) =>
        levels.Side.Equals("SHORT", StringComparison.OrdinalIgnoreCase)
            ? levels.ConservativeExitPrice >= stopLossPrice
            : levels.ConservativeExitPrice <= stopLossPrice;

    private static bool PriceCrossedTakeProfit(PositionExitLevelsSnapshot levels, decimal takeProfitPrice) =>
        levels.Side.Equals("SHORT", StringComparison.OrdinalIgnoreCase)
            ? levels.ConservativeExitPrice <= takeProfitPrice
            : levels.ConservativeExitPrice >= takeProfitPrice;
}
