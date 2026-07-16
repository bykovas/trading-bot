using System.Reflection;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

// Feedback follow-up: safer risk defaults, independent portfolio caps, ATR-stop BLOCK
// (no silent clamp), legacy-parameter floor semantics, and the virtual taker model.
public sealed class FuturesRiskCapsAndSizingTests
{
    private static void Normalize(FuturesBotConfiguration config)
    {
        var method = typeof(FuturesBotConfiguration).GetMethod("Normalize", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(config, null);
    }

    // Mirrors the shipped appsettings live defaults on a 100 EUR virtual book.
    private static FuturesBotConfiguration LiveConfig()
    {
        var config = new FuturesBotConfiguration
        {
            Futures = new FuturesOptions
            {
                MaxLeverage = 10m,
                DefaultLeverage = 10m,
                MaxPositions = 3,
                TargetMarginEur = 20m,
                MaxNotionalEur = 150m,
                MaxTotalNotionalEur = 300m,
                MaxMarginPerPositionEur = 20m
            },
            Portfolio = new FuturesPortfolioOptions { StartingCashEur = 100m },
            Margin = new MarginOptions
            {
                MaintenanceMarginRatePercent = 0.5m,
                MinLiquidationDistancePercent = 8m,
                MaxAccountMarginUtilizationPercent = 80m
            },
            Fees = new FuturesFeesOptions { MakerPct = 0.02m, TakerPct = 0.05m },
            Exits = new FuturesExitOptions
            {
                StopAtrMult = 1m,
                MinRewardRiskMultiple = 2m,
                MinTpVsCostMult = 3m,
                StopDistanceCapPct = 3m,
                SlippageBufferPct = 0.10m
            },
            Risk = new FuturesRiskOptions(),
            TpSl = new TpSlOptions { Enabled = true, TakeProfitPercent = 1.5m, StopLossPercent = 0.75m }
        };
        Normalize(config);
        return config;
    }

    private static FuturesEntryRiskInputs LongInputs(
        FuturesBotConfiguration config,
        PortfolioState state,
        decimal notionalEur,
        decimal leverage,
        decimal stopPct,
        decimal projectedOpenRiskEur,
        decimal usedMarginEur) =>
        new(
            state,
            FuturesDesiredExposure.Long,
            MarkPrice: 100m,
            TargetNotionalEur: notionalEur,
            FilledNotionalEur: notionalEur,
            Leverage: leverage,
            UsedMarginEur: usedMarginEur,
            FundingRatePercent: 0m,
            AtrPct: stopPct,
            StopDistancePct: stopPct,
            TakeProfitDistancePct: stopPct * 2m,
            Volume24hUsd: config.Filters.MinQuoteVolume24h,
            ExitDepthEur: notionalEur * config.Filters.MinExitDepthMultiple,
            ProjectedOpenRiskEur: projectedOpenRiskEur,
            BtcAllowsLongs: true,
            BtcRegimeState: "btc ok",
            ShortAllowed: true,
            ShortBlockReason: null);

    private static PortfolioPosition OpenLong(decimal notionalEur, decimal marginEur) => new()
    {
        Pair = $"P{Guid.NewGuid():N}/USD",
        Side = "LONG",
        Quantity = 1m,
        EntryPrice = 100m,
        StopLossPrice = 98.5m,
        EntryNotionalEur = notionalEur,
        InitialMarginEur = marginEur,
        MarketValueEur = 0m
    };

    // 1. Safer live defaults on ~100 EUR equity.
    [Fact]
    public void Live_defaults_are_conservative_for_100_eur_equity()
    {
        var config = LiveConfig();
        Assert.Equal(1.0m, config.Risk.TargetRiskEur);                 // 1% equity per position
        Assert.Equal(3.0m, config.Risk.MaxConcurrentOpenRisk);         // TargetRiskEur * MaxPositions
        Assert.Equal(config.Risk.TargetRiskEur * config.Futures.MaxPositions, config.Risk.MaxConcurrentOpenRisk);

        var costs = FuturesExecutionCostModel.Estimate(config, FuturesDesiredExposure.Long, 0m);
        var plan = FuturesPositionSizer.Size(config, atrPct: 0.5m, costs, leverage: 10m);
        Assert.Equal(0.75m, plan.StopDistancePct);                     // floored
        // 1.00 / 0.0075 = 133.33 notional, margin 13.33 — both under the caps.
        Assert.True(plan.SizedNotionalEur > 133m && plan.SizedNotionalEur < 134m);
        Assert.True(plan.RequiredMarginEur > 13m && plan.RequiredMarginEur < 14m);
        Assert.True(plan.RequiredMarginEur < config.Futures.MaxMarginPerPositionEur);
        Assert.True(plan.SizedNotionalEur < config.Futures.MaxNotionalEur);
        Assert.Equal(1.0m, plan.ProjectedStopLossEur);                 // == TargetRiskEur
    }

    // 2. ATR stop above the maximum allowed stop is BLOCKED, never silently clamped.
    [Fact]
    public void Atr_stop_above_max_allowed_is_blocked_not_clamped()
    {
        var config = LiveConfig();
        var costs = FuturesExecutionCostModel.Estimate(config, FuturesDesiredExposure.Long, 0m);

        // atr 5% * mult 1 = 5% required stop, cap is 3%.
        var plan = FuturesPositionSizer.Size(config, atrPct: 5m, costs, leverage: 10m);
        Assert.True(plan.StopExceedsMaxAllowed);
        Assert.Equal("ATR_EXCEEDS_MAX", plan.StopSource);
        Assert.Equal(5m, plan.StopDistancePct);          // reported as the real required stop, NOT 3%
        Assert.Equal(3m, plan.MaxAllowedStopPct);

        var risk = new MarginRiskManager(config);
        var eval = risk.EvaluateEntry(LongInputs(config, new PortfolioState { CashEur = 100m },
            plan.SizedNotionalEur, 10m, plan.StopDistancePct, projectedOpenRiskEur: plan.ProjectedStopLossEur, usedMarginEur: 0m));
        Assert.False(eval.Approved);
        Assert.Contains(eval.Reasons, r => r.Contains("STOP_DISTANCE_TOO_LARGE"));
    }

    // 3. Three concurrent positions at equity 100 fit inside all caps.
    [Fact]
    public void Three_concurrent_positions_fit_within_caps_at_100_eur()
    {
        var config = LiveConfig();
        var risk = new MarginRiskManager(config);
        // Mid ATR: stop 1.5% -> notional 66.67, margin 6.667, pure stop risk 1.00 each.
        const decimal notional = 66.67m;
        const decimal margin = 6.667m;

        var state = new PortfolioState { CashEur = 100m };
        var first = risk.EvaluateEntry(LongInputs(config, state, notional, 10m, 1.5m, 1.0m, usedMarginEur: 0m));
        Assert.True(first.Approved);

        state.Positions.Add(OpenLong(notional, margin));
        var second = risk.EvaluateEntry(LongInputs(config, state, notional, 10m, 1.5m, 2.0m, usedMarginEur: margin));
        Assert.True(second.Approved);

        state.Positions.Add(OpenLong(notional, margin));
        var third = risk.EvaluateEntry(LongInputs(config, state, notional, 10m, 1.5m, 3.0m, usedMarginEur: margin * 2m));
        Assert.True(third.Approved);

        // A 4th is blocked by the position-count cap.
        state.Positions.Add(OpenLong(notional, margin));
        var fourth = risk.EvaluateEntry(LongInputs(config, state, notional, 10m, 1.5m, 3.0m, usedMarginEur: margin * 3m));
        Assert.False(fourth.Approved);
    }

    // 3b. The aggregate notional cap is an independent backstop (all-floor stress case).
    [Fact]
    public void Aggregate_notional_cap_blocks_all_floor_stack()
    {
        var config = LiveConfig();
        var risk = new MarginRiskManager(config);
        // Two floor-stop positions already at 133 notional each = 266 aggregate.
        var state = new PortfolioState { CashEur = 100m };
        state.Positions.Add(OpenLong(133.33m, 13.33m));
        state.Positions.Add(OpenLong(133.33m, 13.33m));
        // Third floor position would push aggregate to ~400 > MaxTotalNotionalEur 300.
        var third = risk.EvaluateEntry(LongInputs(config, state, 133.33m, 10m, 0.75m, 3.0m, usedMarginEur: 26.66m));
        Assert.False(third.Approved);
        Assert.Contains(third.Reasons, r => r.Contains("MAX_TOTAL_NOTIONAL"));
    }

    // 4. Insufficient free collateral is blocked explicitly.
    [Fact]
    public void Insufficient_available_margin_is_blocked()
    {
        var config = LiveConfig();
        var risk = new MarginRiskManager(config);
        var state = new PortfolioState { CashEur = 100m };
        // 95 EUR already committed -> 5 EUR free; a 66.67 notional at 10x needs 6.67 margin.
        var eval = risk.EvaluateEntry(LongInputs(config, state, 66.67m, 10m, 1.5m, 1.0m, usedMarginEur: 95m));
        Assert.False(eval.Approved);
        Assert.Contains(eval.Reasons, r => r.Contains("INSUFFICIENT_AVAILABLE_MARGIN"));
    }

    // 5. Requested leverage above the cap is clamped; effective leverage < requested.
    [Fact]
    public void Actual_leverage_is_clamped_below_requested()
    {
        var config = LiveConfig();
        var costs = FuturesExecutionCostModel.Estimate(config, FuturesDesiredExposure.Long, 0m);
        var plan = FuturesPositionSizer.Size(config, atrPct: 1.5m, costs, leverage: 25m);
        Assert.Equal(25m, plan.RequestedLeverage);
        Assert.Equal(config.Futures.MaxLeverage, plan.EffectiveLeverage);
        Assert.True(plan.EffectiveLeverage < plan.RequestedLeverage);
        // Margin/notional stay self-consistent at the effective leverage.
        Assert.Equal(decimal.Round(plan.SizedNotionalEur / plan.EffectiveLeverage, 6), plan.RequiredMarginEur);
    }

    // 6. Adverse funding raises the round-trip cost and therefore the TP floor.
    [Fact]
    public void Funding_cost_increases_tp_floor()
    {
        var config = LiveConfig();
        // Make the round-trip cost floor the binding TP constraint (above R-multiple and TP floor).
        config.Exits.MinRewardRiskMultiple = 0.1m;
        config.Exits.MinTpVsCostMult = 5m;
        config.Exits.MinTakeProfitPct = 0.3m;
        Normalize(config);

        var noFunding = FuturesExecutionCostModel.Estimate(config, FuturesDesiredExposure.Long, 0m);
        var adverseFunding = FuturesExecutionCostModel.Estimate(config, FuturesDesiredExposure.Long, 0.02m);
        Assert.True(adverseFunding.RoundTripCostPct > noFunding.RoundTripCostPct);

        var flat = FuturesPositionSizer.Size(config, atrPct: 0.5m, noFunding, leverage: 10m);
        var funded = FuturesPositionSizer.Size(config, atrPct: 0.5m, adverseFunding, leverage: 10m);
        Assert.True(funded.TakeProfitDistancePct > flat.TakeProfitDistancePct);
        Assert.True(funded.TakeProfitDistancePct >= adverseFunding.RoundTripCostPct * config.Exits.MinTpVsCostMult);
    }

    // 7. Projected per-trade risk includes realistic stop-exit slippage/fees; the concurrent
    //    cap uses pure stop-distance heat (documented split).
    [Fact]
    public void Projected_open_risk_includes_stop_exit_slippage()
    {
        var config = LiveConfig();
        var costs = FuturesExecutionCostModel.Estimate(config, FuturesDesiredExposure.Long, 0m);
        var plan = FuturesPositionSizer.Size(config, atrPct: 1.5m, costs, leverage: 10m);

        Assert.True(plan.ProjectedOpenRiskEur > plan.ProjectedStopLossEur);
        var slippageAndFees = plan.SizedNotionalEur * costs.RoundTripCostPct / 100m;
        Assert.Equal(decimal.Round(plan.ProjectedStopLossEur + slippageAndFees, 8), plan.ProjectedOpenRiskEur);
    }

    // 8. Virtual entry AND exit price the same taker IOC model.
    [Fact]
    public void Virtual_entry_and_exit_use_same_taker_model()
    {
        var config = LiveConfig();
        var portfolio = new FuturesVirtualPortfolio(config, new NullStore());
        var state = portfolio.Load();
        var costs = FuturesExecutionCostModel.Estimate(config, FuturesDesiredExposure.Long, 0m);
        var size = FuturesPositionSizer.Size(config, 1.5m, costs, 10m);
        var plan = new FuturesEntryPlan(
            size.SizedNotionalEur, size.SizedNotionalEur, size.AtrPct, size.StopDistancePct,
            size.TakeProfitDistancePct, costs.RoundTripCostPct, costs.ExpectedFundingPct,
            0m, 1m, 0, 0, size.ProjectedOpenRiskEur, "ok", "btc", "yes",
            size.TargetRiskEur, size.SizedNotionalEur, size.RequiredMarginEur, size.EffectiveLeverage,
            size.ProjectedStopLossEur, costs.Model, size.StopSource, size.NotionalCapReason);

        var open = portfolio.Apply(state, "SOL/USD", FuturesDesiredExposure.Long, 100m,
            size.SizedNotionalEur, 10m, entryPlan: plan);
        Assert.True(open.PositionOpened);
        Assert.Equal(FuturesExecutionCostModel.FeeEur(size.SizedNotionalEur, config.Fees.TakerPct), open.Action.FeeEur);
        Assert.Equal("MODELED_TAKER_IOC", open.Action.FillSource);

        var close = portfolio.Apply(state, "SOL/USD", FuturesDesiredExposure.Flat, 101m, 0m, 10m);
        Assert.True(close.PositionClosed);
        var grossExit = close.Action.FillPrice * close.Action.Quantity;
        Assert.Equal(FuturesExecutionCostModel.FeeEur(grossExit, config.Fees.TakerPct), close.Action.FeeEur);
    }

    // 9. Legacy reconciliation: a position opened WITHOUT an entry plan (restart / old state)
    //    still gets valid SL/TP from the legacy floors and holds without error.
    [Fact]
    public void Legacy_position_without_entry_plan_reconciles()
    {
        var config = LiveConfig();
        var portfolio = new FuturesVirtualPortfolio(config, new NullStore());
        var state = portfolio.Load();

        var open = portfolio.Apply(state, "ADA/USD", FuturesDesiredExposure.Long, 100m,
            config.Futures.DerivedNotionalEur(config.Futures.DefaultLeverage), config.Futures.DefaultLeverage);
        Assert.True(open.PositionOpened);
        var position = Assert.Single(state.Positions);
        Assert.NotNull(position.StopLossPrice);
        Assert.NotNull(position.TakeProfitPrice);
        // SL from the legacy StopLossPercent floor (0.75%): 100 - 0.75 = 99.25.
        Assert.Equal(100m - 100m * config.TpSl.StopLossPercent / 100m, position.StopLossPrice);

        // Re-applying the same exposure is a HOLD; the position survives the stop/TP contract.
        var hold = portfolio.Apply(state, "ADA/USD", FuturesDesiredExposure.Long, 100.5m,
            config.Futures.DerivedNotionalEur(config.Futures.DefaultLeverage), config.Futures.DefaultLeverage);
        Assert.False(hold.PositionClosed);
        Assert.Single(state.Positions);
    }

    // 10. Three signals in one correlation group: the second is blocked (per-group cap).
    [Fact]
    public void Correlation_group_blocks_second_signal_in_same_group()
    {
        var config = LiveConfig();
        config.CorrelationRisk.MaxOpenPositionsPerGroup = 1;
        config.CorrelationRisk.MaxExposureEurPerGroup = 150m;
        config.CorrelationRisk.Groups = new Dictionary<string, List<string>>
        {
            ["L1_L2"] = new() { "SOL/USD", "ADA/USD", "AVAX/USD" }
        };
        config.ExecutionPolicy.EntryBlackoutMinutes = 0; // avoid blackout interference
        Normalize(config);

        var worker = new FuturesDecisionWorker(config, null!, null!, null!, null!, null!, null!);
        var utc = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

        var state = new PortfolioState { CashEur = 100m };
        state.Positions.Add(new PortfolioPosition { Pair = "SOL/USD", Side = "LONG", EntryNotionalEur = 66m, InitialMarginEur = 6.6m });

        var guard = InvokeGuards(worker, state, "ADA/USD", utc, 66m);
        Assert.False(guard.Approved);
        Assert.Contains(guard.Reasons, r => r.Contains("correlation group"));
    }

    private static RiskEvaluation InvokeGuards(
        FuturesDecisionWorker worker, PortfolioState state, string pair, DateTimeOffset utc, decimal sizedNotionalEur)
    {
        var method = typeof(FuturesDecisionWorker).GetMethod("EvaluatePortfolioEntryGuards", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (RiskEvaluation)method.Invoke(worker, [state, pair, FuturesDesiredExposure.Long, utc, sizedNotionalEur])!;
    }

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
}
