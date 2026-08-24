namespace TradingBot.FuturesWorker;

// The own-strategy experiment of 2026-08-24: futures-live trades its own signals with
// two subtractions, futures-lukas-live stays the unmodified control. Both rules were
// trained on 2025 and held up on 2026, which the base strategy did not:
//
//              2025 (train)   2026 (held out)
//   base          +0.012%        -0.118%
//   this gate     +0.119%        -0.055%      per trade, after 0.1% round-trip cost
//
// What it subtracts, and only subtracts - the gate never invents an entry:
//   - LONG entries whose channel is on the disabled list. Continuation longs ("kyla be
//     sustojimo - einu kartu") averaged -0.184%/trade across 10.6k held-out entries,
//     the worst class the strategy has, and also its most frequent one.
//   - SHORT entries while BTC's 24h change is above the configured ceiling. Shorting
//     into a rising BTC averaged -0.245%/trade held out. A missing BTC reading allows
//     the entry: the validated rule was "BTC demonstrably up", not "BTC unknown".
//
// Both knobs default OFF, so the control account is untouched by this class existing.
internal static class FuturesEntryExperimentGate
{
    public static string? Block(
        FuturesDesiredExposure desired,
        string entryChannel,
        FuturesOptions futures,
        FuturesShortOptions shorts,
        decimal? btc24hChangePct)
    {
        if (desired == FuturesDesiredExposure.Long
            && futures.DisabledLongEntryChannels.Contains(entryChannel, StringComparer.OrdinalIgnoreCase))
        {
            return $"EXPERIMENT_CHANNEL_DISABLED: long {entryChannel} entries are switched off "
                + "(held-out 2026: -0.184%/trade over this channel)";
        }

        if (desired == FuturesDesiredExposure.Short
            && shorts.MaxBtc24hRisePercentForShort is { } ceiling
            && btc24hChangePct is { } btc
            && btc > ceiling)
        {
            return $"EXPERIMENT_SHORT_BTC_RISING: BTC 24h {btc:0.###}% exceeds {ceiling:0.###}% "
                + "(held-out 2026: shorting a rising BTC averaged -0.245%/trade)";
        }

        return null;
    }
}
