using TradingBot.SpotWorker;
using Xunit;

namespace TradingBot.SpotWorker.Tests;

public class DryRunPortfolioRiskGuardTests
{
    private static readonly DateTimeOffset TestNow = DateTimeOffset.Parse("2026-07-17T12:00:00Z");
    private static readonly FixedClock Clock = new(TestNow);

    private static DryRunPortfolio Portfolio(ExecutionPolicyOptions? executionPolicy = null) => new(
        new DryRunOptions
        {
            ApplyVirtualFills = true,
            OutputDirectory = Path.Combine(Path.GetTempPath(), "trading-bot-tests", Guid.NewGuid().ToString("N"))
        },
        new PortfolioOptions { StartingCashEur = 75m },
        executionPolicy ?? new ExecutionPolicyOptions(),
        new PositionExitOptions(),
        new PositionSizingOptions { Enabled = true, CashReserveEur = 15m },
        clock: Clock);

    private static PortfolioState State(decimal cashEur = 75m, params decimal[] entries) => new()
    {
        UpdatedAt = TestNow,
        CashEur = cashEur,
        Positions = entries
            .Select((entry, index) => new PortfolioPosition
            {
                Pair = $"HELD{index}/EUR",
                Side = "LONG",
                Quantity = entry,
                EntryPrice = 1m,
                EntryNotionalEur = entry,
                LastPrice = 1m,
                MarketValueEur = entry
            })
            .ToList()
    };

    private static InstrumentMarketState MarketState(string pair = "NEW/EUR") => new()
    {
        Instrument = new InstrumentOptions { Pair = pair, KrakenPair = pair.Replace("/", string.Empty, StringComparison.Ordinal), Venue = "Kraken", Enabled = true },
        Candles = Enumerable.Range(0, 30)
            .Select(index => new Candle(
                TestNow.AddMinutes(-index),
                Open: 1m,
                High: 1m,
                Low: 1m,
                Close: 1m,
                Volume: 100m,
                TradeCount: 10))
            .ToArray()
    };

    private static DecisionProposal LongProposal(decimal targetEur = 10m, string pair = "NEW/EUR") => new(
        pair,
        "LONG_MICRO",
        0.85m,
        targetEur,
        Array.Empty<SignalContribution>());

    private static RiskEvaluation ApprovedRisk() => new(true, Array.Empty<string>());

    // Portfolio wired for the friction gate: ATR exit mode plus a quote so the
    // bid/ask spread contributes to round-trip friction. Candle range controls ATR.
    private static DryRunPortfolio FrictionPortfolio(decimal minTakeProfitToFrictionRatio)
    {
        var config = new BotConfiguration
        {
            Trading = new TradingOptions { TargetOrderEur = 10m, TimeframeMinutes = 5 },
            Risk = new RiskOptions { MaxOrderEur = 15m, MaxConcurrentOpenRisk = 10m },
            Fees = new FeeOptions { MakerPct = 0.25m, TakerPct = 0.40m },
            Filters = new FilterOptions { MinQuoteVolume24h = 0m, MinDepthMultiple = 0m, MaxExitImpactPct = 0.5m, SlippageBufferPct = 0.10m },
            PositionExit = new PositionExitOptions { Mode = PositionExitOptions.ModeAtr, AtrPeriod = 3, StopLossAtrMultiplier = 2m, TakeProfitAtrMultiplier = 3m, MinTpVsCostMult = minTakeProfitToFrictionRatio },
            Strategy = new StrategyOptions { MinTakeProfitToFrictionRatio = minTakeProfitToFrictionRatio }
        };
        return new DryRunPortfolio(
        new DryRunOptions
        {
            ApplyVirtualFills = true,
            TakerFeeBps = 26m,
            SlippageBps = 10m,
            OutputDirectory = Path.Combine(Path.GetTempPath(), "trading-bot-tests", Guid.NewGuid().ToString("N"))
        },
        new PortfolioOptions { StartingCashEur = 75m },
        new ExecutionPolicyOptions(),
        config.PositionExit,
        new PositionSizingOptions { Enabled = false },
        clock: Clock,
        strategy: config.Strategy,
        fullConfig: config);
    }

