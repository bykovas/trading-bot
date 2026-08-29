namespace TradingBot.SpotWorker;

internal sealed class TechnicalDecisionEngine
{
    public DecisionProposal Decide(
        InstrumentMarketState marketState,
        IndicatorSnapshot indicators,
        TradingOptions trading,
        StrategyOptions strategy,
        PositionSizingOptions positionSizing,
        RiskOptions risk,
        decimal cashEur,
        decimal currentExposureEur,
        bool hasOpenPosition = false,
        PriceActionAssessment? priceAction = null)
    {
        var signal = SignalScorer.Evaluate(marketState, indicators, strategy, priceAction, includeEarlyEntryDiagnostics: !hasOpenPosition);
        // Core scoring stops at a venue-neutral intent; the spot worker owns the
        // translation into its persisted desired-position vocabulary.
        var entryDesired = SignalScorer.IntentOf(signal, strategy) == SignalIntent.LongCandidate ? "LONG_MICRO" : "NONE";
        var contributions = signal.Contributions.ToList();

        if (hasOpenPosition)
        {
            // Exit hysteresis: a held position keeps its LONG desire unless a CONFIRMED
            // bearish cross appears. A merely weak (non-bearish) signal is a HOLD, not a
            // flip. This governs only the desired-position handoff to the exit policy;
            // hard exits (stop-loss / take-profit / trailing / max-hold / kill switch)
            // are evaluated independently downstream.
            var held = EvaluateHeldDesire(indicators, strategy, entryDesired);
            contributions.Add(held.Note);
            if (held.Desired == "LONG_MICRO")
            {
                // A held position needs no new-entry sizing; a capacity-driven zero target
                // must never masquerade as a signal flip.
                contributions.Add(new SignalContribution("PositionSizing", 0m, "holding existing position; new-entry sizing skipped"));
            }

            return new DecisionProposal(
                marketState.Instrument.Pair,
                held.Desired,
                signal.Score,
                0m,
                contributions,
                SpreadPercent: EntrySpreadPercent(marketState),
                HasBullishStructure: signal.HasBullishStructure,
                EmaFullyConfirmed: signal.EmaFullyConfirmed,
                BullishEmaGapPercent: signal.BullishEmaGapPercent,
                EmaGapVelocityPercent: signal.EmaGapVelocityPercent);
        }

        // NEW-entry decisioning happens in explicit layers (see EntryGate):
        //   A. hard safety filters (spread, liquidity, tradability),
        //   B. quality filters (score thresholds, anti-lag price-action guard),
        //   C. ranking + portfolio-level gates run downstream in the worker/portfolio.
        var gate = EntryGate.Evaluate(signal, marketState, priceAction, strategy, trading.LiveTradingEnabled, indicators);
        contributions.AddRange(gate.Notes);
        var desiredPosition = gate.DesiredPosition;
        var rejectionReason = gate.RejectionReason;
        var targetNotional = 0m;

        if (desiredPosition == "LONG_MICRO")
        {
            var size = SelectPositionSize(signal, trading, positionSizing, risk, cashEur, currentExposureEur, gate.EarlyEntry);
            targetNotional = size.TargetNotionalEur;
            contributions.Add(new SignalContribution("PositionSizing", 0m, size.Reason));
            if (targetNotional <= 0m)
            {
                desiredPosition = "NONE";
                rejectionReason = "REJECT_NO_CAPACITY";
            }
        }

        return new DecisionProposal(
            marketState.Instrument.Pair,
            desiredPosition,
            signal.Score,
            targetNotional,
            contributions,
            rejectionReason,
            gate.Exploratory && desiredPosition == "LONG_MICRO",
            gate.SpreadPercent,
            signal.HasBullishStructure,
            signal.EmaFullyConfirmed,
            signal.BullishEmaGapPercent,
            signal.EmaGapVelocityPercent,
            EarlyEntryEligible(signal, gate.SpreadPercent, priceAction, strategy),
            EarlyEntryReason(signal, gate.SpreadPercent, priceAction, strategy),
            signal.Score,
            signal.HasBullishStructure && !signal.AllowsLong
                ? SelectPositionSize(signal, trading, positionSizing, risk, cashEur, currentExposureEur).TargetNotionalEur
                : 0m,
            EarlyEntryCandidate: gate.EarlyEntry && desiredPosition == "LONG_MICRO");
    }

