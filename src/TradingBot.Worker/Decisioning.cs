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
        var entryDesired = signal.AllowsLong && signal.Score >= strategy.MinimumLongScore ? "LONG_MICRO" : "NONE";
        var desiredPosition = entryDesired;
        var targetNotional = 0m;
        var contributions = signal.Contributions.ToList();

        if (hasOpenPosition)
        {
            // Exit hysteresis: a held position keeps its LONG desire unless a CONFIRMED
            // bearish cross appears. A merely weak (non-bearish) signal is a HOLD, not a
            // flip. This governs only the desired-position handoff to the exit policy;
            // hard exits (stop-loss / take-profit / trailing / max-hold / kill switch)
            // are evaluated independently downstream.
            var held = EvaluateHeldDesire(indicators, strategy, entryDesired);
            desiredPosition = held.Desired;
            contributions.Add(held.Note);
            if (desiredPosition == "LONG_MICRO")
            {
                // A held position needs no new-entry sizing; a capacity-driven zero target
                // must never masquerade as a signal flip.
                contributions.Add(new SignalContribution("PositionSizing", 0m, "holding existing position; new-entry sizing skipped"));
            }
        }
        else if (entryDesired == "LONG_MICRO")
        {
            // Friction guard for NEW entries only: if the current bid/ask spread alone
            // is wide, the round-trip cost (fees + slippage + spread) likely exceeds any
            // realistic edge, so we skip the entry. Held positions bypass this so exits
            // are never blocked by a temporarily wide book.
            var spreadPercent = EntrySpreadPercent(marketState);
            if (strategy.MaxEntrySpreadPercent > 0m && spreadPercent > strategy.MaxEntrySpreadPercent)
            {
                desiredPosition = "NONE";
                contributions.Add(new SignalContribution("Friction", 0m, $"entry skipped: spread {spreadPercent:0.###}% exceeds max {strategy.MaxEntrySpreadPercent:0.###}%"));
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

    private static TechnicalSignal EvaluateSignal(
        InstrumentMarketState marketState,
        IndicatorSnapshot indicators,
        StrategyOptions strategy)
    {
        var contributions = new List<SignalContribution>();
        decimal score = 0.30m;
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

        if (indicators.Rsi is { } rsi)
        {
            if (rsi >= strategy.RsiIdealMin && rsi <= strategy.RsiIdealMax)
            {
                score += 0.15m;
                contributions.Add(new SignalContribution("RSI", 0.15m, $"RSI {rsi:0.##} is in the ideal long band {strategy.RsiIdealMin:0.#}-{strategy.RsiIdealMax:0.#}"));
            }
            else if ((rsi >= 35m && rsi < strategy.RsiIdealMin) || (rsi > strategy.RsiIdealMax && rsi <= 68m))
            {
                score += 0.05m;
                contributions.Add(new SignalContribution("RSI", 0.05m, $"RSI {rsi:0.##} is acceptable but outside the ideal band"));
            }
            else if (rsi < 30m)
            {
                score += 0.08m;
                contributions.Add(new SignalContribution("RSI", 0.08m, $"RSI {rsi:0.##} is oversold"));
            }
            else if (rsi > 72m)
            {
                score -= 0.25m;
                contributions.Add(new SignalContribution("RSI", -0.25m, $"RSI {rsi:0.##} is overheated"));
            }
            else
            {
                contributions.Add(new SignalContribution("RSI", 0m, $"RSI {rsi:0.##} is neutral"));
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

        // Confirmation bonuses only apply on top of an allowed bullish trend. They exist
        // to create real score dispersion: an ordinary "EMA up + RSI ok + calm" signal
        // lands at 0.80 and must earn at least one confirmation to reach the 0.85 entry
        // bar, while a genuinely strong setup (momentum + volume + trend) can reach ~1.00.
        if (allowsLong)
        {
            if (TryMomentumPercent(marketState.Candles, strategy.MomentumLookbackBars, out var momentumPercent)
                && momentumPercent >= strategy.MomentumMinPercent)
            {
                score += 0.10m;
                contributions.Add(new SignalContribution("Momentum", 0.10m, $"price up {momentumPercent:0.##}% over last {strategy.MomentumLookbackBars} candles"));
            }
            else
            {
                contributions.Add(new SignalContribution("Momentum", 0m, $"no confirmed momentum over last {strategy.MomentumLookbackBars} candles"));
            }

            if (VolumeConfirmed(marketState.Candles, strategy.VolumeConfirmationMultiple))
            {
                score += 0.05m;
                contributions.Add(new SignalContribution("Volume", 0.05m, $"last candle volume above {strategy.VolumeConfirmationMultiple:0.##}x recent average"));
            }
            else
            {
                contributions.Add(new SignalContribution("Volume", 0m, "no volume confirmation"));
            }

            if (TrendAligned(marketState.Candles, strategy.TrendFilterMaPeriod))
            {
                score += 0.05m;
                contributions.Add(new SignalContribution("Trend", 0.05m, $"price above {strategy.TrendFilterMaPeriod}-period trend filter"));
            }
            else
            {
                contributions.Add(new SignalContribution("Trend", 0m, $"price not above {strategy.TrendFilterMaPeriod}-period trend filter"));
            }
        }

        score = Math.Clamp(score, 0m, 1m);
        var direction = score >= 0.55m ? "LONG_BIAS" : "NEUTRAL";
        return new TechnicalSignal(decimal.Round(score, 2), direction, allowsLong, bullishEmaGapPercent, contributions);
    }

    private static decimal? CalculateEmaGapPercent(decimal fastEma, decimal slowEma) =>
        slowEma == 0m ? null : Math.Abs(fastEma - slowEma) / slowEma * 100m;

    private static bool EmaGapPassesFilter(decimal emaGapPercent, decimal minimumEmaGapPercent) =>
        minimumEmaGapPercent <= 0m || emaGapPercent >= minimumEmaGapPercent;

    private static decimal EntrySpreadPercent(InstrumentMarketState marketState)
    {
        var bid = marketState.BestBid;
        var ask = marketState.BestAsk;
        if (bid <= 0m || ask <= 0m || ask < bid)
        {
            return 0m;
        }

        var mid = (bid + ask) / 2m;
        return mid == 0m ? 0m : (ask - bid) / mid * 100m;
    }

    private static bool TryMomentumPercent(IReadOnlyList<Candle> candles, int lookbackBars, out decimal changePercent)
    {
        changePercent = 0m;
        if (lookbackBars < 1 || candles.Count <= lookbackBars)
        {
            return false;
        }

        var last = candles[^1].Close;
        var prior = candles[^(lookbackBars + 1)].Close;
        if (prior <= 0m)
        {
            return false;
        }

        changePercent = (last - prior) / prior * 100m;
        return true;
    }

    private static bool VolumeConfirmed(IReadOnlyList<Candle> candles, decimal multiple)
    {
        if (multiple <= 0m || candles.Count < 6)
        {
            return false;
        }

        var window = Math.Min(20, candles.Count - 1);
        var priorAverage = candles
            .Skip(candles.Count - 1 - window)
            .Take(window)
            .Average(candle => candle.Volume);
        return priorAverage > 0m && candles[^1].Volume >= multiple * priorAverage;
    }

    private static bool TrendAligned(IReadOnlyList<Candle> candles, int period)
    {
        if (period < 2 || candles.Count < period)
        {
            return false;
        }

        var sma = candles.Skip(candles.Count - period).Take(period).Average(candle => candle.Close);
        return candles[^1].Close > sma;
    }

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
