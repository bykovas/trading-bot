namespace TradingBot.Core.Indicators;

public sealed class IndicatorEngine
{
    public IndicatorSnapshot Calculate(IReadOnlyList<Candle> candles, StrategyOptions strategy)
    {
        var closes = candles.Select(candle => candle.Close).ToList();
        return new IndicatorSnapshot(
            CalculateLatestEma(closes, strategy.FastEmaPeriod),
            CalculateLatestEma(closes, strategy.SlowEmaPeriod),
            CalculateLatestRsi(closes, strategy.RsiPeriod));
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

    private static decimal? CalculateLatestRsi(IReadOnlyList<decimal> values, int period)
    {
        if (period <= 1 || values.Count <= period)
        {
            return null;
        }

        decimal gain = 0m;
        decimal loss = 0m;
        for (var i = 1; i <= period; i++)
        {
            var change = values[i] - values[i - 1];
            if (change >= 0)
            {
                gain += change;
            }
            else
            {
                loss -= change;
            }
        }

        var averageGain = gain / period;
        var averageLoss = loss / period;

        for (var i = period + 1; i < values.Count; i++)
        {
            var change = values[i] - values[i - 1];
            var currentGain = change > 0 ? change : 0m;
            var currentLoss = change < 0 ? -change : 0m;
            averageGain = (averageGain * (period - 1) + currentGain) / period;
            averageLoss = (averageLoss * (period - 1) + currentLoss) / period;
        }

        if (averageLoss == 0m)
        {
            return 100m;
        }

        var relativeStrength = averageGain / averageLoss;
        var rsi = 100m - 100m / (1m + relativeStrength);
        return decimal.Round(rsi, 2);
    }
}
