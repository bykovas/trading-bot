using Xunit;

namespace TradingBot.FuturesWorker.Tests;

// Blueprint-mandated safety cases for the virtual margin ledger: shorts only
// when allowed, reduce-only never opens exposure, flips are refused, margin
// math and liquidation distance behave.
public sealed class FuturesVirtualPortfolioTests
{
    private static FuturesBotConfiguration Config(bool allowShorts = true) => new()
    {
        Futures = new FuturesOptions { AllowShorts = allowShorts, MaxLeverage = 2m, DefaultLeverage = 2m },
        Portfolio = new FuturesPortfolioOptions { StartingCashEur = 100m },
        Fees = new FuturesFeesOptions { MakerPct = 0m, TakerPct = 0m },
        DryRun = new DryRunOptions { TakerFeeBps = 0m, SlippageBps = 0m },
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

    [Fact]
    public void Entry_plan_uses_only_filled_notional_for_partial_maker_fill()
    {
        var (portfolio, state) = Setup();
        var plan = new FuturesEntryPlan(
            RequestedNotionalEur: 20m,
            FilledNotionalEur: 8m,
            AtrPct: 1.2m,
            StopDistancePct: 2.4m,
            TakeProfitDistancePct: 3.6m,
            RoundTripCostEstimatePct: 0.25m,
            ExpectedFundingPct: 0.05m,
            QueueAheadEur: 75m,
            MakerFillRate: 0.4m,
            TimeToFillMs: 60000,
            RepegCount: 1,
            OpenRiskEur: 0.25m,
            FundingState: "apiField=fundingRate",
            BtcRegimeState: "btc ok",
            ShortAllowed: "yes");

        var fill = portfolio.Apply(state, "XBT/EUR", FuturesDesiredExposure.Long, 100m, 20m, 2m, entryPlan: plan);

        Assert.True(fill.PositionOpened);
        var position = Assert.Single(state.Positions);
        Assert.Equal(8m, position.EntryNotionalEur);
        Assert.Equal(4m, position.InitialMarginEur);
        Assert.Equal(96m, state.CashEur);
        Assert.Equal(8m, fill.Action.FilledNotionalEur);
        Assert.Equal(20m, fill.Action.RequestedNotionalEur);
        Assert.Equal(0.4m, fill.Action.MakerFillRate);
        Assert.Equal(98m, position.StopLossPrice);
        Assert.Equal(103m, position.TakeProfitPrice);
        Assert.Equal(96m, position.ExchangeStopLossPrice);
        Assert.Equal(106m, position.ExchangeTakeProfitPrice);
    }

    private static (FuturesVirtualPortfolio Portfolio, PortfolioState State) Setup(bool allowShorts = true)
    {
        var portfolio = new FuturesVirtualPortfolio(Config(allowShorts), new NullStore());
        return (portfolio, portfolio.Load());
    }

    [Fact]
    public void Long_entry_reserves_margin_and_sets_levels()
    {
        var (portfolio, state) = Setup();
        var fill = portfolio.Apply(state, "XBT/EUR", FuturesDesiredExposure.Long, 100m, 20m, 2m);

        Assert.True(fill.PositionOpened);
        var position = Assert.Single(state.Positions);
        Assert.Equal("LONG", position.Side);
        Assert.Equal(10m, position.InitialMarginEur);           // 20 notional / 2x
        Assert.Equal(90m, state.CashEur);                       // 100 - 10 margin (no fee)
        Assert.Equal(98m, position.StopLossPrice);              // -2%
        Assert.Equal(103m, position.TakeProfitPrice);           // +3%
        Assert.Equal(96m, position.ExchangeStopLossPrice);      // emergency -4% with 200% multiplier
        Assert.Equal(106m, position.ExchangeTakeProfitPrice);   // emergency +6% with 200% multiplier
        Assert.Equal(PositionOrigins.Bot, position.Origin);
        Assert.NotNull(position.LiquidationPrice);
        Assert.True(position.LiquidationPrice < 100m);
        Assert.Equal("SIMULATED_OPEN", position.TpOrderState);
        Assert.Equal("SIMULATED_OPEN", position.SlOrderState);
    }

    [Fact]
    public void Flipped_entry_uses_calibrated_profit_handoff_without_changing_size_or_stop()
    {
        var config = Config();
        config.TpSl.TakeProfitPercent = 4m;
        config.TpSl.StopLossPercent = 2m;
        config.TpSl.ExchangeProtectionMultiplierPercent = 200m;
        config.TpSl.TrailingStopPercent = 2m;
        config.TpSl.FlippedTakeProfitPercent = 1.5m;
        config.TpSl.FlippedTrailingStopPercent = 0.75m;
        var portfolio = new FuturesVirtualPortfolio(config, new NullStore());
        var state = portfolio.Load();

        var fill = portfolio.Apply(
            state,
            "DOGE/USD",
            FuturesDesiredExposure.Short,
            100m,
            10m,
            2m,
            flippedEntry: true);

        Assert.True(fill.PositionOpened);
        var position = Assert.Single(state.Positions);
        Assert.True(position.FlippedEntry);
        Assert.Equal(10m, position.EntryNotionalEur);
        Assert.Equal(5m, position.InitialMarginEur);
        Assert.Equal(2m, position.Leverage);
        Assert.Equal(102m, position.StopLossPrice);
        Assert.Equal(98.5m, position.TakeProfitPrice);
        Assert.Equal(104m, position.ExchangeStopLossPrice);
        Assert.Equal(97m, position.ExchangeTakeProfitPrice);
        Assert.Equal(2m, position.StopDistancePct);
        Assert.Equal(1.5m, position.TakeProfitDistancePct);
        Assert.Equal(0.75m, position.TrailingStopPercent);
        Assert.Equal(2m, fill.Action.StopDistancePct);
        Assert.Equal(1.5m, fill.Action.TakeProfitDistancePct);
    }

    [Fact]
    public void Entry_without_plan_uses_fixed_tpsl_not_atr_multipliers()
    {
        var config = Config();
        config.Exits.TakeProfitAtrMult = 9m;
        config.Exits.StopAtrMult = 8m;
        var portfolio = new FuturesVirtualPortfolio(config, new NullStore());
        var state = portfolio.Load();

        var fill = portfolio.Apply(state, "XBT/EUR", FuturesDesiredExposure.Long, 100m, 20m, 2m);

        Assert.True(fill.PositionOpened);
        var position = Assert.Single(state.Positions);
        Assert.Equal(98m, position.StopLossPrice);
        Assert.Equal(103m, position.TakeProfitPrice);
    }

    [Fact]
    public void Short_entry_opens_only_when_shorts_allowed()
    {
        var (allowed, allowedState) = Setup(allowShorts: true);
        var fill = allowed.Apply(allowedState, "XBT/EUR", FuturesDesiredExposure.Short, 100m, 20m, 2m);
        Assert.True(fill.PositionOpened);
        Assert.Equal("SHORT", Assert.Single(allowedState.Positions).Side);

        var (blocked, blockedState) = Setup(allowShorts: false);
        var refused = blocked.Apply(blockedState, "XBT/EUR", FuturesDesiredExposure.Short, 100m, 20m, 2m);
        Assert.False(refused.PositionOpened);
        Assert.Empty(blockedState.Positions);
        Assert.Equal("NO_ORDER", refused.Action.Action);
    }

    [Fact]
    public void Reduce_only_never_opens_exposure()
    {
        var (portfolio, state) = Setup();

        // Reduce-only short desire with no position: nothing may open.
        var shortAttempt = portfolio.Apply(state, "XBT/EUR", FuturesDesiredExposure.Short, 100m, 20m, 2m, reduceOnly: true);
        Assert.False(shortAttempt.PositionOpened);
        Assert.Empty(state.Positions);

        // Reduce-only long desire with no position: nothing may open either.
        var longAttempt = portfolio.Apply(state, "XBT/EUR", FuturesDesiredExposure.Long, 100m, 20m, 2m, reduceOnly: true);
        Assert.False(longAttempt.PositionOpened);
        Assert.Empty(state.Positions);
    }

    [Fact]
    public void Reduce_only_against_opposite_side_closes_and_never_reopens()
    {
        var (portfolio, state) = Setup();
        portfolio.Apply(state, "XBT/EUR", FuturesDesiredExposure.Long, 100m, 20m, 2m);

        var fill = portfolio.Apply(state, "XBT/EUR", FuturesDesiredExposure.Short, 110m, 20m, 2m, reduceOnly: true);
        Assert.True(fill.PositionClosed);
        Assert.False(fill.PositionOpened);
        Assert.Empty(state.Positions);
        Assert.Equal(true, fill.Action.ReduceOnly);
    }

    [Fact]
    public void Flip_without_reduce_only_is_refused()
    {
        var (portfolio, state) = Setup();
        portfolio.Apply(state, "XBT/EUR", FuturesDesiredExposure.Long, 100m, 20m, 2m);

        var flip = portfolio.Apply(state, "XBT/EUR", FuturesDesiredExposure.Short, 100m, 20m, 2m);
        Assert.False(flip.PositionOpened);
        Assert.False(flip.PositionClosed);
        Assert.Equal("LONG", Assert.Single(state.Positions).Side);
        Assert.Contains("flip", flip.Action.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hold_keeps_explicit_reason_when_desired_side_already_matches()
    {
        var (portfolio, state) = Setup();
        portfolio.Apply(state, "XBT/EUR", FuturesDesiredExposure.Long, 100m, 20m, 2m);

        var hold = portfolio.Apply(
            state,
            "XBT/EUR",
            FuturesDesiredExposure.Long,
            101m,
            0m,
            2m,
            reason: "external/adopted Kraken Futures position: signal reversal ignored");

        Assert.False(hold.PositionClosed);
        Assert.Equal("WOULD_HOLD", hold.Action.Action);
        Assert.Contains("external/adopted", hold.Action.Reason);
    }

    [Fact]
    public void Short_pnl_gains_when_price_falls_and_loses_when_price_rises()
    {
        var (portfolio, state) = Setup();
        portfolio.Apply(state, "XBT/EUR", FuturesDesiredExposure.Short, 100m, 20m, 2m);

        portfolio.MarkToMarket(state, "XBT/EUR", 95m);
        var position = Assert.Single(state.Positions);
        Assert.True(position.UnrealizedPnlEur > 0m);            // short gains on drop

        portfolio.MarkToMarket(state, "XBT/EUR", 105m);
        Assert.True(position.UnrealizedPnlEur < 0m);            // short loses on rise
    }

    [Fact]
    public void Closing_a_profitable_short_credits_margin_plus_pnl()
    {
        var (portfolio, state) = Setup();
        portfolio.Apply(state, "XBT/EUR", FuturesDesiredExposure.Short, 100m, 20m, 2m);
        Assert.Equal(90m, state.CashEur);

        var close = portfolio.Apply(state, "XBT/EUR", FuturesDesiredExposure.Flat, 90m, 0m, 2m, reduceOnly: true);
        Assert.True(close.PositionClosed);
        // entry 100, exit 90, qty 0.2 -> pnl +2; cash 90 + 10 margin + 2 pnl = 102
        Assert.Equal(102m, state.CashEur);
    }

    [Fact]
    public void Futures_close_action_carries_frozen_tpsl_distances_and_actual_exit_percent()
    {
        var (portfolio, state) = Setup();
        portfolio.Apply(state, "XBT/EUR", FuturesDesiredExposure.Long, 100m, 20m, 2m);

        var close = portfolio.Apply(
            state,
            "XBT/EUR",
            FuturesDesiredExposure.Flat,
            98m,
            0m,
            2m,
            reduceOnly: true,
            reason: "STOP_LOSS fast trigger at 98",
            exitTriggerSource: "bid");

        Assert.True(close.PositionClosed);
        Assert.Equal(2m, close.Action.StopDistancePct);
        Assert.Equal(3m, close.Action.TakeProfitDistancePct);
        Assert.Equal(0.4m, close.Action.OpenRiskEur);
        Assert.Equal("bid", close.Action.ExitTriggerSource);
        Assert.Contains("entry 100", close.Action.Reason);
        Assert.Contains("fill 98", close.Action.Reason);
        Assert.Contains("(-2", close.Action.Reason);
        Assert.Contains("working SL 2%", close.Action.Reason);
    }

    [Fact]
    public void Liquidation_estimates_sit_on_the_correct_side()
    {
        var longLiquidation = FuturesMath.EstimateLiquidationPrice("LONG", 100m, 2m, 5m);
        var shortLiquidation = FuturesMath.EstimateLiquidationPrice("SHORT", 100m, 2m, 5m);
        Assert.Equal(55m, longLiquidation);
        Assert.Equal(145m, shortLiquidation);

        // Higher leverage moves liquidation closer to entry.
        var lev1 = FuturesMath.EstimateLiquidationPrice("LONG", 100m, 1m, 5m);
        Assert.True(lev1 < longLiquidation);
    }

    [Fact]
    public void Ten_x_liquidation_distance_uses_maintenance_as_notional_fraction()
    {
        var longLiquidation = FuturesMath.EstimateLiquidationPrice("LONG", 100m, 10m, 5m);
        var shortLiquidation = FuturesMath.EstimateLiquidationPrice("SHORT", 100m, 10m, 5m);

        Assert.Equal(95m, longLiquidation);
        Assert.Equal(105m, shortLiquidation);
        Assert.Equal(5m, FuturesMath.LiquidationDistancePercent(100m, longLiquidation));
        Assert.Equal(5m, FuturesMath.LiquidationDistancePercent(100m, shortLiquidation));
    }
}
