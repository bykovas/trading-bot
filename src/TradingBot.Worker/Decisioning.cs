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
        bool hasOpenPosition = false,
        PriceActionAssessment? priceAction = null)
    {
        var signal = EvaluateSignal(marketState, indicators, strategy, priceAction, includeEarlyEntryDiagnostics: !hasOpenPosition);
        var entryDesired = signal.AllowsLong && signal.Score >= strategy.MinimumLongScore ? "LONG_MICRO" : "NONE";
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
        var gate = EntryGate.Evaluate(signal, marketState, priceAction, strategy, trading.LiveTradingEnabled);
        contributions.AddRange(gate.Notes);
        var desiredPosition = gate.DesiredPosition;
        var rejectionReason = gate.RejectionReason;
        var targetNotional = 0m;

        if (desiredPosition == "LONG_MICRO")
        {
            var size = SelectPositionSize(signal, trading, positionSizing, risk, cashEur, currentExposureEur);
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
                : 0m);
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
        StrategyOptions strategy,
        PriceActionAssessment? priceAction = null,
        bool includeEarlyEntryDiagnostics = true)
    {
        var contributions = new List<SignalContribution>();
        decimal score = 0.30m;
        var allowsLong = false;
        var hasBullishStructure = false;
        var emaFullyConfirmed = false;
        decimal? bullishEmaGapPercent = null;
        decimal? emaGapVelocityPercent = CalculateEmaGapVelocityPercent(marketState.Candles, strategy);

        if (indicators.FastEma is not null && indicators.SlowEma is not null)
        {
            var emaGapPercent = CalculateEmaGapPercent(indicators.FastEma.Value, indicators.SlowEma.Value);
            if (emaGapPercent is null)
            {
                contributions.Add(new SignalContribution("EMA", 0m, "slow EMA is zero; cannot calculate EMA gap"));
            }
            else if (indicators.FastEma > indicators.SlowEma)
            {
                bullishEmaGapPercent = emaGapPercent.Value;
                hasBullishStructure = HasEarlyBullishStructure(emaGapPercent.Value, strategy.MinimumEmaGapPercent);
                if (EmaGapPassesFilter(emaGapPercent.Value, strategy.MinimumEmaGapPercent))
                {
                    score += 0.30m;
                    allowsLong = true;
                    emaFullyConfirmed = true;
                    contributions.Add(new SignalContribution("EMA", 0.30m, $"fast EMA is above slow EMA by {emaGapPercent.Value:0.###}%"));
                }
                else
                {
                    var partial = includeEarlyEntryDiagnostics
                        ? SmoothBullishEmaContribution(emaGapPercent.Value, strategy.MinimumEmaGapPercent)
                        : 0m;
                    score += partial;
                    contributions.Add(new SignalContribution(
                        "EMA",
                        decimal.Round(partial, 2),
                        partial > 0m
                            ? $"fast EMA is above slow EMA by {emaGapPercent.Value:0.###}% but below configured minimum {strategy.MinimumEmaGapPercent:0.###}%; partial early-structure credit"
                            : $"EMA crossover ignored because gap {emaGapPercent.Value:0.000}% < configured minimum {strategy.MinimumEmaGapPercent:0.000}%"));
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
        var volumeConfirmed = false;
        if (allowsLong || (includeEarlyEntryDiagnostics && hasBullishStructure))
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

            volumeConfirmed = VolumeConfirmed(marketState.Candles, strategy.VolumeConfirmationMultiple);
            if (volumeConfirmed)
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

            // Anti-lag score adjustment: EMA/momentum/trend all read CANDLES, which lag.
            // When the light-snapshot ticker series says the price is already falling,
            // the bullish score loses credit so a stale breakout cannot look pristine.
            if (priceAction is { DataSufficient: true })
            {
                if (priceAction.TrendPercent < 0m && strategy.NegativePriceActionPenalty > 0m)
                {
                    score -= strategy.NegativePriceActionPenalty;
                    contributions.Add(new SignalContribution(
                        "PriceAction",
                        -strategy.NegativePriceActionPenalty,
                        $"recent snapshot price action is negative ({priceAction.TrendPercent:0.###}% over last {strategy.PriceActionLookbackSnapshots} snapshots)"));
                }
                else
                {
                    contributions.Add(new SignalContribution(
                        "PriceAction",
                        0m,
                        $"recent snapshot price action {priceAction.TrendPercent:0.###}% ({priceAction.Direction})"));
                }
            }
        }

        if (emaGapVelocityPercent is { } velocity)
        {
            contributions.Add(new SignalContribution(
                "EmaGapVelocity",
                0m,
                $"EMA gap velocity diagnostic {velocity:+0.###;-0.###;0}% versus previous candle; not used for trading"));
        }

        score = Math.Clamp(score, 0m, 1m);
        var uncappedScore = decimal.Round(score, 2);

        // Volume confirmation is not optional for high-confidence entries: without it
        // the final score is capped below the firm entry bar, so a lagging-indicator
        // stack (EMA + RSI + momentum) alone can never mint a 0.95 "sure thing".
        if (allowsLong && !volumeConfirmed && strategy.MissingVolumeScoreCap > 0m && uncappedScore > strategy.MissingVolumeScoreCap)
        {
            score = strategy.MissingVolumeScoreCap;
            contributions.Add(new SignalContribution(
                "VolumeCap",
                decimal.Round(score - uncappedScore, 2),
                $"score capped at {strategy.MissingVolumeScoreCap:0.##} (from {uncappedScore:0.##}) because volume confirmation is missing"));
        }

        var direction = score >= 0.55m ? "LONG_BIAS" : "NEUTRAL";
        return new TechnicalSignal(
            decimal.Round(score, 2),
            direction,
            allowsLong,
            hasBullishStructure,
            emaFullyConfirmed,
            bullishEmaGapPercent,
            emaGapVelocityPercent,
            contributions,
            uncappedScore,
            volumeConfirmed);
    }

    private static bool HasEarlyBullishStructure(decimal emaGapPercent, decimal minimumEmaGapPercent)
    {
        if (EmaGapPassesFilter(emaGapPercent, minimumEmaGapPercent))
        {
            return true;
        }

        var earlyFloor = minimumEmaGapPercent <= 0m
            ? 0m
            : Math.Min(0.10m, minimumEmaGapPercent);
        return emaGapPercent >= earlyFloor;
    }

    private static decimal SmoothBullishEmaContribution(decimal emaGapPercent, decimal minimumEmaGapPercent)
    {
        if (minimumEmaGapPercent <= 0m || emaGapPercent >= minimumEmaGapPercent)
        {
            return 0.30m;
        }

        var floor = Math.Min(0.10m, minimumEmaGapPercent);
        if (emaGapPercent < floor)
        {
            return 0m;
        }

        var range = minimumEmaGapPercent - floor;
        if (range <= 0m)
        {
            return 0m;
        }

        var ratio = (emaGapPercent - floor) / range;
        return decimal.Round(Math.Clamp(ratio, 0m, 1m) * 0.30m, 4);
    }

    private static decimal? CalculateEmaGapVelocityPercent(IReadOnlyList<Candle> candles, StrategyOptions strategy)
    {
        if (candles.Count <= Math.Max(strategy.FastEmaPeriod, strategy.SlowEmaPeriod))
        {
            return null;
        }

        var current = CalculateEmaGapPercentForCloses(candles.Select(candle => candle.Close).ToList(), strategy);
        var previous = CalculateEmaGapPercentForCloses(candles.Take(candles.Count - 1).Select(candle => candle.Close).ToList(), strategy);
        return current is { } currentGap && previous is { } previousGap
            ? decimal.Round(currentGap - previousGap, 3)
            : null;
    }

    private static decimal? CalculateEmaGapPercentForCloses(IReadOnlyList<decimal> closes, StrategyOptions strategy)
    {
        var fast = CalculateLatestEma(closes, strategy.FastEmaPeriod);
        var slow = CalculateLatestEma(closes, strategy.SlowEmaPeriod);
        return fast is { } fastEma && slow is { } slowEma && slowEma != 0m
            ? (fastEma - slowEma) / slowEma * 100m
            : null;
    }

    private static decimal? CalculateLatestEma(IReadOnlyList<decimal> values, int period)
    {
        if (period <= 1 || values.Count < period)
        {
            return null;
        }

        var ema = values.Take(period).Average();
        var multiplier = 2m / (period + 1);
        for (var i = period; i < values.Count; i++)
        {
            ema = (values[i] - ema) * multiplier + ema;
        }

        return decimal.Round(ema, 6);
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
        && (HasPositiveContribution(signal, "Momentum") || signal.VolumeConfirmed);

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

        if (!HasPositiveContribution(signal, "Momentum") && !signal.VolumeConfirmed)
        {
            return "not early-eligible: needs momentum or volume confirmation";
        }

        return $"early-entry diagnostic only: partial EMA structure score {signal.Score:0.##}, positive price action and confirmation present; normal gate still blocks trading";
    }

    private static bool HasPositiveContribution(TechnicalSignal signal, string name) =>
        signal.Contributions.Any(contribution =>
            contribution.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && contribution.Value > 0m);

    private static decimal? CalculateEmaGapPercent(decimal fastEma, decimal slowEma) =>
        slowEma == 0m ? null : Math.Abs(fastEma - slowEma) / slowEma * 100m;

    private static bool EmaGapPassesFilter(decimal emaGapPercent, decimal minimumEmaGapPercent) =>
        minimumEmaGapPercent <= 0m || emaGapPercent >= minimumEmaGapPercent;

    private static decimal EntrySpreadPercent(InstrumentMarketState marketState) =>
        EntryGate.SpreadPercentOf(marketState);

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
