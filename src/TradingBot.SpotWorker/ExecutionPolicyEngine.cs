using System.Globalization;

namespace TradingBot.SpotWorker;

// Result of evaluating a held LONG position against the deterministic exit policy.
// This is intentionally a pure value type so the decision logic can be unit tested
// without market data, files, or fills.
internal sealed record ExitEvaluation(
    bool ShouldSell,
    ExitReason? ExitReason,
    string? HoldReasonCode,
    string Reason);

// Score history of a held position as seen by the defensive exit rules: the score at
// entry time, the score this cycle, how many consecutive cycles the score has been at
// or below the defensive level, and whether the current score still clears the
// original entry threshold.
internal sealed record ScoreDecaySnapshot(
    decimal? EntryScore,
    decimal CurrentScore,
    int ConsecutiveLowScoreCycles,
    bool ScoreConfirmsEntry,
    bool EmaBullish = true,
    bool MomentumPositive = true);

internal sealed record PositionExitLevelsSnapshot(
    string Side,
    decimal? StopLossPrice,
    decimal? TakeProfitPrice,
    decimal ConservativeExitPrice);

// Deterministic exit policy for a currently held LONG position.
//
// The evaluation is a pure function of the position age, conservative unrealized
// PnL, desired position and the configured policies. Keeping it free of side
// effects makes the priority ordering easy to reason about and to test.
//
// The evaluation is organised into strict priority TIERS. A higher tier is always
// resolved before any lower tier is even consulted, so a lower-tier soft filter
// (such as MinProfitToExitOnSignalFlipPercent) can never suppress a higher-tier
// exit. This ordering is the whole point of the policy and must not be broken:
//
//   TIER 1 - HARD RISK RULES (highest priority, nothing may block them)
//     - Kill switch / emergency risk / broker safety
//     - Stop-loss
//     These flatten the position immediately and bypass EVERY hold/profit guard.
//
//   TIER 2 - FORCED EXITS (bypass the profit filter, rank below hard risk)
//     - Take-profit
//     - Conditional max-hold / stale-position exit
//     (Future position-invalid / data-integrity forced exits plug in here.)
//
//   TIER 3 - STRATEGY EXITS (may be filtered)
//     - desired LONG_MICRO -> HOLD (already matches)
//     - desired NONE       -> signal-flip exit, but only after the soft guards:
//         - MinHoldSeconds            (MIN_HOLD_BLOCK)
//         - MinProfitToExitOnSignalFlipPercent (MIN_PROFIT_BLOCK)
//
//   TIER 4 - STRATEGY ENTRIES (BUY / position increase) are handled upstream in
//     DryRunPortfolio and are never reached for a position that is already held.
//
// Only TIER 3 signal-flip exits are subject to MinProfitToExitOnSignalFlipPercent.
// The min-profit / min-hold filters are structurally unreachable for TIER 1 and
// TIER 2 exits because those tiers return before the strategy tier is evaluated.
internal static class PositionExitPolicy
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

        // TIER 1 - HARD RISK RULES. Highest priority. Nothing below may block these.
        var hardRiskExit = EvaluateHardRiskRules(pnl, canValuePosition, killSwitchActive, positionExit, exitLevels);
        if (hardRiskExit is not null)
        {
            return hardRiskExit;
        }

        // TIER 2 - FORCED EXITS. Rank below hard risk but still bypass the profit filter.
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

        // TIER 2.5 - DEFENSIVE EXITS. Score decay and post-entry adverse movement.
        // These protect a failed high-score entry from drifting into the full
        // stop-loss: they bypass min-hold / min-profit but rank below take-profit,
        // trailing stop and max-hold so a profitable position is never cut by them.
        var defensiveExit = EvaluateDefensiveExits(pnl, positionAgeSeconds, canValuePosition, scoreDecay, recentPriceActionNegative, positionExit);
        if (defensiveExit is not null)
        {
            return defensiveExit;
        }

        // TIER 3 - STRATEGY EXITS (the only tier subject to the soft profit/hold guards).
        return EvaluateStrategyExit(desiredLong, positionAgeSeconds, pnl, canValuePosition, exitHysteresisEnabled, executionPolicy, positionExit);
    }

    // TIER 1: hard risk rules. These MUST always execute immediately and can never
    // be blocked by hold-time or profit filters. Returns null when none fire.
    private static ExitEvaluation? EvaluateHardRiskRules(
        decimal pnl,
        bool canValuePosition,
        bool killSwitchActive,
        PositionExitOptions positionExit,
        PositionExitLevelsSnapshot? exitLevels)
    {
        // Kill switch / emergency exit. Uses the existing RiskOptions.KillSwitch and
        // flattens an open position regardless of hold/profit guards.
        // TODO: liquidation, broker/account safety, and other emergency signals plug in here.
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

        // Legacy fallback for positions without saved price levels.
        var fixedStopLossPercent = positionExit.EffectiveFixedStopLossPercent;
        if (canValuePosition && fixedStopLossPercent > 0m && pnl <= -fixedStopLossPercent)
        {
            return Sell(
                ExitReason.StopLoss,
                $"stop-loss exit: unrealized PnL {Percent(pnl)}% <= -{Percent(fixedStopLossPercent)}%");
        }

        return null;
    }

    // TIER 2: forced exits. These bypass the profit filter but rank below hard risk.
    // Returns null when none fire.
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

        // Legacy fallback for positions without saved price levels.
        var fixedTakeProfitPercent = positionExit.EffectiveFixedTakeProfitPercent;
        if (canValuePosition && fixedTakeProfitPercent > 0m && pnl >= fixedTakeProfitPercent)
        {
            return Sell(
                ExitReason.TakeProfit,
                $"take-profit exit: unrealized PnL {Percent(pnl)}% >= {Percent(fixedTakeProfitPercent)}%");
        }

        // Trailing stop. Once the conservative peak PnL reaches the activation level,
        // sell if PnL falls at least TrailingDistancePercent below that peak. Both knobs
        // must be positive to arm it. Ranks below take-profit, above max-hold.
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

        // Conditional max-hold / stale-position exit. Age alone is not enough to
        // flatten: sell when the position is old and either losing, no longer desired,
        // or the original signal structure has deteriorated. Unknown valuation is not
        // treated as a loss; it only exits when the thesis is weak.
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

    // TIER 2.5: defensive exits for failed high-score entries. Only losing (or flat)
    // positions are ever cut here — a profitable position is left to take-profit /
    // trailing rules. Returns null when nothing fires.
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

        // Score-decay rule 1 (immediate): the score collapsed outright and the
        // position is not profitable — the original thesis is gone, exit now while
        // the loss is still shallower than the stop-loss.
        if (highConvictionEntry
            && positionExit.ScoreDecayImmediateScore > 0m
            && scoreDecay.CurrentScore <= positionExit.ScoreDecayImmediateScore
            && pnl <= 0m)
        {
            return Sell(
                ExitReason.ScoreDecay,
                $"score-decay exit: entry score {scoreDecay.EntryScore:0.##} collapsed to {scoreDecay.CurrentScore:0.##} <= {positionExit.ScoreDecayImmediateScore:0.##} with PnL {Percent(pnl)}%; exiting before stop-loss");
        }

        // Score-decay rule 2 (persistent): the score stayed below the defensive level
        // for N consecutive cycles and the position is not profitable.
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

        // Post-entry adverse movement guard: inside the initial monitoring window a
        // fresh position that is already down more than the friction-adjusted
        // threshold is only cut when the setup itself is structurally deteriorating.
        // A strong score with only noisy negative price action is not enough for this
        // exit; hard stop-loss owns deeper losses.
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

    // TIER 3: strategy exits. This is the ONLY tier where MinHoldSeconds and
    // MinProfitToExitOnSignalFlipPercent may suppress an exit.
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

        // desired == NONE: this is an ordinary signal-flip exit subject to soft guards.
        // The minimum-hold guard always applies (it is not replaced by the loss floor).
        if (!executionPolicy.AllowImmediateExitOnSignalFlip && positionAgeSeconds < executionPolicy.MinHoldSeconds)
        {
            return Hold(
                "MIN_HOLD_BLOCK",
                $"minimum hold active: signal flip ignored until position age reaches {executionPolicy.MinHoldSeconds}s");
        }

        // Controlled-loss exit for CONFIRMED bearish flips. When exit hysteresis is on
        // (Strategy.ExitEmaGapPercent > 0), desired NONE on a held position means a
        // confirmed bearish cross. For those flips ONLY, the loss floor replaces the
        // min-profit guard: exit while the loss is still shallow (PnL >= floor), but
        // leave deeper losses to stop-loss / max-hold / emergency rules. A floor of 0
        // disables this mechanism and falls back to the legacy min-profit behavior.
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

    // Machine-readable code for the JSON event / console, so dashboards can tell the
    // different sell and hold reasons apart without parsing free text.
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
