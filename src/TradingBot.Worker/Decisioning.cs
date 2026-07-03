namespace TradingBot.Worker;

internal sealed class TechnicalDecisionEngine
{
    public DecisionProposal Decide(
        InstrumentMarketState marketState,
        IndicatorSnapshot indicators,
        TradingOptions trading,
        StrategyOptions strategy)
    {
        var signal = EvaluateSignal(marketState, indicators, strategy);
        var desiredPosition = signal.AllowsLong && signal.Score >= strategy.MinimumLongScore ? "LONG_MICRO" : "NONE";
        var targetNotional = desiredPosition == "LONG_MICRO" ? trading.TargetOrderEur : 0m;

        return new DecisionProposal(
            marketState.Instrument.Pair,
            desiredPosition,
            signal.Score,
            targetNotional,
            signal.Contributions);
    }

    private static TechnicalSignal EvaluateSignal(
        InstrumentMarketState marketState,
        IndicatorSnapshot indicators,
        StrategyOptions strategy)
    {
        var contributions = new List<SignalContribution>();
        decimal score = 0.35m;
        var allowsLong = false;

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
        return new TechnicalSignal(decimal.Round(score, 2), direction, allowsLong, contributions);
    }

    private static decimal? CalculateEmaGapPercent(decimal fastEma, decimal slowEma) =>
        slowEma == 0m ? null : Math.Abs(fastEma - slowEma) / slowEma * 100m;

    private static bool EmaGapPassesFilter(decimal emaGapPercent, decimal minimumEmaGapPercent) =>
        minimumEmaGapPercent <= 0m || emaGapPercent >= minimumEmaGapPercent;
}

internal sealed class RiskManager
{
    public RiskEvaluation Evaluate(DecisionProposal proposal, RiskOptions risk)
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
        return new RiskEvaluation(true, reasons);
    }
}
