using Skender.Stock.Indicators;

namespace EasyBot.Trading;

public enum Signal
{
    None,
    Long,
    Short
}

/// <summary>
/// Pure EMA-crossover strategy. Takes a closed-candle series and returns the signal produced
/// by the transition between the second-to-last and last candle only (i.e. crossover detection
/// on the most recently closed candle, never on intra-candle/live data).
/// </summary>
public static class Strategy
{
    public static Signal DetectCrossoverSignal(IReadOnlyList<Quote> closedCandles, int emaFastPeriods, int emaSlowPeriods)
    {
        if (closedCandles.Count < emaSlowPeriods + 2)
            return Signal.None;

        var ordered = closedCandles.OrderBy(c => c.Date).ToList();

        var fast = ordered.GetEma(emaFastPeriods).ToList();
        var slow = ordered.GetEma(emaSlowPeriods).ToList();

        var last = ordered.Count - 1;
        var prevFast = fast[last - 1].Ema;
        var prevSlow = slow[last - 1].Ema;
        var currFast = fast[last].Ema;
        var currSlow = slow[last].Ema;

        if (prevFast is null || prevSlow is null || currFast is null || currSlow is null)
            return Signal.None;

        var wasBelow = prevFast <= prevSlow;
        var isAbove = currFast > currSlow;
        if (wasBelow && isAbove)
            return Signal.Long;

        var wasAbove = prevFast >= prevSlow;
        var isBelow = currFast < currSlow;
        if (wasAbove && isBelow)
            return Signal.Short;

        return Signal.None;
    }
}
