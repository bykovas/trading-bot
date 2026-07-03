using TradingBot.Worker;
using Xunit;

namespace TradingBot.Worker.Tests;

public class DryRunPortfolioRiskGuardTests
{
    private static DryRunPortfolio Portfolio(ExecutionPolicyOptions? executionPolicy = null) => new(
        new DryRunOptions
        {
            ApplyVirtualFills = true,
            OutputDirectory = Path.Combine(Path.GetTempPath(), "trading-bot-tests", Guid.NewGuid().ToString("N"))
        },
        new PortfolioOptions { StartingCashEur = 75m },
        executionPolicy ?? new ExecutionPolicyOptions(),
        new PositionExitOptions(),
        new PositionSizingOptions { Enabled = true, CashReserveEur = 15m });

    private static PortfolioState State(decimal cashEur = 75m, params decimal[] entries) => new()
    {
        UpdatedAt = DateTimeOffset.UtcNow,
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
                DateTimeOffset.UtcNow.AddMinutes(-index),
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
            new PositionSizingOptions());

        var state = new PortfolioState
        {
            UpdatedAt = DateTimeOffset.UtcNow,
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
                    OpenedAtUtc = DateTimeOffset.UtcNow.AddHours(-1)
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
            DateUtc = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd"),
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
            DateUtc = DateTimeOffset.UtcNow.UtcDateTime.AddDays(-1).ToString("yyyy-MM-dd"),
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
}