    private static InstrumentMarketState RangedMarketState(decimal range) => new()
    {
        Instrument = new InstrumentOptions { Pair = "NEW/EUR", KrakenPair = "NEWEUR", Venue = "Kraken", Enabled = true },
        Quote = new Quote(Bid: 99.95m, Ask: 100.05m, Last: 100m, VolumeToday: 1000m),
        OrderBook = new OrderBookSnapshot(
            [new OrderBookLevel(99.95m, 100m), new OrderBookLevel(99.50m, 100m)],
            [new OrderBookLevel(100.05m, 100m)]),
        Candles = Enumerable.Range(0, 30)
            .Select(index => new Candle(
                TestNow.AddMinutes(-(30 - index)),
                Open: 100m,
                High: 100m + range / 2m,
                Low: 100m - range / 2m,
                Close: 100m,
                Volume: 100m,
                TradeCount: 10))
            .ToArray()
    };

    [Fact]
    public void Atr_take_profit_is_lifted_to_clear_round_trip_cost_floor()
    {
        // Calm candles: ATR = 0.3 -> TP distance 0.9 (0.9%). Friction = 0.52% fees
        // + 0.1% spread + 0.2% slippage = 0.82%; required 3 x 0.82 = 2.46% > 0.9%.
        var portfolio = FrictionPortfolio(minTakeProfitToFrictionRatio: 3m);
        var state = State();

        var action = portfolio.Apply(
            state,
            RangedMarketState(range: 0.3m),
            LongProposal(),
            ApprovedRisk(),
            new RiskOptions { MaxOrderEur = 15m, MaxOpenPositions = 6 },
            newPositionsThisCycle: 0);

        Assert.Equal("WOULD_BUY", action.Action);
        Assert.True(action.RoundTripCostEstimatePct >= 0.85m);
        Assert.True(Assert.Single(state.Positions).TakeProfitPrice > 102.4m);
    }

    [Fact]
    public void Allows_new_buy_when_take_profit_clears_the_friction_requirement()
    {
        // Volatile candles: ATR = 1.5 -> TP distance 4.5 (4.5%) > 3 x friction 0.82%.
        var portfolio = FrictionPortfolio(minTakeProfitToFrictionRatio: 3m);
        var state = State();

        var action = portfolio.Apply(
            state,
            RangedMarketState(range: 1.5m),
            LongProposal(),
            ApprovedRisk(),
            new RiskOptions { MaxOrderEur = 15m, MaxOpenPositions = 6 },
            newPositionsThisCycle: 0);

        Assert.Equal("WOULD_BUY", action.Action);
        Assert.Single(state.Positions);
    }

    [Fact]
    public void Blocks_new_buy_when_cycle_new_position_limit_is_reached()
    {
        var portfolio = Portfolio(new ExecutionPolicyOptions { MaxNewPositionsPerCycle = 2 });
        var state = State();

        var action = portfolio.Apply(
            state,
            MarketState(),
            LongProposal(),
            ApprovedRisk(),
            new RiskOptions { MaxOrderEur = 15m, MaxOpenPositions = 6 },
            newPositionsThisCycle: 2);

        Assert.Equal("WOULD_BUY_BLOCKED", action.Action);
        Assert.Equal("CYCLE_POSITION_LIMIT", action.HoldReasonCode);
        Assert.Contains("max new positions per cycle 2 already reached", action.Reason);
        Assert.Empty(state.Positions);
    }

    [Fact]
    public void Blocks_new_buy_when_total_exposure_cap_would_be_exceeded()
    {
        var portfolio = Portfolio();
        var state = State(cashEur: 45m, 10m, 10m, 10m, 10m);

        var action = portfolio.Apply(
            state,
            MarketState(),
            LongProposal(),
            ApprovedRisk(),
            new RiskOptions { MaxOrderEur = 15m, MaxOpenPositions = 6, MaxTotalExposureEur = 40m },
            newPositionsThisCycle: 0);

        Assert.Equal("WOULD_BUY_BLOCKED", action.Action);
        Assert.Contains("would exceed max EUR 40", action.Reason);
        Assert.Equal(4, state.Positions.Count);
    }