    // Exit hysteresis for a held position (2.1). Returns the desired position plus a
    // contribution line explaining which case applied. With ExitEmaGapPercent = 0 the
    // held desire simply follows the entry signal (old flip-when-weak behavior).
    private static (string Desired, SignalContribution Note) EvaluateHeldDesire(
        IndicatorSnapshot indicators,
        StrategyOptions strategy,
        string entryDesired)
    {
        if (strategy.ExitEmaGapPercent <= 0m)
        {
            return (entryDesired, new SignalContribution(
                "ExitSignal",
                0m,
                $"exit hysteresis disabled; held desire follows entry signal ({entryDesired})"));
        }

        if (indicators.FastEma is { } fast && indicators.SlowEma is { } slow && slow != 0m)
        {
            var gapPercent = Math.Abs(fast - slow) / slow * 100m;
            if (fast < slow && gapPercent >= strategy.ExitEmaGapPercent)
            {
                return ("NONE", new SignalContribution(
                    "ExitSignal",
                    0m,
                    $"confirmed bearish cross: fast EMA below slow by {gapPercent:0.###}% >= exit gap {strategy.ExitEmaGapPercent:0.###}%; flipping desired to none"));
            }

            return ("LONG_MICRO", new SignalContribution(
                "ExitSignal",
                0m,
                $"no confirmed bearish cross (fast/slow gap {gapPercent:0.###}% < exit gap {strategy.ExitEmaGapPercent:0.###}%); holding long"));
        }

        return ("LONG_MICRO", new SignalContribution(
            "ExitSignal",
            0m,
            "EMA unavailable; holding long rather than flipping on missing data"));
    }

    private static bool EarlyEntryEligible(
        TechnicalSignal signal,
        decimal spreadPercent,
        PriceActionAssessment? priceAction,
        StrategyOptions strategy) =>
        signal.HasBullishStructure
        && !signal.AllowsLong
        && signal.Score >= Math.Min(strategy.ExploratoryMinimumLongScore, strategy.MinimumLongScore)
        && priceAction is { IsPositive: true }
        && priceAction.TrendPercent >= strategy.ExploratoryMinPriceActionTrendPercent
        && signal.BullishEmaGapPercent is { } gap
        && gap >= strategy.ExploratoryMinBullishEmaGapPercent
        && signal.EmaGapVelocityPercent is { } velocity
        && velocity >= strategy.ExploratoryMinEmaGapVelocityPercent
        && (strategy.MaxExploratorySpreadPercent <= 0m || spreadPercent <= strategy.MaxExploratorySpreadPercent)
        && (SignalScorer.HasPositiveContribution(signal, "Momentum") || signal.VolumeConfirmed);

