namespace TradingBot.Core.Common;

// Venue-neutral outcome of technical scoring. Core deliberately stops at
// intent: spot maps LongCandidate to its persisted "LONG_MICRO" desired
// position, futures maps intents to Flat/Long/Short exposure. Core never
// emits BUY/SELL and ShortCandidate is never acted on by the spot worker.
public enum SignalIntent
{
    None,
    LongCandidate,
    ShortCandidate
}