    [Fact]
    public void Blocks_new_buy_when_max_open_positions_reached_regardless_of_ranking()
    {
        var portfolio = Portfolio();
        var state = State(cashEur: 45m, 10m, 10m, 10m, 10m);

        var action = portfolio.Apply(
            state,
            MarketState(),
            LongProposal(),
            ApprovedRisk(),
            new RiskOptions { MaxOrderEur = 15m, MaxOpenPositions = 4 },
            newPositionsThisCycle: 0);

        Assert.Equal("WOULD_BUY_BLOCKED", action.Action);
        Assert.Contains("max open positions 4 already reached", action.Reason);
        Assert.Equal(4, state.Positions.Count);
    }

    [Fact]
    public void Exit_of_held_position_is_never_blocked_by_the_per_cycle_entry_limit()
    {
        // AllowImmediateExitOnSignalFlip + zero min-profit so a plain signal-flip
        // sell is allowed; the per-cycle NEW-position counter is already exhausted,
        // which must not matter for exits.
        var portfolio = new DryRunPortfolio(
            new DryRunOptions
            {
                ApplyVirtualFills = true,
                OutputDirectory = Path.Combine(Path.GetTempPath(), "trading-bot-tests", Guid.NewGuid().ToString("N"))
            },
            new PortfolioOptions { StartingCashEur = 75m },
            new ExecutionPolicyOptions { MaxNewPositionsPerCycle = 1, AllowImmediateExitOnSignalFlip = true },
            new PositionExitOptions { MinProfitToExitOnSignalFlipPercent = 0m, TakeProfitPercent = 0m, MaxHoldMinutes = 0 },
            new PositionSizingOptions(),
            clock: Clock);

        var state = new PortfolioState
        {
            UpdatedAt = TestNow,
            CashEur = 10m,
            Positions = new List<PortfolioPosition>
            {
                new()
                {
                    // Entry below current price so the conservative (fee/slippage
                    // adjusted) PnL is clearly positive and no soft guard interferes.
                    Pair = "NEW/EUR",
                    Side = "LONG",
                    Quantity = 5m,
                    EntryPrice = 0.9m,
                    EntryNotionalEur = 4.5m,
                    LastPrice = 1m,
                    MarketValueEur = 5m,
                    OpenedAtUtc = TestNow.AddHours(-1)
                }
            }
        };

        var flatProposal = new DecisionProposal("NEW/EUR", "NONE", 0.30m, 0m, Array.Empty<SignalContribution>());
        var action = portfolio.Apply(
            state,
            MarketState(),
            flatProposal,
            ApprovedRisk(),
            new RiskOptions { MaxOrderEur = 15m, MaxOpenPositions = 4 },
            newPositionsThisCycle: 99);

        Assert.Equal("WOULD_SELL", action.Action);
        Assert.Equal("SELL_SIGNAL_FLIP", action.ExitReasonCode);
        Assert.Empty(state.Positions);
    }

    [Fact]
    public void Blocks_new_buy_when_daily_loss_cap_is_breached()
    {
        var portfolio = Portfolio();
        var state = State();
        state.DailyRisk = new DailyRiskState
        {
            DateUtc = TestNow.UtcDateTime.ToString("yyyy-MM-dd"),
            RealizedPnlEur = -5.10m
        };

        var action = portfolio.Apply(
            state,
            MarketState(),
            LongProposal(),
            ApprovedRisk(),
            new RiskOptions { MaxOrderEur = 15m, MaxOpenPositions = 6, MaxDailyLossEur = 5m },
            newPositionsThisCycle: 0);

        Assert.Equal("WOULD_BUY_BLOCKED", action.Action);
        Assert.Equal("DAILY_LOSS_BLOCK", action.HoldReasonCode);
        Assert.Contains("daily loss cap reached", action.Reason);
        Assert.Empty(state.Positions);
    }

    [Fact]
    public void Allows_new_buy_when_yesterdays_loss_no_longer_counts()
    {
        var portfolio = Portfolio();
        var state = State();
        state.DailyRisk = new DailyRiskState
        {
            DateUtc = TestNow.UtcDateTime.AddDays(-1).ToString("yyyy-MM-dd"),
            RealizedPnlEur = -20m
        };

        var action = portfolio.Apply(
            state,
            MarketState(),
            LongProposal(),
            ApprovedRisk(),
            new RiskOptions { MaxOrderEur = 15m, MaxOpenPositions = 6, MaxDailyLossEur = 5m },
            newPositionsThisCycle: 0);

        Assert.Equal("WOULD_BUY", action.Action);
        Assert.Single(state.Positions);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
