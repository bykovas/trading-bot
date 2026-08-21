using System.Reflection;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

// Futures accounting is USD-native: margin, notional, fees, and PnL all share USD.
public sealed class FuturesSizingTests
{
    private static FuturesBotConfiguration Config(decimal targetMarginUsd, decimal leverage) => new()
    {
        Futures = new FuturesOptions
        {
            TargetMarginUsd = targetMarginUsd,
            DefaultLeverage = leverage,
            MaxLeverage = 10m
        },
        Portfolio = new FuturesPortfolioOptions { StartingCashUsd = 100m },
        Fees = new FuturesFeesOptions { MakerPct = 0m, TakerPct = 0m },
        TpSl = new TpSlOptions { Enabled = true, TakeProfitPercent = 3m, StopLossPercent = 2m }
    };

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

    private static (FuturesVirtualPortfolio Portfolio, PortfolioState State, FuturesBotConfiguration Config) Setup(
        decimal targetMarginUsd, decimal leverage, decimal startingCash = 100m)
    {
        var config = Config(targetMarginUsd, leverage);
        config.Portfolio.StartingCashUsd = startingCash;
        var portfolio = new FuturesVirtualPortfolio(config, new NullStore());
        return (portfolio, portfolio.Load(), config);
    }

    [Fact]
    public void Margin_10_leverage_10_derives_100_notional_and_10_margin()
    {
        var (portfolio, state, config) = Setup(targetMarginUsd: 10m, leverage: 10m);
        Assert.Equal(100m, config.Futures.DerivedNotionalUsd(10m));

        var fill = portfolio.Apply(state, "RIVER/USD", FuturesDesiredExposure.Long, 3.509m,
            config.Futures.DerivedNotionalUsd(config.Futures.DefaultLeverage), config.Futures.DefaultLeverage);

        var position = Assert.Single(state.Positions);
        Assert.Equal(100m, position.EntryNotionalEur);
        Assert.Equal(10m, position.InitialMarginEur);
        Assert.Equal(10m, fill.Action.RequestedMarginEur);
        Assert.Equal(10m, fill.Action.ActualEffectiveLeverage); // 100 / 10
    }

    [Fact]
    public void Margin_10_leverage_2_derives_20_notional_and_10_margin()
    {
        var (portfolio, state, config) = Setup(targetMarginUsd: 10m, leverage: 2m);
        Assert.Equal(20m, config.Futures.DerivedNotionalUsd(2m));

        portfolio.Apply(state, "XBT/USD", FuturesDesiredExposure.Long, 100m,
            config.Futures.DerivedNotionalUsd(config.Futures.DefaultLeverage), config.Futures.DefaultLeverage);

        var position = Assert.Single(state.Positions);
        Assert.Equal(20m, position.EntryNotionalEur);
        Assert.Equal(10m, position.InitialMarginEur);
    }

    [Fact]
    public void Margin_15_leverage_4_derives_60_usd_notional()
    {
        var (portfolio, state, config) = Setup(targetMarginUsd: 15m, leverage: 4m);

        var fill = portfolio.Apply(state, "ATOM/USD", FuturesDesiredExposure.Short, 1.50m,
            config.Futures.DerivedNotionalUsd(config.Futures.DefaultLeverage), config.Futures.DefaultLeverage);

        Assert.Equal(60m, config.Futures.DerivedNotionalUsd(4m));
        Assert.Equal(40m, fill.Action.Quantity);
        Assert.Equal(60m, Assert.Single(state.Positions).EntryNotionalEur);
        Assert.Equal(15m, Assert.Single(state.Positions).InitialMarginEur);
    }

    [Fact]
    public void Insufficient_collateral_is_rejected()
    {
        // Margin needed is 10 USD but only 5 USD collateral is available.
        var (portfolio, state, config) = Setup(targetMarginUsd: 10m, leverage: 10m, startingCash: 5m);

        var fill = portfolio.Apply(state, "RIVER/USD", FuturesDesiredExposure.Long, 3.509m,
            config.Futures.DerivedNotionalUsd(config.Futures.DefaultLeverage), config.Futures.DefaultLeverage);

        Assert.False(fill.PositionOpened);
        Assert.Empty(state.Positions);
        Assert.Contains("insufficient margin", fill.Action.Reason);
    }

    [Fact]
    public void Normalize_recomputes_risk_caps_for_target_risk_semantics()
    {
        var config = new FuturesBotConfiguration
        {
            Futures = new FuturesOptions { TargetMarginUsd = 40m, DefaultLeverage = 10m, MaxLeverage = 10m, MaxPositions = 3 },
            Risk = new FuturesRiskOptions { TargetRiskUsd = 3m, MaxConcurrentOpenRiskUsd = 1.5m },
            TpSl = new TpSlOptions { Enabled = true, StopLossPercent = 0.75m, TakeProfitPercent = 1.5m }
        };
        InvokeNormalize(config);

        Assert.Equal(400m, config.Futures.DerivedNotionalUsd(10m));
        Assert.Equal(400m, config.CorrelationRisk.MaxExposureUsdPerGroup);
        Assert.Equal(3m, config.Risk.TargetRiskUsd);
        Assert.Equal(9m, config.Risk.MaxConcurrentOpenRiskUsd);
    }

    [Fact]
    public void Legacy_target_notional_migrates_to_margin_preserving_exposure()
    {
        var config = new FuturesBotConfiguration
        {
            Futures = new FuturesOptions { TargetMarginUsd = 0m, TargetNotionalUsd = 10m, DefaultLeverage = 10m, MaxLeverage = 10m }
        };
        InvokeNormalize(config);

        // Legacy notional 10 at 10x migrates to margin 1 so the OLD 10-notional
        // exposure is preserved (not silently blown up to 100).
        Assert.Equal(1m, config.Futures.TargetMarginUsd);
        Assert.Equal(10m, config.Futures.DerivedNotionalUsd(10m));
        Assert.Null(config.Futures.TargetNotionalUsd);
    }

    private static void InvokeNormalize(FuturesBotConfiguration config)
    {
        var method = typeof(FuturesBotConfiguration).GetMethod("Normalize", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(config, null);
    }
}
