using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class MarginRiskAndTpSlTests
{
    private static FuturesBotConfiguration Config() => new()
    {
        Futures = new FuturesOptions { AllowShorts = true, MaxLeverage = 2m, DefaultLeverage = 2m, MaxPositions = 1 },
        Margin = new MarginOptions
        {
            MaintenanceMarginRatePercent = 0.5m,
            MinLiquidationDistancePercent = 15m,
            MaxAccountMarginUtilizationPercent = 50m
        },
        TpSl = new TpSlOptions { Enabled = true, TakeProfitPercent = 3m, StopLossPercent = 2m, TriggerSource = "mark" }
    };

    private static PortfolioState State(decimal cash = 100m) => new() { CashEur = cash };

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
        config.Margin.MinLiquidationDistancePercent = 60m; // 2x leverage gives ~49.75% distance
        var risk = new MarginRiskManager(config);
        var evaluation = risk.EvaluateEntry(State(), FuturesDesiredExposure.Long, 100m, 20m, 2m, usedMarginEur: 0m);
        Assert.False(evaluation.Approved);
        Assert.Contains(evaluation.Reasons, reason => reason.Contains("liquidation distance"));
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
        state.Positions.Add(new PortfolioPosition { Pair = "ETH/USD", Side = "LONG", InitialMarginEur = 10m });
        state.Positions.Add(new PortfolioPosition { Pair = "SOL/USD", Side = "LONG", InitialMarginEur = 10m });

        var third = risk.EvaluateEntry(state, FuturesDesiredExposure.Long, 100m, 20m, 2m, usedMarginEur: 20m);
        Assert.True(third.Approved);

        state.Positions.Add(new PortfolioPosition { Pair = "XBT/USD", Side = "LONG", InitialMarginEur = 10m });
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
    public void Funding_gate_blocks_expensive_entries()
    {
        var config = Config();
        config.Funding.MaxAbsFundingRatePercentForEntry = 0.05m;
        var risk = new MarginRiskManager(config);
        var evaluation = risk.EvaluateEntry(State(), FuturesDesiredExposure.Long, 100m, 20m, 2m, 0m, fundingRatePercent: -0.2m);
        Assert.False(evaluation.Approved);
        Assert.Contains(evaluation.Reasons, reason => reason.Contains("funding rate"));
    }

    [Fact]
    public void Funding_gate_blocks_when_rate_is_unavailable()
    {
        var config = Config();
        config.Funding.MaxAbsFundingRatePercentForEntry = 0.05m;
        var risk = new MarginRiskManager(config);
        var evaluation = risk.EvaluateEntry(State(), FuturesDesiredExposure.Long, 100m, 20m, 2m, 0m);
        Assert.False(evaluation.Approved);
        Assert.Contains(evaluation.Reasons, reason => reason.Contains("funding rate unavailable"));
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
}
