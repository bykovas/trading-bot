namespace TradingBot.FuturesWorker;

// Maps Core's venue-neutral SignalIntent onto futures exposure. Core never says
// BUY/SELL; this is the only place intent becomes a futures exposure request.
internal sealed class LongShortStrategy(FuturesBotConfiguration config)
{
    public FuturesDesiredExposure DecideEntry(TechnicalSignal signal)
    {
        var intent = SignalScorer.IntentOf(signal, config.Strategy);
        return intent switch
        {
            SignalIntent.LongCandidate => FuturesDesiredExposure.Long,
            // ShortCandidate is futures-only; spot ignores it. The futures worker
            // still applies AllowShorts, margin, funding, no-flip, and TP/SL gates.
            SignalIntent.ShortCandidate when config.Futures.AllowShorts => FuturesDesiredExposure.Short,
            _ => FuturesDesiredExposure.Flat
        };
    }

    // Held positions: keep exposure while the signal does not confirm a reversal.
    // Hard exits (TP/SL) are owned by TpSlOrchestrator, not the strategy.
    public FuturesDesiredExposure DecideHeld(PortfolioPosition position, TechnicalSignal signal)
    {
        var intent = SignalScorer.IntentOf(signal, config.Strategy);

        // Flipped-logic short: it exists BECAUSE the LONG thesis fired, so the
        // reversal exit inverts with it — the position is held while the long
        // signal persists and closes when the original long would have closed
        // (a ShortCandidate). Without this the very signal that opened the
        // position requests its close on the next cycle.
        if (position.FlippedEntry && position.Side == "SHORT")
        {
            return intent == SignalIntent.ShortCandidate ? FuturesDesiredExposure.Flat : FuturesDesiredExposure.Short;
        }

        return position.Side == "SHORT"
            ? intent == SignalIntent.LongCandidate ? FuturesDesiredExposure.Flat : FuturesDesiredExposure.Short
            : intent == SignalIntent.ShortCandidate ? FuturesDesiredExposure.Flat : FuturesDesiredExposure.Long;
    }
}
