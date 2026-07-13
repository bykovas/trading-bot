using System.Reflection;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

// Part 1: margin-based sizing. TargetMarginEur is the initial margin; the position
// notional is derived once as margin * leverage. Quantity converts the EUR notional
// to the instrument's USD quote currency via UsdPerEur before dividing by price.
public sealed class FuturesSizingTests
{
    private static FuturesBotConfiguration Config(decimal targetMarginEur, decimal leverage, decimal usdPerEur = 1m) => new()
    {
        Futures = new FuturesOptions
        {
            TargetMarginEur = targetMarginEur,
            DefaultLeverage = leverage,
            MaxLeverage = 10m,
            UsdPerEur = usdPerEur
        },
        Portfolio = new FuturesPortfolioOptions { StartingCashEur = 100m },
        Fees = new FuturesFeesOptions { MakerPct = 0m, TakerPct = 0m },
        TpSl = new TpSlOptions { Enabled = true, TakeProfitPercent = 3m, StopLossPercent = 2m }
    };

    private sealed class NullStore : IDryRunPortfolioStore
    {
        public string StateDescription => "null";
        public string EventsDescription => "null";
        public PortfolioState? Load() => null;
        public void Save(PortfolioState state) { }
        public void AppendCycle(DryRunCycleRecord record) { }
        public void AppendMarketSnapshots(IReadOnlyList<MarketSnapshotRecord> snapshots) { }
        public IReadOnlyList<MarketSnapshotRecord> LoadRecentMarketSnapshots(DateTimeOffset sinceUtc) => Array.Empty<MarketSnapshotRecord>();
    }

    private static (FuturesVirtualPortfolio Portfolio, PortfolioState State, FuturesBotConfiguration Config) Setup(
        decimal targetMarginEur, decimal leverage, decimal usdPerEur = 1m, decimal startingCash = 100m)
    {
        var config = Config(targetMarginEur, leverage, usdPerEur);
        config.Portfolio.StartingCashEur = startingCash;
        var portfolio = new FuturesVirtualPortfolio(config, new NullStore());
        return (portfolio, portfolio.Load(), config);
    }

    [Fact]
    public void Margin_10_leverage_10_derives_100_notional_and_10_margin()
    {
        var (portfolio, state, config) = Setup(targetMarginEur: 10m, leverage: 10m);
        Assert.Equal(100m, config.Futures.DerivedNotionalEur(10m));

        var fill = portfolio.Apply(state, "RIVER/USD", FuturesDesiredExposure.Long, 3.509m,
            config.Futures.DerivedNotionalEur(config.Futures.DefaultLeverage), config.Futures.DefaultLeverage);

        var position = Assert.Single(state.Positions);
        Assert.Equal(100m, position.EntryNotionalEur);
        Assert.Equal(10m, position.InitialMarginEur);
        Assert.Equal(10m, fill.Action.RequestedMarginEur);
        Assert.Equal(10m, fill.Action.ActualEffectiveLeverage); // 100 / 10
    }

    [Fact]
    public void Margin_10_leverage_2_derives_20_notional_and_10_margin()
    {
        var (portfolio, state, config) = Setup(targetMarginEur: 10m, leverage: 2m);
        Assert.Equal(20m, config.Futures.DerivedNotionalEur(2m));

        portfolio.Apply(state, "XBT/USD", FuturesDesiredExposure.Long, 100m,
            config.Futures.DerivedNotionalEur(config.Futures.DefaultLeverage), config.Futures.DefaultLeverage);

        var position = Assert.Single(state.Positions);
        Assert.Equal(20m, position.EntryNotionalEur);
        Assert.Equal(10m, position.InitialMarginEur);
    }

    [Fact]
    public void Quantity_applies_eur_usd_conversion()
    {
        // RIVER example: 100 EUR notional, USD/EUR 1.08, price 3.509
        // quantity = 100 * 1.08 / 3.509 = 30.78 RIVER (~10x the old 2.8).
        var (portfolio, state, config) = Setup(targetMarginEur: 10m, leverage: 10m, usdPerEur: 1.08m);

        var fill = portfolio.Apply(state, "RIVER/USD", FuturesDesiredExposure.Long, 3.509m,
            config.Futures.DerivedNotionalEur(config.Futures.DefaultLeverage), config.Futures.DefaultLeverage);

        var expected = 100m * 1.08m / 3.509m;
        Assert.Equal(decimal.Round(expected, 6), decimal.Round(fill.Action.Quantity, 6));
        Assert.True(fill.Action.Quantity > 28m && fill.Action.Quantity < 32m);
        // ~11x the old 2.8 RIVER quantity.
        Assert.True(fill.Action.Quantity / 2.8m > 9m);
        // The EUR margin is unchanged by FX.
        Assert.Equal(10m, Assert.Single(state.Positions).InitialMarginEur);
    }

    [Fact]
    public void Insufficient_collateral_is_rejected()
    {
        // Margin needed is 10 EUR but only 5 EUR collateral is available.
        var (portfolio, state, config) = Setup(targetMarginEur: 10m, leverage: 10m, startingCash: 5m);

        var fill = portfolio.Apply(state, "RIVER/USD", FuturesDesiredExposure.Long, 3.509m,
            config.Futures.DerivedNotionalEur(config.Futures.DefaultLeverage), config.Futures.DefaultLeverage);

        Assert.False(fill.PositionOpened);
        Assert.Empty(state.Positions);
        Assert.Contains("insufficient margin", fill.Action.Reason);
    }

    [Fact]
    public void Normalize_recomputes_risk_caps_for_notional_semantics()
    {
        var config = new FuturesBotConfiguration
        {
            Futures = new FuturesOptions { TargetMarginEur = 10m, DefaultLeverage = 10m, MaxLeverage = 10m, MaxPositions = 3 },
            TpSl = new TpSlOptions { Enabled = true, StopLossPercent = 2m }
        };
        InvokeNormalize(config);

        // Derived notional = 100. Per-group exposure defaults to one notional.
        Assert.Equal(100m, config.CorrelationRisk.MaxExposureEurPerGroup);
        // Open-risk cap must clear one position's stop-out loss (100 * 2% = 2 EUR);
        // the old 1.5 default would block every entry, so it is recomputed.
        Assert.True(config.Risk.MaxConcurrentOpenRisk >= 2m);
    }

    [Fact]
    public void Legacy_target_notional_migrates_to_margin_preserving_exposure()
    {
        var config = new FuturesBotConfiguration
        {
            Futures = new FuturesOptions { TargetMarginEur = 0m, TargetNotionalEur = 10m, DefaultLeverage = 10m, MaxLeverage = 10m }
        };
        InvokeNormalize(config);

        // Legacy notional 10 at 10x migrates to margin 1 so the OLD 10-notional
        // exposure is preserved (not silently blown up to 100).
        Assert.Equal(1m, config.Futures.TargetMarginEur);
        Assert.Equal(10m, config.Futures.DerivedNotionalEur(10m));
        Assert.Null(config.Futures.TargetNotionalEur);
    }

    private static void InvokeNormalize(FuturesBotConfiguration config)
    {
        var method = typeof(FuturesBotConfiguration).GetMethod("Normalize", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(config, null);
    }
}