    private static string EarlyEntryReason(
        TechnicalSignal signal,
        decimal spreadPercent,
        PriceActionAssessment? priceAction,
        StrategyOptions strategy)
    {
        if (!signal.HasBullishStructure)
        {
            return "not early-eligible: no early bullish EMA structure";
        }

        if (signal.AllowsLong)
        {
            return "not early-eligible: EMA is already fully confirmed; normal gate applies";
        }

        var diagnosticThreshold = Math.Min(strategy.ExploratoryMinimumLongScore, strategy.MinimumLongScore);
        if (signal.Score < diagnosticThreshold)
        {
            return $"not early-eligible: diagnostic score {signal.Score:0.##} below {diagnosticThreshold:0.##}";
        }

        if (priceAction is not { DataSufficient: true })
        {
            return $"not early-eligible: price action is {PriceActionAssessment.WarmupStateOf(priceAction)}";
        }

        if (!priceAction.IsPositive)
        {
            return $"not early-eligible: price action is {priceAction.Direction}";
        }

        if (priceAction.TrendPercent < strategy.ExploratoryMinPriceActionTrendPercent)
        {
            return $"not early-eligible: price-action trend {priceAction.TrendPercent:0.###}% below {strategy.ExploratoryMinPriceActionTrendPercent:0.###}%";
        }

        if (signal.BullishEmaGapPercent is not { } gap
            || gap < strategy.ExploratoryMinBullishEmaGapPercent)
        {
            return $"not early-eligible: bullish EMA gap {signal.BullishEmaGapPercent?.ToString("0.###") ?? "unknown"}% below {strategy.ExploratoryMinBullishEmaGapPercent:0.###}%";
        }

        if (signal.EmaGapVelocityPercent is not { } velocity
            || velocity < strategy.ExploratoryMinEmaGapVelocityPercent)
        {
            return $"not early-eligible: EMA gap velocity {signal.EmaGapVelocityPercent?.ToString("0.###") ?? "unknown"}% below {strategy.ExploratoryMinEmaGapVelocityPercent:0.###}%";
        }

        if (strategy.MaxExploratorySpreadPercent > 0m && spreadPercent > strategy.MaxExploratorySpreadPercent)
        {
            return $"not early-eligible: spread {spreadPercent:0.###}% exceeds diagnostic max {strategy.MaxExploratorySpreadPercent:0.###}%";
        }

        if (!SignalScorer.HasPositiveContribution(signal, "Momentum") && !signal.VolumeConfirmed)
        {
            return "not early-eligible: needs momentum or volume confirmation";
        }

        return $"early-entry diagnostic only: partial EMA structure score {signal.Score:0.##}, positive price action and confirmation present; normal gate still blocks trading";
    }

    private static decimal EntrySpreadPercent(InstrumentMarketState marketState) =>
        EntryGate.SpreadPercentOf(marketState);

    private static PositionSizeSelection SelectPositionSize(
        TechnicalSignal signal,
        TradingOptions trading,
        PositionSizingOptions positionSizing,
        RiskOptions risk,
        decimal cashEur,
        decimal currentExposureEur,
        bool earlyEntry = false)
    {
        if (!positionSizing.Enabled)
        {
            return new PositionSizeSelection(
                trading.TargetOrderEur,
                $"fixed target EUR {trading.TargetOrderEur:0.##}");
        }

        var veryStrongByScore = signal.Score >= positionSizing.VeryStrongScoreThreshold;
        var strongByScore = signal.Score >= positionSizing.StrongScoreThreshold;
        var strongByEmaGap =
            signal.Score >= positionSizing.StrongEmaGapScoreThreshold
            && signal.BullishEmaGapPercent >= positionSizing.StrongEmaGapPercent;

        // Early entries take the BASE order regardless of their (by construction
        // sub-firm) score: the channel's own gates are the qualification, and the
        // score tiers would otherwise silently demote every early entry to small.
        var target = earlyEntry
            ? positionSizing.BaseOrderEur
            : veryStrongByScore
                ? positionSizing.VeryStrongOrderEur
                : strongByScore || strongByEmaGap
                    ? positionSizing.StrongOrderEur
                    : signal.Score >= positionSizing.BaseScoreThreshold
                        ? positionSizing.BaseOrderEur
                        : positionSizing.SmallOrderEur;

        var effectiveMaxOrder = Math.Min(positionSizing.MaxOrderEur, risk.MaxOrderEur);
        target = Math.Min(target, effectiveMaxOrder);
        var selectedTarget = target;

        var availableCash = positionSizing.CashReserveEur > 0m
            ? cashEur - positionSizing.CashReserveEur
            : cashEur;
        var availableExposure = risk.MaxTotalExposureEur > 0m
            ? risk.MaxTotalExposureEur - currentExposureEur
            : decimal.MaxValue;
        var availableNotional = Math.Min(availableCash, availableExposure);

        if (positionSizing.CashReserveEur > 0m || risk.MaxTotalExposureEur > 0m)
        {
            target = PositionSizeTiers(positionSizing)
                .Where(tier => tier <= selectedTarget && tier <= availableNotional)
                .DefaultIfEmpty(0m)
                .Max();
        }

        if (target <= 0m)
        {
            return new PositionSizeSelection(
                0m,
                $"score {signal.Score:0.##} selected no entry because cash EUR {cashEur:0.##} must keep reserve EUR {positionSizing.CashReserveEur:0.##} and exposure EUR {currentExposureEur:0.##} must stay within max EUR {risk.MaxTotalExposureEur:0.##}");
        }

        var reserveReason = target < selectedTarget
            ? $"; reduced from EUR {selectedTarget:0.##} to keep cash reserve EUR {positionSizing.CashReserveEur:0.##} and max exposure EUR {risk.MaxTotalExposureEur:0.##}"
            : string.Empty;
        var strongEmaGapReason = strongByEmaGap
            ? $"; EMA gap {signal.BullishEmaGapPercent:0.###}% reached strong threshold {positionSizing.StrongEmaGapPercent:0.###}%"
            : string.Empty;

        return new PositionSizeSelection(
            target,
            $"score {signal.Score:0.##} selected target EUR {target:0.##} (tiers {positionSizing.SmallOrderEur:0.##}/{positionSizing.BaseOrderEur:0.##}/{positionSizing.StrongOrderEur:0.##}/{positionSizing.VeryStrongOrderEur:0.##}, max EUR {effectiveMaxOrder:0.##}){strongEmaGapReason}{reserveReason}");
    }

