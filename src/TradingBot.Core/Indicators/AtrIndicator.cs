namespace TradingBot.Core.Indicators;

public static class AtrIndicator
{
    public static decimal? CalculateLatestClosedAtr(IReadOnlyList<Candle> candles, int period)
    {
        if (period <= 0 || candles.Count < period + 2)
        {
            return null;
        }

        // The newest candle can still be forming in live trading, so ATR uses only
        // completed candles. True Range needs a previous close, hence period + 1
        // closed candles are required for the first Wilder ATR.
        var closed = candles.Take(candles.Count - 1).ToList();
        if (closed.Count < period + 1)
        {
            return null;
        }

        var trueRanges = new List<decimal>(closed.Count - 1);
        for (var i = 1; i < closed.Count; i++)
        {
            var candle = closed[i];
            var previousClose = closed[i - 1].Close;
            var highLow = candle.High - candle.Low;
            var highPreviousClose = Math.Abs(candle.High - previousClose);
            var lowPreviousClose = Math.Abs(candle.Low - previousClose);
            trueRanges.Add(Math.Max(highLow, Math.Max(highPreviousClose, lowPreviousClose)));
        }

        if (trueRanges.Count < period)
        {
            return null;
        }

        var atr = trueRanges.Take(period).Average();
        for (var i = period; i < trueRanges.Count; i++)
        {
            atr = ((atr * (period - 1)) + trueRanges[i]) / period;
        }

        return decimal.Round(atr, 10);
    }
}
