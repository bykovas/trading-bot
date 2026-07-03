namespace TradingBot.Worker;

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
        bool hasOpenPosition = false)
    {
        var signal = EvaluateSignal(marketState, indicators, strategy);
        var desiredPosition = signal.AllowsLong && signal.Score >= strategy.MinimumLongScore ? "LONG_MICRO" : "NONE";
        var targetNotional = 0m;
        var contributions = signal.Contributions.ToList();
        if (desiredPosition == "LONG_MICRO")
        {
            if (hasOpenPosition)
            {
                // A held position needs no new-entry sizing. Critically, a capacity-driven
                // zero target must NOT collapse the desired state to NONE: when cash
                // reserve / max exposure leave no room for NEW entries, the signal for
                // the held pair is still LONG, and flipping to NONE here would masquerade
                // as a signal flip and push the position into the exit path.
                contributions.Add(new SignalContribution("PositionSizing", 0m, "holding existing position; new-entry sizing skipped"));
            }
            else
            {
                var size = SelectPositionSize(signal, trading, positionSizing, risk, cashEur, currentExposureEur);
                targetNotional = size.TargetNotionalEur;
                contributions.Add(new SignalContribution("PositionSizing", 0m, size.Reason));
                if (targetNotional <= 0m)
                {
                    desiredPosition = "NONE";
                }
            }
        }

        return new DecisionProposal(
            marketState.Instrument.Pair,
            desiredPosition,
            signal.Score,
            targetNotional,
            contributions);
    }

    private static TechnicalSignal EvaluateSignal(
        InstrumentMarketState marketState,
        IndicatorSnapshot indicators,
        StrategyOptions strategy)
    {
        var contributions = new List<SignalContribution>();
        decimal score = 0.35m;
        var allowsLong = false;
        decimal? bullishEmaGapPercent = null;

        if (indicators.FastEma is not null && indicators.SlowEma is not null)
        {
            var emaGapPercent = CalculateEmaGapPercent(indicators.FastEma.Value, indicators.SlowEma.Value);
            if (emaGapPercent is null)
            {
                contributions.Add(new SignalContribution("EMA", 0m, "slow EMA is zero; cannot calculate EMA gap"));
            }
            else if (indicators.FastEma > indicators.SlowEma)
            {
                if (EmaGapPassesFilter(emaGapPercent.Value, strategy.MinimumEmaGapPercent))
                {
                    score += 0.30m;
                    allowsLong = true;
                    bullishEmaGapPercent = emaGapPercent.Value;
                    contributions.Add(new SignalContribution("EMA", 0.30m, $"fast EMA is above slow EMA by {emaGapPercent.Value:0.###}%"));
                }
                else
                {
                    contributions.Add(new SignalContribution("EMA", 0m, $"EMA crossover ignored because gap {emaGapPercent.Value:0.000}% < configured minimum {strategy.MinimumEmaGapPercent:0.000}%"));
                }
            }
            else if (indicators.FastEma < indicators.SlowEma)
            {
                if (EmaGapPassesFilter(emaGapPercent.Value, strategy.MinimumEmaGapPercent))
                {
                    score -= 0.25m;
                    contributions.Add(new SignalContribution("EMA", -0.25m, $"fast EMA is below slow EMA by {emaGapPercent.Value:0.###}%"));
                }
                else
                {
                    contributions.Add(new SignalContribution("EMA", 0m, $"EMA crossover ignored because gap {emaGapPercent.Value:0.000}% < configured minimum {strategy.MinimumEmaGapPercent:0.000}%"));
                }
            }
            else
            {
                contributions.Add(new SignalContribution("EMA", 0m, "fast EMA equals slow EMA"));
            }
        }
        else
        {
            contributions.Add(new SignalContribution("EMA", 0m, "not enough candles for EMA"));
        }

        if (indicators.Rsi is not null)
        {
            if (indicators.Rsi is >= 35m and <= 68m)
            {
                score += 0.15m;
                contributions.Add(new SignalContribution("RSI", 0.15m, $"RSI {indicators.Rsi:0.##} is in the acceptable range"));
            }
            else if (indicators.Rsi < 30m)
            {
                score += 0.08m;
                contributions.Add(new SignalContribution("RSI", 0.08m, $"RSI {indicators.Rsi:0.##} is oversold"));
            }
            else if (indicators.Rsi > 75m)
            {
                score -= 0.25m;
                contributions.Add(new SignalContribution("RSI", -0.25m, $"RSI {indicators.Rsi:0.##} is overheated"));
            }
            else
            {
                contributions.Add(new SignalContribution("RSI", 0m, $"RSI {indicators.Rsi:0.##} is neutral"));
            }
        }
        else
        {
            contributions.Add(new SignalContribution("RSI", 0m, "not enough candles for RSI"));
        }

        if (marketState.VolatilityPercent <= 1.2m)
        {
            score += 0.05m;
            contributions.Add(new SignalContribution("Volatility", 0.05m, $"short-term volatility {marketState.VolatilityPercent:0.##}% is controlled"));
        }
        else
        {
            score -= 0.10m;
            contributions.Add(new SignalContribution("Volatility", -0.10m, $"short-term volatility {marketState.VolatilityPercent:0.##}% is elevated"));
        }

        score = Math.Clamp(score, 0m, 1m);
        var direction = score >= 0.55m ? "LONG_BIAS" : "NEUTRAL";
        return new TechnicalSignal(decimal.Round(score, 2), direction, allowsLong, bullishEmaGapPercent, contributions);
    }

    private static decimal? CalculateEmaGapPercent(decimal fastEma, decimal slowEma) =>
        slowEma == 0m ? null : Math.Abs(fastEma - slowEma) / slowEma * 100m;

    private static bool EmaGapPassesFilter(decimal emaGapPercent, decimal minimumEmaGapPercent) =>
        minimumEmaGapPercent <= 0m || emaGapPercent >= minimumEmaGapPercent;

    private static PositionSizeSelection SelectPositionSize(
        TechnicalSignal signal,
        TradingOptions trading,
        PositionSizingOptions positionSizing,
        RiskOptions risk,
        decimal cashEur,
        decimal currentExposureEur)
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

        var target = veryStrongByScore
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