    private static IEnumerable<decimal> PositionSizeTiers(PositionSizingOptions sizing)
    {
        yield return sizing.SmallOrderEur;
        yield return sizing.BaseOrderEur;
        yield return sizing.StrongOrderEur;
        yield return sizing.VeryStrongOrderEur;
    }
}

internal sealed class RiskManager
{
    public RiskEvaluation Evaluate(DecisionProposal proposal, RiskOptions risk, bool hasOpenPosition = false)
    {
        var reasons = new List<string>();

        if (risk.KillSwitch)
        {
            reasons.Add("kill switch is active");
            return new RiskEvaluation(false, reasons);
        }

        if (proposal.DesiredPosition == "NONE")
        {
            reasons.Add("no position requested");
            return new RiskEvaluation(true, reasons);
        }

        // A held pair whose signal is still LONG carries a zero target on purpose
        // (new-entry sizing is skipped for held positions). That is a HOLD, not an
        // order request — rejecting it here would pollute the journal with
        // riskApproved=false on perfectly healthy positions.
        if (hasOpenPosition)
        {
            reasons.Add("holding existing position; exit rules govern this pair");
            return new RiskEvaluation(true, reasons);
        }

        if (proposal.TargetNotionalEur <= 0m)
        {
            reasons.Add("target notional is zero");
            return new RiskEvaluation(false, reasons);
        }

        if (proposal.TargetNotionalEur > risk.MaxOrderEur)
        {
            reasons.Add($"target EUR {proposal.TargetNotionalEur:0.##} exceeds max order EUR {risk.MaxOrderEur:0.##}");
            return new RiskEvaluation(false, reasons);
        }

        reasons.Add($"target EUR {proposal.TargetNotionalEur:0.##} is within max order EUR {risk.MaxOrderEur:0.##}");
        reasons.Add($"daily loss cap configured at EUR {risk.MaxDailyLossEur:0.##}");
        reasons.Add($"max open positions configured at {risk.MaxOpenPositions}");
        if (risk.MaxTotalExposureEur > 0m)
        {
            reasons.Add($"max total exposure configured at EUR {risk.MaxTotalExposureEur:0.##}");
        }
        return new RiskEvaluation(true, reasons);
    }
}
