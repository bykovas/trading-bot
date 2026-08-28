using TradingBot.Core.Common;

namespace TradingBot.FuturesWorker;

// The Reversal strategy's whole signal: did a sharp fast move just happen, and which
// way does the fade point. Nothing else - no score, no EMA, no tape, no range. The
// 45-day onset study behind it found the tape mean-reverting at every move size, the
// hardest after the largest and fastest moves; this class trades that one fact and
// leaves every judgement about affordability to the shared portfolio and risk layers.
//
// A pure function over CLOSED candles so it is deterministic and testable: the worker's
// candle list carries no forming bar (both market-data paths drop it), so the newest
// close is the newest settled price.
internal static class FuturesReversalStrategy
{
    public static FuturesReversalSignal Evaluate(
        IReadOnlyList<Candle> candles,
        int timeframeMinutes,
        FuturesReversalOptions options)
    {
        if (!options.Enabled)
        {
            return FuturesReversalSignal.None("reversal disabled");
        }

        if (timeframeMinutes <= 0)
        {
            return FuturesReversalSignal.None("timeframe unavailable");
        }

        // The window in whole candles, at least one. A 15-minute window on 15m candles
        // is the last closed bar against the close before it.
        var bars = Math.Max(1, options.TriggerWindowMinutes / timeframeMinutes);
        if (candles is null || candles.Count < bars + 1)
        {
            return FuturesReversalSignal.None("not enough closed candles");
        }

        var last = candles[^1].Close;
        var reference = candles[^(bars + 1)].Close;
        if (last <= 0m || reference <= 0m)
        {
            return FuturesReversalSignal.None("price unavailable");
        }

        var movePercent = (last - reference) / reference * 100m;
        if (Math.Abs(movePercent) < options.MinMovePercent)
        {
            return FuturesReversalSignal.None(
                $"move {movePercent:0.###}% inside +-{options.MinMovePercent:0.###}%");
        }

        // Beyond the ceiling the move is read as news or a broken market, not an
        // overreaction: a coin down 40% is not a dip, it is a delisting.
        if (options.MaxMovePercent > 0m && Math.Abs(movePercent) > options.MaxMovePercent)
        {
            return FuturesReversalSignal.None(
                $"move {movePercent:0.###}% beyond the {options.MaxMovePercent:0.###}% sanity cap");
        }

        // Fade the move: a sharp rise is sold, a sharp fall is bought.
        var desired = movePercent > 0m ? FuturesDesiredExposure.Short : FuturesDesiredExposure.Long;
        return new FuturesReversalSignal(
            Fires: true,
            Desired: desired,
            MovePercent: decimal.Round(movePercent, 3),
            WindowBars: bars,
            Reason: $"reversal: {movePercent:+0.###;-0.###}% in {bars * timeframeMinutes} min, fading with a {(desired == FuturesDesiredExposure.Short ? "SHORT" : "LONG")}");
    }
}

internal sealed record FuturesReversalSignal(
    bool Fires,
    FuturesDesiredExposure Desired,
    decimal MovePercent,
    int WindowBars,
    string Reason)
{
    public static FuturesReversalSignal None(string reason) =>
        new(false, FuturesDesiredExposure.Flat, 0m, 0, reason);
}
