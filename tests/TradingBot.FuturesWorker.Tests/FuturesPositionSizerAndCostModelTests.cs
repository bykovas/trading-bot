using System.Reflection;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class FuturesPositionSizerAndCostModelTests
{
    private static FuturesBotConfiguration BaseConfig()
    {
        var config = new FuturesBotConfiguration
        {
            Futures = new FuturesOptions
            {
                TargetMarginEur = 40m,
                DefaultLeverage = 10m,
                MaxLeverage = 10m,
                MaxPositions = 3,
                MaxNotionalEur = 400m
            },
            Portfolio = new FuturesPortfolioOptions { StartingCashEur = 100m },
            Fees = new FuturesFeesOptions { MakerPct = 0.02m, TakerPct = 0.05m },
            Exits = new FuturesExitOptions
            {
                StopAtrMult = 1m,
                MinRewardRiskMultiple = 2m,
                MinTpVsCostMult = 3m,
                StopDistanceCapPct = 3m,
                SlippageBufferPct = 0.10m
            },
            Risk = new FuturesRiskOptions { TargetRiskEur = 3m, MaxConcurrentOpenRisk = 1.5m },
            TpSl = new TpSlOptions { Enabled = true, TakeProfitPercent = 1.5m, StopLossPercent = 0.75m }
        };
        InvokeNormalize(config);
        return config;
    }

    [Fact]
    public void Cost_model_is_taker_fok_round_trip_for_live_and_virtual()
    {
        var config = BaseConfig();
        var costs = FuturesExecutionCostModel.Estimate(config, FuturesDesiredExposure.Long, fundingRatePercent: 0.01m);
        Assert.Equal(FuturesExecutionCostModel.TakerFokRoundTrip, costs.Model);
        // 0.05 + 0.05 + 0.10 + 0.01 funding = 0.21
        Assert.Equal(0.21m, costs.RoundTripCostPct);
        Assert.Equal(0.05m, costs.EntryFeePct);
        Assert.Equal(0.05m, costs.ExitFeePct);
    }

    [Fact]
    public void Same_eur_risk_across_atr_by_changing_notional()
    {
        var config = BaseConfig();
        var costs = FuturesExecutionCostModel.Estimate(config, FuturesDesiredExposure.Long, 0m);

        var calm = FuturesPositionSizer.Size(config, atrPct: 0.50m, costs, leverage: 10m);
        var mid = FuturesPositionSizer.Size(config, atrPct: 1.50m, costs, leverage: 10m);
        var hot = FuturesPositionSizer.Size(config, atrPct: 3.00m, costs, leverage: 10m);

        Assert.Equal(0.75m, calm.StopDistancePct); // floored at TpSl.StopLossPercent
        Assert.Equal(1.50m, mid.StopDistancePct);
        Assert.Equal(3.00m, hot.StopDistancePct); // at cap

        Assert.Equal(3m, calm.TargetRiskEur);
        Assert.Equal(3m, mid.TargetRiskEur);
        Assert.Equal(3m, hot.TargetRiskEur);

        // Projected stop loss EUR ≈ TargetRiskEur (before cost add-on in open-risk).
        Assert.Equal(3m, calm.ProjectedStopLossEur);
        Assert.Equal(3m, mid.ProjectedStopLossEur);
        Assert.Equal(3m, hot.ProjectedStopLossEur);

        Assert.True(calm.SizedNotionalEur > mid.SizedNotionalEur);
        Assert.True(mid.SizedNotionalEur > hot.SizedNotionalEur);
        Assert.Equal(400m, calm.SizedNotionalEur); // 3 / 0.0075 = 400, at max notional
        Assert.Equal(200m, mid.SizedNotionalEur);  // 3 / 0.015
        Assert.Equal(100m, hot.SizedNotionalEur);  // 3 / 0.03
    }

    [Fact]
    public void Volatile_pair_wider_stop_smaller_notional_calm_opposite_capped()
    {
        var config = BaseConfig();
        var costs = FuturesExecutionCostModel.Estimate(config, FuturesDesiredExposure.Long, null);
        var volatilePlan = FuturesPositionSizer.Size(config, 2.5m, costs, 10m);
        var calmPlan = FuturesPositionSizer.Size(config, 0.4m, costs, 10m);

        Assert.True(volatilePlan.StopDistancePct > calmPlan.StopDistancePct);
        Assert.True(volatilePlan.SizedNotionalEur < calmPlan.SizedNotionalEur);
        Assert.True(calmPlan.SizedNotionalEur <= 400m);
        Assert.True(calmPlan.RequiredMarginEur <= 40m);
    }

    [Fact]
    public void Tp_never_below_cost_floor_even_when_tpsl_enabled()
    {
        var config = BaseConfig();
        config.Exits.MinTpVsCostMult = 10m; // force cost floor above R-multiple / legacy TP floor
        config.Exits.MinRewardRiskMultiple = 1m;
        config.TpSl.TakeProfitPercent = 0.5m;
        InvokeNormalize(config);

        var costs = FuturesExecutionCostModel.Estimate(config, FuturesDesiredExposure.Long, 0m);
        // round trip ~0.20 → cost floor = 2.0
        var plan = FuturesPositionSizer.Size(config, atrPct: 0.75m, costs, 10m);
        Assert.True(plan.TakeProfitDistancePct >= costs.RoundTripCostPct * config.Exits.MinTpVsCostMult);
        Assert.True(plan.TakeProfitDistancePct >= 2.0m);
    }

    [Fact]
    public void Margin_notional_leverage_math_is_consistent()
    {
        var config = BaseConfig();
        var costs = FuturesExecutionCostModel.Estimate(config, FuturesDesiredExposure.Long, null);
        var plan = FuturesPositionSizer.Size(config, atrPct: 1.5m, costs, leverage: 10m);
        Assert.Equal(200m, plan.SizedNotionalEur);
        Assert.Equal(20m, plan.RequiredMarginEur);
        Assert.Equal(10m, plan.EffectiveLeverage);
        Assert.Equal(plan.SizedNotionalEur / plan.EffectiveLeverage, plan.RequiredMarginEur);
    }

    [Fact]
    public void Normalize_sets_open_risk_from_target_risk_and_max_positions()
    {
        var config = new FuturesBotConfiguration
        {
            Futures = new FuturesOptions { TargetMarginEur = 40m, DefaultLeverage = 10m, MaxLeverage = 10m, MaxPositions = 3 },
            Risk = new FuturesRiskOptions { TargetRiskEur = 3m, MaxConcurrentOpenRisk = 1.5m },
            TpSl = new TpSlOptions { StopLossPercent = 0.75m, TakeProfitPercent = 1.5m }
        };
        InvokeNormalize(config);
        Assert.Equal(3m, config.Risk.TargetRiskEur);
        Assert.Equal(9m, config.Risk.MaxConcurrentOpenRisk);
    }

    [Fact]
    public void Virtual_open_fee_uses_taker_pct()
    {
        var config = BaseConfig();
        var portfolio = new FuturesVirtualPortfolio(config, new NullStore());
        var state = portfolio.Load();
        var costs = FuturesExecutionCostModel.Estimate(config, FuturesDesiredExposure.Long, null);
        var size = FuturesPositionSizer.Size(config, 0.75m, costs, 10m);
        var plan = new FuturesEntryPlan(
            size.SizedNotionalEur, size.SizedNotionalEur, size.AtrPct, size.StopDistancePct,
            size.TakeProfitDistancePct, costs.RoundTripCostPct, costs.ExpectedFundingPct,
            0m, 1m, 0, 0, size.ProjectedOpenRiskEur, "ok", "btc", "yes",
            size.TargetRiskEur, size.SizedNotionalEur, size.RequiredMarginEur, size.EffectiveLeverage,
            size.ProjectedStopLossEur, costs.Model, size.StopSource, size.NotionalCapReason);

        var fill = portfolio.Apply(state, "RIVER/USD", FuturesDesiredExposure.Long, 3.5m,
            size.SizedNotionalEur, 10m, entryPlan: plan);

        Assert.True(fill.PositionOpened);
        var expectedFee = FuturesExecutionCostModel.FeeEur(size.SizedNotionalEur, config.Fees.TakerPct);
        Assert.Equal(expectedFee, fill.Action.FeeEur);
        Assert.Equal("MODELED_TAKER_FOK", fill.Action.FillSource);
        Assert.Equal(FuturesExecutionCostModel.TakerFokRoundTrip, fill.Action.ExecutionCostModel);
    }

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

    private static void InvokeNormalize(FuturesBotConfiguration config)
    {
        var method = typeof(FuturesBotConfiguration).GetMethod("Normalize", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(config, null);
    }
}
