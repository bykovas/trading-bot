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
            // TODO(futures): SignalScorer does not derive ShortCandidate yet; a
            // bearish scoring path (inverse EMA structure, overheated RSI) must be
            // designed and replay-tested before shorts trade even in dry-run.
            SignalIntent.ShortCandidate when config.Futures.AllowShorts => FuturesDesiredExposure.Short,
            _ => FuturesDesiredExposure.Flat
        };
    }

    // Held positions: keep exposure while the signal does not confirm a reversal.
    // Hard exits (TP/SL) are owned by TpSlOrchestrator, not the strategy.
    public FuturesDesiredExposure DecideHeld(PortfolioPosition position, TechnicalSignal signal)
    {
        var intent = SignalScorer.IntentOf(signal, config.Strategy);
        return position.Side == "SHORT"
            ? intent == SignalIntent.LongCandidate ? FuturesDesiredExposure.Flat : FuturesDesiredExposure.Short
            : intent == SignalIntent.ShortCandidate ? FuturesDesiredExposure.Flat : FuturesDesiredExposure.Long;
    }
}
