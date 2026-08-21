using System.Reflection;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

// Dip-bounce channel: config normalization and the entry-channel tag surviving from
// the opened position onto the close action (so realized PnL is attributable to
// Continuation / Breakout / DipBounce in SQL).
public sealed class FuturesDipBounceTests
{
    private sealed class NullStore : IDryRunPortfolioStore
    {
        public IReadOnlySet<string> LoadRecordedExchangeOrderIds(string botInstanceId, DateTimeOffset sinceUtc) =>
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public string StateDescription => "null";
        public string EventsDescription => "null";
        public PortfolioState? Load() => null;
        public void Save(PortfolioState state) { }
        public void AppendCycle(DryRunCycleRecord record) { }
        public void AppendMarketSnapshots(IReadOnlyList<MarketSnapshotRecord> snapshots) { }
        public IReadOnlyList<MarketSnapshotRecord> LoadRecentMarketSnapshots(DateTimeOffset sinceUtc) => Array.Empty<MarketSnapshotRecord>();
        public void SaveCashEvents(IReadOnlyList<PortfolioCashEvent> events) { }
    }

        public IReadOnlySet<string> LoadRecordedExchangeOrderIds(string botInstanceId, DateTimeOffset sinceUtc) =>
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Dip_bounce_options_normalize_to_sane_defaults()
    {
        var low = new FuturesBotConfiguration
        {
            Dip = new FuturesDipBounceOptions { NearLowMax24hRangePositionPct = 0m, MinScore = 0m, MinCandleMomentumPct = -1m }
        };
        InvokeNormalize(low);
        Assert.Equal(25m, low.Dip.NearLowMax24hRangePositionPct);
        Assert.Equal(0.70m, low.Dip.MinScore);
        Assert.Equal(0m, low.Dip.MinCandleMomentumPct); // negative floors clamp to 0
        Assert.True(low.Dip.Enabled); // enabled by default

        var over = new FuturesBotConfiguration
        {
            Dip = new FuturesDipBounceOptions { NearLowMax24hRangePositionPct = 250m, MinScore = 2m }
        };
        InvokeNormalize(over);
        Assert.Equal(100m, over.Dip.NearLowMax24hRangePositionPct);
        Assert.Equal(1m, over.Dip.MinScore);
    }

    [Fact]
    public void Entry_channel_is_carried_from_position_to_close_action()
    {
        var config = new FuturesBotConfiguration
        {
            Futures = new FuturesOptions { TargetMarginEur = 40m, DefaultLeverage = 10m, MaxLeverage = 10m },
            Portfolio = new FuturesPortfolioOptions { StartingCashEur = 100m },
            Fees = new FuturesFeesOptions { MakerPct = 0m, TakerPct = 0m },
            TpSl = new TpSlOptions { Enabled = true, TakeProfitPercent = 1.5m, StopLossPercent = 0.75m }
        };
        var portfolio = new FuturesVirtualPortfolio(config, new NullStore());
        var state = portfolio.Load();

        var open = portfolio.Apply(state, "SOL/USD", FuturesDesiredExposure.Long, 100m,
            config.Futures.DerivedNotionalEur(config.Futures.DefaultLeverage), config.Futures.DefaultLeverage);
        Assert.True(open.PositionOpened);

        // The worker stamps the admitting channel on the opened position; simulate it.
        Assert.Single(state.Positions).EntryChannel = "DipBounce";

        var close = portfolio.Apply(state, "SOL/USD", FuturesDesiredExposure.Flat, 101m, 0m, 10m);
        Assert.True(close.PositionClosed);
        Assert.Equal("DipBounce", close.Action.EntryChannel);
    }

    private static void InvokeNormalize(FuturesBotConfiguration config)
    {
        var method = typeof(FuturesBotConfiguration).GetMethod("Normalize", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(config, null);
    }
}
