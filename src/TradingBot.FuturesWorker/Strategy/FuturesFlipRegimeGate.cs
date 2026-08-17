namespace TradingBot.FuturesWorker;

// The flipped-entry experiment is only useful as a countertrend trade. A fully
// approved LONG remains a LONG when either the pair is already in a strong daily
// rise or BTC is rising; blindly selling those regimes produced the live loss
// cluster that this gate is designed to separate.
internal static class FuturesFlipRegimeGate
{
    public static FlipLongEntryDecision Evaluate(
        FuturesDesiredExposure desired,
        FuturesOptions options,
        decimal? pair24hChangePct,
        decimal? btc24hChangePct)
    {
        if (!options.FlipLongEntries || desired != FuturesDesiredExposure.Long)
        {
            return new FlipLongEntryDecision(false, false, pair24hChangePct, btc24hChangePct, "flip not requested");
        }

        var blockers = new List<string>();
        if (pair24hChangePct is null)
        {
            blockers.Add("pair 24h closed-candle change unavailable");
        }
        else if (pair24hChangePct > options.FlipMaxPair24hRisePercent)
        {
            blockers.Add(
                $"pair 24h change {pair24hChangePct:0.###}% exceeds {options.FlipMaxPair24hRisePercent:0.###}%");
        }

        if (btc24hChangePct is null)
        {
            blockers.Add("BTC 24h closed-candle change unavailable");
        }
        else if (btc24hChangePct > options.FlipMaxBtc24hRisePercent)
        {
            blockers.Add(
                $"BTC 24h change {btc24hChangePct:0.###}% exceeds {options.FlipMaxBtc24hRisePercent:0.###}%");
        }

        if (blockers.Count > 0)
        {
            return new FlipLongEntryDecision(
                true,
                false,
                pair24hChangePct,
                btc24hChangePct,
                $"original LONG preserved: {string.Join("; ", blockers)}");
        }

        return new FlipLongEntryDecision(
            true,
            true,
            pair24hChangePct,
            btc24hChangePct,
            "countertrend flip allowed: BTC is not rising and the pair is not in a strong 24h rise");
    }

    public static decimal? CalculateClosedCandle24hChangePct(
        IReadOnlyList<Candle> candles,
        int timeframeMinutes)
    {
        if (timeframeMinutes <= 0)
        {
            return null;
        }

        var requiredBars = (int)decimal.Ceiling(24m * 60m / timeframeMinutes);
        if (requiredBars < 1 || candles.Count < requiredBars)
        {
            return null;
        }

        var window = candles.TakeLast(requiredBars).ToList();
        var reference = window[0].Open;
        var close = window[^1].Close;
        return reference <= 0m || close <= 0m
            ? null
            : decimal.Round((close - reference) / reference * 100m, 6);
    }
}

internal sealed record FlipLongEntryDecision(
    bool Requested,
    bool ApplyFlip,
    decimal? Pair24hChangePct,
    decimal? Btc24hChangePct,
    string Reason);
