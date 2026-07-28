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
    public void Relative_strength_gate_ships_disabled()
    {
        // The whole point of this rollout: measurement only, no behaviour change until
        // the recorded data justifies switching the veto on.
        Assert.False(new FuturesRegimeOptions().RelativeStrengthGateEnabled);
        Assert.Equal(0.5m, new FuturesRegimeOptions().MinRelativeStrengthPct);
    }
}
