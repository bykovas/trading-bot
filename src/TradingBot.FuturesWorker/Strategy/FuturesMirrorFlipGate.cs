namespace TradingBot.FuturesWorker;

// Whether a mirrored entry is copied as it is or turned around, decided per trade from
// the 24h BTC regime rather than from a switch that is either always on or always off.
//
// This is NOT the same rule as FuturesFlipRegimeGate. That one guards the bot's own
// signals, only ever turns a LONG into a SHORT, and also asks about the pair's own 24h
// rise. This one is symmetric - it turns LONG into SHORT and SHORT into LONG alike -
// because a mirror copies both directions, and inverting only half of them would make
// the follower neither a copy of the source nor its opposite.
//
// InvertSide is a permission, not a command: with it off nothing is ever turned around
// and this gate is not consulted at all, so the follower behaves exactly as before.
internal static class FuturesMirrorFlipGate
{
    public static MirrorFlipDecision Evaluate(
        FuturesEntryMirrorOptions options,
        decimal? btc24hChangePct)
    {
        if (!options.InvertSide)
        {
            return new MirrorFlipDecision(false, false, btc24hChangePct, "mirror inversion disabled");
        }

        // No reading means no countertrend claim. Copying the source is the conservative
        // outcome: it is what the follower does every day the flip is switched off.
        if (btc24hChangePct is null)
        {
            return new MirrorFlipDecision(
                true, false, null, "BTC 24h change unavailable: copied as it is");
        }

        if (btc24hChangePct > options.InvertMaxBtc24hRisePercent)
        {
            return new MirrorFlipDecision(
                true,
                false,
                btc24hChangePct,
                $"BTC 24h change {btc24hChangePct:0.###}% exceeds {options.InvertMaxBtc24hRisePercent:0.###}%: copied as it is");
        }

        return new MirrorFlipDecision(
            true,
            true,
            btc24hChangePct,
            $"countertrend copy: BTC 24h change {btc24hChangePct:0.###}% at or below {options.InvertMaxBtc24hRisePercent:0.###}%");
    }
}

internal sealed record MirrorFlipDecision(
    bool Permitted,
    bool Invert,
    decimal? Btc24hChangePct,
    string Reason);
