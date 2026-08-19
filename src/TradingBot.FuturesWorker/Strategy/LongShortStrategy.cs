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
        var heldExposure = position.Side == "SHORT"
            ? FuturesDesiredExposure.Short
            : FuturesDesiredExposure.Long;

        // Once exchange trailing protection is armed, Kraken owns the exit. A
        // strategy reversal must not cut the winner or cancel the trailing order.
        if (position.TrailingStopState?.Equals("EXCHANGE_OPEN", StringComparison.OrdinalIgnoreCase) == true)
        {
            return heldExposure;
        }

        // A mirrored position intentionally opposes the source account. The local
        // scoring stream is therefore not an exit signal for either mirror side;
        // price-based TP/SL/trailing protection owns its lifecycle.
        if (position.EntryChannel?.Equals("Mirror", StringComparison.OrdinalIgnoreCase) == true)
        {
            return heldExposure;
        }

        var intent = SignalScorer.IntentOf(signal, config.Strategy);

        // A flipped short deliberately trades against an approved LONG entry.
        // Neither a persisting LONG nor a later ShortCandidate is a meaningful
        // reversal for that experiment, so price-based TP/SL/trailing owns exit.
        if (position.FlippedEntry && position.Side == "SHORT")
        {
            return FuturesDesiredExposure.Short;
        }

        return position.Side == "SHORT"
            ? intent == SignalIntent.LongCandidate ? FuturesDesiredExposure.Flat : FuturesDesiredExposure.Short
            : intent == SignalIntent.ShortCandidate ? FuturesDesiredExposure.Flat : FuturesDesiredExposure.Long;
    }
}
