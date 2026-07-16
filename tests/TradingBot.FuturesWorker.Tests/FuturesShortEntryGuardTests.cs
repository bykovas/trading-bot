using TradingBot.Core.Signals;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

// SHORT context/anti-knife gate — the mirror of FuturesLongRangeGuardTests. Wick
// mid-range is diagnostic only (reclaim-down after wide spikes must pass); a rising
// knife, a short glued to a local low, or an unconfirmed top still blocks.
public sealed class FuturesShortEntryGuardTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-07-16T03:31:00Z");

    private static FuturesFreshnessOptions Freshness() => new();
    private static FuturesShortOptions Shorts() => new();

    [Fact]
    public void Fresh_downward_tape_below_the_high_passes()
    {
        var market = DecliningMarket(bid: 100m, localLow: 99.5m);
        var result = FuturesShortEntryGuard.Evaluate(
            market,
            FallingTape(100.3m, 100.15m, 100m),
            FuturesDesiredExposure.Short,
            Shorts(),
            Freshness());

        Assert.True(result.Evaluated);
        Assert.False(result.Blocked);
        Assert.Null(result.BlockReasonCode);
        Assert.True(result.FreshTape);
        Assert.Equal(FuturesShortEntryGuard.RangeBasisClosePercentile, result.RangeBasis);
    }

    [Fact]
    public void Reclaim_down_after_wide_wick_spikes_is_not_blocked_by_mid_24h_range()
    {
        // Value ~100 with a wild 10–150 wick range and a fresh down-tape reclaim to 100.
        // Wick position is mid-range — must NOT veto solely for that.
        var candles = new List<Candle>();
        for (var i = 0; i < 30; i++)
        {
            candles.Add(Candle(100m, high: 101m, low: 99m, i));
        }

        candles.Add(Candle(50m, high: 100m, low: 50m, 30));
        candles.Add(Candle(80m, high: 150m, low: 10m, 31));
        candles.Add(Candle(120m, high: 150m, low: 10m, 32));
        // Recent bars roll gently over (negative momentum), lows well under the bid.
        candles.Add(Candle(100.4m, high: 101m, low: 99.6m, 33));
        candles.Add(Candle(100.3m, high: 101m, low: 99.6m, 34));
        candles.Add(Candle(100.2m, high: 101m, low: 99.6m, 35));
        candles.Add(Candle(100.1m, high: 101m, low: 99.6m, 36));
        candles.Add(Candle(100m, high: 101m, low: 99.6m, 37));

        var market = new InstrumentMarketState
        {
            Instrument = new InstrumentOptions { Pair = "TEST/USD", KrakenPair = "PF_TESTUSD", Enabled = true },
            Candles = candles,
            Quote = new Quote(100m, 100.02m, 100m, 1_000_000m, MarkPrice: 100m)
        };

        var result = FuturesShortEntryGuard.Evaluate(
            market,
            FallingTape(100.3m, 100.15m, 100m),
            FuturesDesiredExposure.Short,
            Shorts(),
            Freshness());

        Assert.True(result.Evaluated);
        Assert.False(result.Blocked);
        Assert.NotNull(result.ClosePercentile);
    }

    [Fact]
    public void Entry_near_local_low_without_breakdown_is_blocked()
    {
        var market = DecliningMarket(bid: 100m, localLow: 99.98m);
        var result = FuturesShortEntryGuard.Evaluate(
            market,
            FallingTape(100.3m, 100.15m, 100m),
            FuturesDesiredExposure.Short,
            Shorts(),
            Freshness());

        Assert.True(result.Blocked);
        Assert.Equal(FuturesShortEntryGuard.EntryTooCloseToLocalLow, result.BlockReasonCode);
    }

    [Fact]
    public void Rising_tape_is_blocked_as_falling_snapshots_not_confirmed()
    {
        var market = DecliningMarket(bid: 100m, localLow: 99.5m);
        var result = FuturesShortEntryGuard.Evaluate(
            market,
            FallingTape(99.8m, 99.9m, 100m),
            FuturesDesiredExposure.Short,
            Shorts(),
            Freshness());

        Assert.True(result.Blocked);
        Assert.Equal(FuturesShortEntryGuard.FallingSnapshotsNotConfirmed, result.BlockReasonCode);
    }

    [Fact]
    public void Short_at_the_top_without_pullback_is_blocked()
    {
        // Tight range: high barely above the bid -> pullback from the 24h high < min.
        var candles = new List<Candle>();
        for (var i = 0; i < 30; i++)
        {
            candles.Add(Candle(100m, high: 100.05m, low: 99.99m, i));
        }

        var market = new InstrumentMarketState
        {
            Instrument = new InstrumentOptions { Pair = "TEST/USD", KrakenPair = "PF_TESTUSD", Enabled = true },
            Candles = candles,
            Quote = new Quote(100m, 100.02m, 100m, 1_000_000m, MarkPrice: 100m)
        };

        var result = FuturesShortEntryGuard.Evaluate(
            market,
            FallingTape(100.3m, 100.15m, 100m),
            FuturesDesiredExposure.Short,
            Shorts(),
            Freshness());

        Assert.True(result.Blocked);
        Assert.Equal(FuturesShortEntryGuard.PullbackTooSmall, result.BlockReasonCode);
    }

    [Fact]
    public void Not_evaluated_for_long_or_when_disabled()
    {
        var market = DecliningMarket(bid: 100m, localLow: 99.5m);
        var forLong = FuturesShortEntryGuard.Evaluate(
            market, FallingTape(100.3m, 100.15m, 100m),
            FuturesDesiredExposure.Long, Shorts(), Freshness());
        Assert.False(forLong.Evaluated);

        var disabled = FuturesShortEntryGuard.Evaluate(
            market, FallingTape(100.3m, 100.15m, 100m),
            FuturesDesiredExposure.Short, new FuturesShortOptions { RangeGuardEnabled = false }, Freshness());
        Assert.False(disabled.Evaluated);
    }

    // Value ~100 with a 150 wick high (large pullback), recent bars declining, and the
    // final two candle lows placed relative to the bid so the local-low proximity can be
    // toggled per test. Bid == signal close so drift stays inside the threshold.
    private static InstrumentMarketState DecliningMarket(decimal bid, decimal localLow)
    {
        var candles = new List<Candle>();
        for (var i = 0; i < 30; i++)
        {
            candles.Add(Candle(100m, high: 101m, low: 99m, i));
        }

        candles.Add(Candle(120m, high: 150m, low: 10m, 30));
        candles.Add(Candle(100.5m, high: 101m, low: 99.6m, 31));
        candles.Add(Candle(100.4m, high: 101m, low: 99.6m, 32));
        candles.Add(Candle(100.3m, high: 101m, low: 99.6m, 33));
        candles.Add(Candle(100.2m, high: 101m, low: localLow, 34));
        candles.Add(Candle(bid, high: 101m, low: localLow, 35));

        return new InstrumentMarketState
        {
            Instrument = new InstrumentOptions { Pair = "TEST/USD", KrakenPair = "PF_TESTUSD", Enabled = true },
            Candles = candles,
            Quote = new Quote(bid, bid + 0.02m, bid, 1_000_000m, MarkPrice: bid)
        };
    }

    private static IReadOnlyList<PriceObservation> FallingTape(params decimal[] lasts) =>
        lasts.Select((value, index) => new PriceObservation(T.AddSeconds(index * 15), value, value - 0.01m, value + 0.01m)).ToList();

    private static Candle Candle(decimal close, decimal high, decimal low, int index) =>
        new(T.AddMinutes(15 * index), close, high, low, close, 1000m, 1);
}
