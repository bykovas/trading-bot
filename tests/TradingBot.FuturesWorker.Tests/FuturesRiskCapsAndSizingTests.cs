using System.Reflection;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

// Feedback follow-up: notional sizing caps, ATR-stop BLOCK (no silent clamp),
// legacy-parameter floor semantics, and the virtual taker model.
public sealed class FuturesRiskCapsAndSizingTests
{
    private static void Normalize(FuturesBotConfiguration config)
    {
        var method = typeof(FuturesBotConfiguration).GetMethod("Normalize", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(config, null);
    }

    // Mirrors the shipped appsettings live defaults on a small live futures account.
    private static FuturesBotConfiguration LiveConfig()
    {
        var config = new FuturesBotConfiguration
        {
            Futures = new FuturesOptions
            {
                MaxLeverage = 10m,
                DefaultLeverage = 10m,
                MaxPositions = 3,
                TargetMarginUsd = 15m,
                MaxNotionalUsd = 150m,
                MaxTotalNotionalUsd = 450m,
                MaxMarginPerPositionUsd = 15m
            },
            Portfolio = new FuturesPortfolioOptions { StartingCashUsd = 60m },
            Margin = new MarginOptions
            {
                MaintenanceMarginRatePercent = 5m,
                MinLiquidationDistancePercent = 5m,
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
            Risk = new FuturesRiskOptions { TargetRiskUsd = 4.5m, MaxConcurrentOpenRiskUsd = 13.5m },
            TpSl = new TpSlOptions { Enabled = true, TakeProfitPercent = 4m, StopLossPercent = 2m }
        };
        Normalize(config);
        return config;
    }

    [Fact]
    public void Invalid_maintenance_margin_rate_resets_to_kraken_retail_default()
    {
        foreach (var invalidRate in new[] { -1m, 0m, 51m })
        {
            var config = new FuturesBotConfiguration
            {
                Margin = new MarginOptions { MaintenanceMarginRatePercent = invalidRate }
            };

            Normalize(config);

            Assert.Equal(5m, config.Margin.MaintenanceMarginRatePercent);
        }
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

    // 1. Live defaults target a 150 USD notional floor-stop entry.
    [Fact]
    public void Live_defaults_target_150_usd_notional_for_floor_stop()
    {
        var config = LiveConfig();
        Assert.Equal(4.5m, config.Risk.TargetRiskUsd);                 // 3% max stop on 150 USD notional
        Assert.Equal(13.5m, config.Risk.MaxConcurrentOpenRiskUsd);     // TargetRiskUsd * MaxPositions
        Assert.Equal(config.Risk.TargetRiskUsd * config.Futures.MaxPositions, config.Risk.MaxConcurrentOpenRiskUsd);

        var costs = FuturesExecutionCostModel.Estimate(config, FuturesDesiredExposure.Long, 0m);
        var plan = FuturesPositionSizer.Size(config, atrPct: 0.5m, costs, leverage: 10m);
        Assert.Equal(2m, plan.StopDistancePct);                        // floored
        Assert.Equal(150m, plan.SizedNotionalEur);
        Assert.Equal(15m, plan.RequiredMarginEur);
        Assert.Equal(config.Futures.MaxMarginPerPositionUsd, plan.RequiredMarginEur);
        Assert.Equal(config.Futures.MaxNotionalUsd, plan.SizedNotionalEur);
        Assert.Equal(3m, plan.ProjectedStopLossEur);                   // actual risk at the 2% floor
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
            plan.SizedNotionalEur, 2m, plan.StopDistancePct, projectedOpenRiskEur: plan.ProjectedStopLossEur, usedMarginEur: 0m));
        Assert.False(eval.Approved);
        Assert.Contains(eval.Reasons, r => r.Contains("STOP_DISTANCE_TOO_LARGE"));
    }

    // 3. Three concurrent 15 USD-margin positions fit at the 80% utilization cap.
    [Fact]
    public void Three_concurrent_positions_fit_within_caps_at_150_usd()
    {
        var config = LiveConfig();
        var risk = new MarginRiskManager(config);
        // Floor-stop entries use the requested 150 USD notional / 15 USD margin.
        const decimal notional = 150m;
        const decimal margin = 15m;

        var state = new PortfolioState { CashEur = 60m };
        var first = risk.EvaluateEntry(LongInputs(config, state, notional, 10m, 2m, 3m, usedMarginEur: 0m));
        Assert.True(first.Approved);

        state.Positions.Add(OpenLong(notional, margin));
        var second = risk.EvaluateEntry(LongInputs(config, state, notional, 10m, 2m, 6m, usedMarginEur: margin));
        Assert.True(second.Approved);

        state.Positions.Add(OpenLong(notional, margin));
        var third = risk.EvaluateEntry(LongInputs(config, state, notional, 10m, 2m, 9m, usedMarginEur: margin * 2m));
        Assert.True(third.Approved);

        // A 4th is blocked by the position-count cap.
        state.Positions.Add(OpenLong(notional, margin));
        var fourth = risk.EvaluateEntry(LongInputs(config, state, notional, 10m, 2m, 9m, usedMarginEur: margin * 3m));
        Assert.False(fourth.Approved);
        Assert.Contains(fourth.Reasons, r => r.Contains("max futures positions"));
    }

    // 3b. The aggregate notional cap is an independent backstop (all-floor stress case).
    [Fact]
    public void Aggregate_notional_cap_blocks_all_floor_stack()
    {
        var config = LiveConfig();
        config.Futures.MaxPositions = 4;
        config.Risk.MaxConcurrentOpenRisk = 20m;
        config.Margin.MaxAccountMarginUtilizationPercent = 1000m;
        var risk = new MarginRiskManager(config);
        // Three floor-stop positions already consume the 450 USD aggregate cap.
        var state = new PortfolioState { CashEur = 60m };
        state.Positions.Add(OpenLong(150m, 15m));
        state.Positions.Add(OpenLong(150m, 15m));
        state.Positions.Add(OpenLong(150m, 15m));
        var fourth = risk.EvaluateEntry(LongInputs(config, state, 150m, 10m, 2m, 9m, usedMarginEur: 45m));
        Assert.False(fourth.Approved);
        Assert.Contains(fourth.Reasons, r => r.Contains("MAX_TOTAL_NOTIONAL"));
    }

    // 4. Insufficient free collateral is blocked explicitly.
    [Fact]
    public void Insufficient_available_margin_is_blocked()
    {
        var config = LiveConfig();
        var risk = new MarginRiskManager(config);
        var state = new PortfolioState { CashEur = 60m };
        // 58 USD already committed -> 2 USD free; a 150 USD notional at 10x needs 15 USD margin.
        var eval = risk.EvaluateEntry(LongInputs(config, state, 150m, 10m, 2m, 3m, usedMarginEur: 58m));
        Assert.False(eval.Approved);
        Assert.Contains(eval.Reasons, r => r.Contains("INSUFFICIENT_AVAILABLE_MARGIN"));
    }

    [Fact]
    public void Entry_plan_shrinks_to_remaining_free_collateral_before_risk_gate()
    {
        var config = LiveConfig();
        var costs = FuturesExecutionCostModel.Estimate(config, FuturesDesiredExposure.Long, 0m);
        var full = FuturesPositionSizer.Size(config, atrPct: 0.5m, costs, leverage: 10m);
        Assert.Equal(150m, full.SizedNotionalEur);
        Assert.Equal(15m, full.RequiredMarginEur);

        var state = new PortfolioState { CashEur = 3m };
        var existing = OpenLong(40m, 10m);
        existing.MarketValueEur = 97m;
        state.Positions.Add(existing);
        var shrunk = FuturesPositionSizer.FitToAvailableCollateral(
            full,
            config,
            state,
            usedMarginEur: 10m,
            costs);

        Assert.True(shrunk.SizedNotionalEur < full.SizedNotionalEur);
        Assert.Contains("AVAILABLE_COLLATERAL", shrunk.NotionalCapReason);
        Assert.Equal(decimal.Round(3m / (1m / 10m + config.Fees.TakerPct / 100m), 6), shrunk.SizedNotionalEur);
        Assert.Equal(decimal.Round(shrunk.SizedNotionalEur / 10m, 6), shrunk.RequiredMarginEur);
        Assert.True(shrunk.ProjectedStopLossEur < full.ProjectedStopLossEur);

        var risk = new MarginRiskManager(config);
        var eval = risk.EvaluateEntry(LongInputs(config, state, shrunk.SizedNotionalEur, 10m, shrunk.StopDistancePct, shrunk.ProjectedOpenRiskEur, usedMarginEur: 10m));
        Assert.True(eval.Approved);
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

        var flat = FuturesPositionSizer.Size(config, atrPct: 0.5m, noFunding, leverage: 2m);
        var funded = FuturesPositionSizer.Size(config, atrPct: 0.5m, adverseFunding, leverage: 2m);
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
        var plan = FuturesPositionSizer.Size(config, atrPct: 1.5m, costs, leverage: 2m);

        Assert.True(plan.ProjectedOpenRiskEur > plan.ProjectedStopLossEur);
        var slippageAndFees = plan.SizedNotionalEur * costs.RoundTripCostPct / 100m;
        Assert.Equal(decimal.Round(plan.ProjectedStopLossEur + slippageAndFees, 8), plan.ProjectedOpenRiskEur);
    }

    // 8. Virtual entry and exit price the same taker FOK model.
    [Fact]
    public void Virtual_entry_and_exit_use_same_taker_model()
    {
        var config = LiveConfig();
        var portfolio = new FuturesVirtualPortfolio(config, new NullStore());
        var state = portfolio.Load();
        var costs = FuturesExecutionCostModel.Estimate(config, FuturesDesiredExposure.Long, 0m);
        var size = FuturesPositionSizer.Size(config, 1.5m, costs, 2m);
        var plan = new FuturesEntryPlan(
            size.SizedNotionalEur, size.SizedNotionalEur, size.AtrPct, size.StopDistancePct,
            size.TakeProfitDistancePct, costs.RoundTripCostPct, costs.ExpectedFundingPct,
            0m, 1m, 0, 0, size.ProjectedOpenRiskEur, "ok", "btc", "yes",
            size.TargetRiskEur, size.SizedNotionalEur, size.RequiredMarginEur, size.EffectiveLeverage,
            size.ProjectedStopLossEur, costs.Model, size.StopSource, size.NotionalCapReason);

        var open = portfolio.Apply(state, "SOL/USD", FuturesDesiredExposure.Long, 100m,
            size.SizedNotionalEur, 2m, entryPlan: plan);
        Assert.True(open.PositionOpened);
        Assert.Equal(FuturesExecutionCostModel.FeeEur(size.SizedNotionalEur, config.Fees.TakerPct), open.Action.FeeEur);
        Assert.Equal("MODELED_TAKER_FOK", open.Action.FillSource);

        var close = portfolio.Apply(state, "SOL/USD", FuturesDesiredExposure.Flat, 101m, 0m, 2m);
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
