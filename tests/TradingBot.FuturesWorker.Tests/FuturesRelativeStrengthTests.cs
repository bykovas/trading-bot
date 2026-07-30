using Xunit;

namespace TradingBot.FuturesWorker.Tests;

// Relative strength versus BTC: the pair's own momentum minus BTC's over the shared
// candle lookback. It is measured on every decision; the veto that uses it ships
// DISABLED, so these tests pin both the arithmetic and the default-off contract.
public sealed class FuturesRelativeStrengthTests
{
    private static BtcRegimeState Regime(decimal? btcChangePct, bool allowsLongs = false) =>
        new(allowsLongs, false, !allowsLongs, "test regime", btcChangePct);

    private static EntryFreshnessResult Freshness(decimal? pairMomentumPct) =>
        new(
            PositionIn24hRangePct: 20m,
            DistanceFromRecentHighPct: 1m,
            LastSnapshotStepPct: 0.1m,
            ShortSnapshotSlopePct: 0.2m,
            PositiveStepsInLast3: 2,
            IsNearHigh: false,
            HasFreshUpwardTape: true,
            HasFreshBreakout: false,
            Blocked: false,
            BlockReason: null,
            RecentCandleMomentumPct: pairMomentumPct);

    [Fact]
    public void Relative_strength_is_pair_momentum_minus_btc_momentum()
    {
        // The scalp case: the pair is up 0.8% over the lookback while BTC is down 0.4%.
        Assert.Equal(1.2m, FuturesDecisionWorker.RelativeStrengthPct(Freshness(0.8m), Regime(-0.4m)));

        // Drifting with a market-wide selloff: both down, barely any outperformance.
        Assert.Equal(0.1m, FuturesDecisionWorker.RelativeStrengthPct(Freshness(-0.3m), Regime(-0.4m)));
    }

    [Fact]
    public void Relative_strength_is_null_when_either_side_is_unavailable()
    {
        Assert.Null(FuturesDecisionWorker.RelativeStrengthPct(Freshness(0.8m), Regime(null)));
        Assert.Null(FuturesDecisionWorker.RelativeStrengthPct(Freshness(null), Regime(-0.4m)));
        Assert.Null(FuturesDecisionWorker.RelativeStrengthPct(null, Regime(-0.4m)));
    }

    [Fact]
    public void Relative_strength_gate_code_default_is_off_as_a_safe_fallback()
    {
        // appsettings turns the gate ON; the code default stays OFF so a config that
        // omits the section can never silently start vetoing entries.
        Assert.False(new FuturesRegimeOptions().RelativeStrengthGateEnabled);
        Assert.Equal(0.5m, new FuturesRegimeOptions().MinRelativeStrengthPct);
    }

    [Fact]
    public void Short_entry_threshold_matches_the_scorer_and_survives_normalize()
    {
        // The short score is structurally capped at 0.80 in a real downtrend: the base
        // bearish-EMA credit plus calm volatility, downside momentum and price-below-trend
        // reach 0.80, while both RSI bonuses require an OVERHEATED RSI that a falling
        // market does not produce. A gate above 0.80 is therefore unreachable, which is
        // why zero shorts fired across 14k+ bearish candidates. The scorer already admits
        // shorts at MinimumLongScore (0.80), so the entry gate must not sit above it.
        var config = new FuturesBotConfiguration
        {
            Strategy = new StrategyOptions { MinimumLongScore = 0.80m },
            Shorts = new FuturesShortOptions { MinShortScore = 0.80m }
        };
        InvokeNormalize(config);

        Assert.Equal(0.80m, config.Shorts.MinShortScore);
        Assert.True(config.Shorts.MinShortScore <= config.Strategy.MinimumLongScore,
            "a short entry gate above the scorer's own admission bar is unreachable");
    }

    private static void InvokeNormalize(FuturesBotConfiguration config)
    {
        var method = typeof(FuturesBotConfiguration).GetMethod(
            "Normalize",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(config, null);
    }
}
