using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class MarginRiskAndTpSlTests
{
    private static FuturesBotConfiguration Config() => new()
    {
        Futures = new FuturesOptions { AllowShorts = true, MaxLeverage = 2m, DefaultLeverage = 2m, MaxPositions = 1 },
        Margin = new MarginOptions
        {
            MaintenanceMarginRatePercent = 5m,
            MinLiquidationDistancePercent = 15m,
            MaxAccountMarginUtilizationPercent = 50m
        },
        TpSl = new TpSlOptions { Enabled = true, TakeProfitPercent = 3m, StopLossPercent = 2m, TriggerSource = "mark" }
    };

    private static PortfolioState State(decimal cash = 100m) => new() { CashEur = cash };

    private static FuturesEntryRiskInputs Inputs(
        FuturesBotConfiguration config,
        PortfolioState? state = null,
        FuturesDesiredExposure desired = FuturesDesiredExposure.Long,
        decimal? fundingRatePercent = 0m,
        decimal? atrPct = 1m,
        decimal? exitDepthEur = null,
        decimal projectedOpenRiskEur = 0.5m,
        bool btcAllowsLongs = true,
        bool shortAllowed = true) =>
        new(
            state ?? State(),
            desired,
            MarkPrice: 100m,
            TargetNotionalEur: 20m,
            FilledNotionalEur: 20m,
            Leverage: 2m,
            UsedMarginEur: 0m,
            FundingRatePercent: fundingRatePercent,
            AtrPct: atrPct,
            StopDistancePct: atrPct is null ? null : 2m,
            TakeProfitDistancePct: atrPct is null ? null : 3m,
            Volume24hUsd: config.Filters.MinQuoteVolume24h,
            ExitDepthEur: exitDepthEur ?? 20m * config.Filters.MinExitDepthMultiple,
            ProjectedOpenRiskEur: projectedOpenRiskEur,
            BtcAllowsLongs: btcAllowsLongs,
            BtcRegimeState: "btc ok",
            ShortAllowed: shortAllowed,
            ShortBlockReason: shortAllowed ? null : "btc short gate");

    [Fact]
    public void Margin_cap_blocks_oversized_entry()
    {
        var risk = new MarginRiskManager(Config());
        // 200 notional at 2x = 100 margin on 100 equity = 100% utilization > 50% cap.
        var evaluation = risk.EvaluateEntry(State(), FuturesDesiredExposure.Long, 100m, 200m, 2m, usedMarginEur: 0m);
        Assert.False(evaluation.Approved);
        Assert.Contains(evaluation.Reasons, reason => reason.Contains("margin utilization"));
    }

    [Fact]
    public void Liquidation_distance_gate_blocks_when_too_close()
    {
        var config = Config();
        config.Margin.MinLiquidationDistancePercent = 60m; // 2x leverage gives 45% distance.
        var risk = new MarginRiskManager(config);
        var evaluation = risk.EvaluateEntry(State(), FuturesDesiredExposure.Long, 100m, 20m, 2m, usedMarginEur: 0m);
        Assert.False(evaluation.Approved);
        Assert.Contains(evaluation.Reasons, reason => reason.Contains("liquidation distance"));
    }

    [Fact]
    public void Kraken_retail_maintenance_rate_rejects_10x_but_allows_2x_at_eight_percent_floor()
    {
        var config = Config();
        config.Futures.MaxLeverage = 10m;
        config.Futures.DefaultLeverage = 10m;
        config.Margin.MinLiquidationDistancePercent = 8m;
        var risk = new MarginRiskManager(config);

        var tenX = risk.EvaluateEntry(State(), FuturesDesiredExposure.Long, 100m, 20m, 10m, usedMarginEur: 0m);
        var twoX = risk.EvaluateEntry(State(), FuturesDesiredExposure.Long, 100m, 20m, 2m, usedMarginEur: 0m);

        Assert.False(tenX.Approved);
        Assert.Contains(tenX.Reasons, reason => reason.Contains("liquidation distance 5% below minimum 8%"));
        Assert.True(twoX.Approved);
    }

    [Fact]
    public void Leverage_cap_and_max_positions_block_entries()
    {
        var risk = new MarginRiskManager(Config());

        var overLeveraged = risk.EvaluateEntry(State(), FuturesDesiredExposure.Long, 100m, 20m, 5m, usedMarginEur: 0m);
        Assert.False(overLeveraged.Approved);

        var state = State();
        state.Positions.Add(new PortfolioPosition { Pair = "ETH/EUR", Side = "LONG" });
        var secondPosition = risk.EvaluateEntry(state, FuturesDesiredExposure.Long, 100m, 20m, 2m, usedMarginEur: 10m);
        Assert.False(secondPosition.Approved);
    }

    [Fact]
    public void Max_positions_allows_entries_until_configured_cap()
    {
        var config = Config();
        config.Futures.MaxPositions = 3;
        var risk = new MarginRiskManager(config);
        var state = State();
        state.Positions.Add(new PortfolioPosition { Pair = "ETH/USD", Side = "LONG", InitialMarginEur = 10m, Quantity = 0.1m, EntryPrice = 100m, StopLossPrice = 95m });
        state.Positions.Add(new PortfolioPosition { Pair = "SOL/USD", Side = "LONG", InitialMarginEur = 10m, Quantity = 0.1m, EntryPrice = 100m, StopLossPrice = 95m });

        var third = risk.EvaluateEntry(state, FuturesDesiredExposure.Long, 100m, 20m, 2m, usedMarginEur: 20m);
        Assert.True(third.Approved);

        state.Positions.Add(new PortfolioPosition { Pair = "XBT/USD", Side = "LONG", InitialMarginEur = 10m, Quantity = 0.1m, EntryPrice = 100m, StopLossPrice = 95m });
        var fourth = risk.EvaluateEntry(state, FuturesDesiredExposure.Long, 100m, 20m, 2m, usedMarginEur: 30m);
        Assert.False(fourth.Approved);
        Assert.Contains(fourth.Reasons, reason => reason.Contains("max futures positions 3"));
    }

    [Fact]
    public void Shorts_disabled_blocks_short_entry()
    {
        var config = Config();
        config.Futures.AllowShorts = false;
        var risk = new MarginRiskManager(config);
        var evaluation = risk.EvaluateEntry(State(), FuturesDesiredExposure.Short, 100m, 20m, 2m, usedMarginEur: 0m);
        Assert.False(evaluation.Approved);
    }

    [Fact]
    public void Funding_gate_blocks_directional_adverse_entries()
    {
        var config = Config();
        config.Funding.MaxAbsFundingRatePercentForEntry = 0.03m;
        var risk = new MarginRiskManager(config);
        var longPays = risk.EvaluateEntry(Inputs(config, fundingRatePercent: 0.2m));
        var shortPays = risk.EvaluateEntry(Inputs(config, desired: FuturesDesiredExposure.Short, fundingRatePercent: -0.2m));
        var longReceives = risk.EvaluateEntry(Inputs(config, fundingRatePercent: -0.2m));

        Assert.False(longPays.Approved);
        Assert.False(shortPays.Approved);
        Assert.True(longReceives.Approved);
        Assert.Contains(longPays.Reasons, reason => reason.Contains("adverse for long"));
        Assert.Contains(shortPays.Reasons, reason => reason.Contains("adverse for short"));
    }

    [Fact]
    public void Funding_gate_blocks_when_rate_is_unavailable()
    {
        var config = Config();
        config.Funding.MaxAbsFundingRatePercentForEntry = 0.05m;
        var risk = new MarginRiskManager(config);
        var evaluation = risk.EvaluateEntry(Inputs(config, fundingRatePercent: null));
        Assert.False(evaluation.Approved);
        Assert.Contains(evaluation.Reasons, reason => reason.Contains("funding rate unavailable"));
    }

    [Fact]
    public void Atr_and_exit_depth_are_fail_closed_for_new_entries()
    {
        var config = Config();
        var risk = new MarginRiskManager(config);

        var missingAtr = risk.EvaluateEntry(Inputs(config, atrPct: null));
        var missingDepth = risk.EvaluateEntry(Inputs(config, exitDepthEur: 0m));

        Assert.False(missingAtr.Approved);
        Assert.False(missingDepth.Approved);
        Assert.Contains(missingAtr.Reasons, reason => reason.Contains("ATR"));
        Assert.Contains(missingDepth.Reasons, reason => reason.Contains("exit depth"));
    }

    [Fact]
    public void Open_risk_cap_blocks_projected_risk_and_missing_stop_is_fail_unsafe()
    {
        var config = Config();
        config.Risk.MaxConcurrentOpenRisk = 1.5m;
        config.Futures.MaxPositions = 3;
        var risk = new MarginRiskManager(config);

        var tooMuchRisk = risk.EvaluateEntry(Inputs(config, projectedOpenRiskEur: 1.6m));
        var state = State();
        state.Positions.Add(new PortfolioPosition { Pair = "ETH/USD", Side = "LONG", Quantity = 1m, EntryPrice = 100m });
        var missingStop = risk.EvaluateEntry(Inputs(config, state));

        Assert.False(tooMuchRisk.Approved);
        Assert.False(missingStop.Approved);
        Assert.Contains(tooMuchRisk.Reasons, reason => reason.Contains("open risk"));
        Assert.Contains(missingStop.Reasons, reason => reason.Contains("valid stop"));
    }

    [Fact]
    public void Btc_regime_blocks_longs_and_short_gate_blocks_unapproved_shorts()
    {
        var config = Config();
        var risk = new MarginRiskManager(config);

        var longBlocked = risk.EvaluateEntry(Inputs(config, btcAllowsLongs: false));
        var shortBlocked = risk.EvaluateEntry(Inputs(config, desired: FuturesDesiredExposure.Short, shortAllowed: false));

        Assert.False(longBlocked.Approved);
        Assert.False(shortBlocked.Approved);
        Assert.Contains(longBlocked.Reasons, reason => reason.Contains("BTC regime"));
        Assert.Contains(shortBlocked.Reasons, reason => reason.Contains("short blocked"));
    }

    [Fact]
    public void Btc_override_flag_allows_long_entry()
    {
        var config = Config();
        var risk = new MarginRiskManager(config);

        var evaluation = risk.EvaluateEntry(Inputs(
            config,
            btcAllowsLongs: true,
            projectedOpenRiskEur: 0.5m));

        Assert.True(evaluation.Approved);
    }

    [Fact]
    public void TpSl_triggers_are_reduce_only_and_cancel_the_sibling_order()
    {
        var orchestrator = new TpSlOrchestrator(Config());
        var position = new PortfolioPosition
        {
            Pair = "XBT/EUR",
            Side = "LONG",
            EntryPrice = 100m,
            StopLossPrice = 98m,
            TakeProfitPrice = 103m,
            TpOrderState = "SIMULATED_OPEN",
            SlOrderState = "SIMULATED_OPEN"
        };

        Assert.Null(orchestrator.Evaluate(position, 101m, 101m));

        var trigger = orchestrator.Evaluate(position, 103.2m, 103.2m);
        Assert.NotNull(trigger);
        Assert.Equal("TAKE_PROFIT", trigger!.Kind);
        Assert.Equal("mark", trigger.TriggerSource);
        Assert.Equal("TRIGGERED", position.TpOrderState);
        Assert.Equal("CANCELLED", position.SlOrderState);

        // A triggered order pair never fires again.
        Assert.Null(orchestrator.Evaluate(position, 200m, 200m));
    }

    [Fact]
    public void Short_stop_loss_triggers_on_rising_mark_price()
    {
        var orchestrator = new TpSlOrchestrator(Config());
        var position = new PortfolioPosition
        {
            Pair = "XBT/EUR",
            Side = "SHORT",
            EntryPrice = 100m,
            StopLossPrice = 102m,
            TakeProfitPrice = 97m,
            TpOrderState = "SIMULATED_OPEN",
            SlOrderState = "SIMULATED_OPEN"
        };

        var trigger = orchestrator.Evaluate(position, 102.6m, 102.6m);
        Assert.NotNull(trigger);
        Assert.Equal("STOP_LOSS", trigger!.Kind);
        Assert.Equal("TRIGGERED", position.SlOrderState);
        Assert.Equal("CANCELLED", position.TpOrderState);
    }

    [Fact]
    public void Long_working_stop_uses_bid_before_mark_or_last()
    {
        var orchestrator = new TpSlOrchestrator(Config());
        var position = new PortfolioPosition
        {
            Pair = "XBT/EUR",
            Side = "LONG",
            Origin = PositionOrigins.Bot,
            EntryPrice = 100m,
            StopLossPrice = 99m,
            TakeProfitPrice = 103m,
            TpOrderState = "EXCHANGE_OPEN",
            SlOrderState = "EXCHANGE_OPEN"
        };

        var trigger = orchestrator.Evaluate(position, markPrice: 100m, lastPrice: 100m, bidPrice: 98.9m, askPrice: 99.1m);

        Assert.NotNull(trigger);
        Assert.Equal("STOP_LOSS", trigger!.Kind);
        Assert.Equal("bid", trigger.TriggerSource);
    }

    [Fact]
    public void Short_working_stop_uses_ask_before_mark_or_last()
    {
        var orchestrator = new TpSlOrchestrator(Config());
        var position = new PortfolioPosition
        {
            Pair = "XBT/EUR",
            Side = "SHORT",
            Origin = PositionOrigins.Bot,
            EntryPrice = 100m,
            StopLossPrice = 101m,
            TakeProfitPrice = 97m,
            TpOrderState = "EXCHANGE_OPEN",
            SlOrderState = "EXCHANGE_OPEN"
        };

        var trigger = orchestrator.Evaluate(position, markPrice: 100m, lastPrice: 100m, bidPrice: 100.9m, askPrice: 101.1m);

        Assert.NotNull(trigger);
        Assert.Equal("STOP_LOSS", trigger!.Kind);
        Assert.Equal("ask", trigger.TriggerSource);
    }

    [Fact]
    public void Exchange_open_tpsl_orders_are_not_triggered_by_the_bot()
    {
        var orchestrator = new TpSlOrchestrator(Config());
        var position = new PortfolioPosition
        {
            Pair = "XBT/EUR",
            Side = "LONG",
            EntryPrice = 100m,
            StopLossPrice = 98m,
            TakeProfitPrice = 103m,
            Origin = PositionOrigins.KrakenSync,
            TpOrderState = "EXCHANGE_OPEN",
            SlOrderState = "EXCHANGE_OPEN"
        };

        Assert.Null(orchestrator.Evaluate(position, 97m, 97m));
        Assert.Equal("EXCHANGE_OPEN", position.SlOrderState);
        Assert.Equal("EXCHANGE_OPEN", position.TpOrderState);

        Assert.Null(orchestrator.Evaluate(position, 104m, 104m));
        Assert.Equal("EXCHANGE_OPEN", position.SlOrderState);
        Assert.Equal("EXCHANGE_OPEN", position.TpOrderState);
    }

    [Fact]
    public void Kraken_synced_position_is_not_triggered_by_simulated_fallback()
    {
        var orchestrator = new TpSlOrchestrator(Config());
        var position = new PortfolioPosition
        {
            Pair = "XBT/EUR",
            Side = "LONG",
            EntryPrice = 100m,
            StopLossPrice = 98m,
            TakeProfitPrice = 103m,
            Origin = PositionOrigins.KrakenSync,
            TpOrderState = "EXCHANGE_OPEN",
            SlOrderState = "SIMULATED_OPEN"
        };

        var trigger = orchestrator.Evaluate(position, 97m, 97m);

        Assert.Null(trigger);
        Assert.Equal("SIMULATED_OPEN", position.SlOrderState);
        Assert.Equal("EXCHANGE_OPEN", position.TpOrderState);
    }
}
