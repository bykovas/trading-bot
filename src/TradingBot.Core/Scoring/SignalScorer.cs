namespace TradingBot.Core.Scoring;

// Pure technical scoring shared by workers. This class only produces a
// TechnicalSignal and a venue-neutral SignalIntent; it never emits BUY/SELL
// and never sizes an order - workers translate intent into their own
// desired-position vocabulary.
public static class SignalScorer
{
    public static TechnicalSignal Evaluate(
        InstrumentMarketState marketState,
        IndicatorSnapshot indicators,
        StrategyOptions strategy,
        PriceActionAssessment? priceAction = null,
        bool includeEarlyEntryDiagnostics = true)
    {
        var contributions = new List<SignalContribution>();
        decimal score = 0.30m;
        var allowsLong = false;
        var bearishEmaConfirmed = false;
        var hasBullishStructure = false;
        var hasBearishStructure = false;
        var emaFullyConfirmed = false;
        decimal? bullishEmaGapPercent = null;
        decimal? bearishEmaGapPercent = null;
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
                bearishEmaGapPercent = emaGapPercent.Value;
                hasBearishStructure = HasEarlyBullishStructure(emaGapPercent.Value, strategy.MinimumEmaGapPercent);
                if (EmaGapPassesFilter(emaGapPercent.Value, strategy.MinimumEmaGapPercent))
                {
                    bearishEmaConfirmed = true;
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

        var shortScore = bearishEmaConfirmed ? 0.60m : 0.30m;
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
                if (bearishEmaConfirmed)
                {
                    shortScore += 0.15m;
                }

                contributions.Add(new SignalContribution("RSI", -0.25m, $"RSI {rsi:0.##} is overheated"));
            }
            else if (bearishEmaConfirmed && rsi > strategy.RsiIdealMax)
            {
                shortScore += 0.05m;
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
            if (bearishEmaConfirmed)
            {
                shortScore += 0.05m;
            }

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
        var shortVolumeConfirmed = false;
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
                // A mild pullback (trend between -threshold and 0) is a normal entry
                // point, not weakness; only a genuinely falling series is penalized.
                if (priceAction.TrendPercent < -strategy.NegativePriceActionPenaltyThresholdPercent && strategy.NegativePriceActionPenalty > 0m)
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

        if (bearishEmaConfirmed)
        {
            if (TryMomentumPercent(marketState.Candles, strategy.MomentumLookbackBars, out var momentumPercent)
                && momentumPercent <= -strategy.MomentumMinPercent)
            {
                shortScore += 0.10m;
                contributions.Add(new SignalContribution("ShortMomentum", 0.10m, $"price down {Math.Abs(momentumPercent):0.##}% over last {strategy.MomentumLookbackBars} candles"));
            }
            else
            {
                contributions.Add(new SignalContribution("ShortMomentum", 0m, $"no confirmed downside momentum over last {strategy.MomentumLookbackBars} candles"));
            }

            shortVolumeConfirmed = VolumeConfirmed(marketState.Candles, strategy.VolumeConfirmationMultiple);
            if (shortVolumeConfirmed)
            {
                shortScore += 0.05m;
                contributions.Add(new SignalContribution("ShortVolume", 0.05m, $"last candle volume above {strategy.VolumeConfirmationMultiple:0.##}x recent average"));
            }
            else
            {
                contributions.Add(new SignalContribution("ShortVolume", 0m, "no downside volume confirmation"));
            }

            if (TrendBelow(marketState.Candles, strategy.TrendFilterMaPeriod))
            {
                shortScore += 0.05m;
                contributions.Add(new SignalContribution("ShortTrend", 0.05m, $"price below {strategy.TrendFilterMaPeriod}-period trend filter"));
            }
            else
            {
                contributions.Add(new SignalContribution("ShortTrend", 0m, $"price not below {strategy.TrendFilterMaPeriod}-period trend filter"));
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
        shortScore = Math.Clamp(shortScore, 0m, 1m);
        var uncappedScore = decimal.Round(score, 2);
        var roundedShortScore = decimal.Round(shortScore, 2);

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

        var allowsShort = bearishEmaConfirmed
            && roundedShortScore >= strategy.MinimumLongScore
            && (shortVolumeConfirmed || HasPositiveContribution(contributions, "ShortMomentum") || HasPositiveContribution(contributions, "ShortTrend"));
        if (bearishEmaConfirmed)
        {
            contributions.Add(new SignalContribution(
                "ShortScore",
                0m,
                $"short diagnostic score {roundedShortScore:0.##}; requires bearish EMA plus downside confirmation"));
        }

        var direction = allowsShort ? "SHORT_BIAS" : score >= 0.55m ? "LONG_BIAS" : "NEUTRAL";
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
            volumeConfirmed,
            allowsShort,
            hasBearishStructure,
            bearishEmaGapPercent,
            roundedShortScore);
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

        // NOT rounded. decimal.Round(ema, 6) collapsed EMA9 and EMA21 onto the same value
        // for anything priced below ~0.0001 - SHIB at 0.00000502, PEPE at 0.0000033,
        // BONK at 0.0000027 - so the gap read exactly 0%, AllowsLong and the bearish
        // branch could never turn on, and those symbols were untradeable in both
        // directions with no log line saying so. A price that did cross a sixth-decimal
        // boundary reported a nonsense gap of exactly 25%. The gap is a ratio; rounding
        // the inputs to a fixed number of decimals buys nothing and destroys small prices.
        return ema;
    }

    public static bool HasPositiveContribution(TechnicalSignal signal, string name) =>
        signal.Contributions.Any(contribution =>
            contribution.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && contribution.Value > 0m);

    private static bool HasPositiveContribution(IReadOnlyList<SignalContribution> contributions, string name) =>
        contributions.Any(contribution =>
            contribution.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && contribution.Value > 0m);

    public static decimal? CalculateEmaGapPercent(decimal fastEma, decimal slowEma) =>
        slowEma == 0m ? null : Math.Abs(fastEma - slowEma) / slowEma * 100m;

    public static bool EmaGapPassesFilter(decimal emaGapPercent, decimal minimumEmaGapPercent) =>
        minimumEmaGapPercent <= 0m || emaGapPercent >= minimumEmaGapPercent;

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

    private static bool TrendBelow(IReadOnlyList<Candle> candles, int period)
    {
        if (period < 2 || candles.Count < period)
        {
            return false;
        }

        var sma = candles.Skip(candles.Count - period).Take(period).Average(candle => candle.Close);
        return candles[^1].Close < sma;
    }

    public static SignalIntent IntentOf(TechnicalSignal signal, StrategyOptions strategy) =>
        signal.AllowsLong && signal.Score >= strategy.MinimumLongScore
            ? SignalIntent.LongCandidate
            : signal.AllowsShort
                ? SignalIntent.ShortCandidate
                : SignalIntent.None;
}
