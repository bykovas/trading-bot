using TradingBot.SpotWorker;
using Xunit;

namespace TradingBot.SpotWorker.Tests;

// Covers the Part 2 behaviors that live in DryRunPortfolio: the UTC entry blackout
// (2.4) and the trailing-stop peak tracking (2.2).
public class DryRunPortfolioBehaviorTests
{
    private static DryRunPortfolio Portfolio(
        ExecutionPolicyOptions executionPolicy,
        IClock clock,
        DryRunOptions? dryRun = null,
        PositionExitOptions? positionExit = null) => new(
        dryRun ?? new DryRunOptions
        {
            ApplyVirtualFills = true,
            OutputDirectory = Path.Combine(Path.GetTempPath(), "trading-bot-tests", Guid.NewGuid().ToString("N"))
        },
        new PortfolioOptions { StartingCashEur = 75m },
        executionPolicy,
        positionExit ?? new PositionExitOptions(),
        new PositionSizingOptions { Enabled = true, CashReserveEur = 15m },
        store: null,
        clock: clock);

    private static PortfolioState FreshState(decimal cashEur = 75m) => new()
    {
        UpdatedAt = DateTimeOffset.UtcNow,
        CashEur = cashEur,
        Positions = new List<PortfolioPosition>()
    };

    private static InstrumentMarketState MarketState(decimal bid = 1m, decimal ask = 1.01m, decimal last = 1m) => new()
    {
        Instrument = new InstrumentOptions { Pair = "NEW/EUR", KrakenPair = "NEWEUR", Venue = "Kraken", Enabled = true },
        Candles = Enumerable.Range(0, 30)
            .Select(index => new Candle(DateTimeOffset.UtcNow.AddMinutes(-index), last, last, last, last, 100m, 10))
            .ToArray(),
        Quote = new Quote(bid, ask, last, 1000m)
    };

    private static DecisionProposal LongProposal() =>
        new("NEW/EUR", "LONG_MICRO", 0.85m, 10m, Array.Empty<SignalContribution>());

    private static RiskEvaluation ApprovedRisk() => new(true, Array.Empty<string>());

    private static RiskOptions RiskOptions() => new() { MaxOrderEur = 15m, MaxOpenPositions = 6 };

    [Fact]
    public void New_entry_is_blocked_during_the_utc_blackout_window()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-07-03T00:30:00Z"));
        var portfolio = Portfolio(new ExecutionPolicyOptions { EntryBlackoutUtcFromHour = 0, EntryBlackoutMinutes = 60 }, clock);
        var state = FreshState();

        var action = portfolio.Apply(state, MarketState(), LongProposal(), ApprovedRisk(), RiskOptions(), newPositionsThisCycle: 0);

        Assert.Equal("WOULD_BUY_BLOCKED", action.Action);
        Assert.Equal("ENTRY_BLACKOUT", action.HoldReasonCode);
        Assert.Contains("entry blackout active", action.Reason);
        Assert.Empty(state.Positions);
    }

    [Fact]
    public void Exit_is_allowed_during_the_blackout_window()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-07-03T00:30:00Z"));
        var portfolio = Portfolio(
            new ExecutionPolicyOptions { EntryBlackoutUtcFromHour = 0, EntryBlackoutMinutes = 60, AllowImmediateExitOnSignalFlip = true },
            clock,
            positionExit: new PositionExitOptions { MinProfitToExitOnSignalFlipPercent = 0m, TakeProfitPercent = 0m, MaxHoldMinutes = 0 });

        var state = new PortfolioState
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            CashEur = 10m,
            Positions = new List<PortfolioPosition>
            {
                new()
                {
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
        var action = portfolio.Apply(state, MarketState(), flatProposal, ApprovedRisk(), RiskOptions(), newPositionsThisCycle: 0);

        Assert.Equal("WOULD_SELL", action.Action);
        Assert.Empty(state.Positions);
    }

    [Fact]
    public void New_entry_is_allowed_after_the_blackout_window()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-07-03T01:01:00Z"));
        var portfolio = Portfolio(new ExecutionPolicyOptions { EntryBlackoutUtcFromHour = 0, EntryBlackoutMinutes = 60 }, clock);
        var state = FreshState();

        var action = portfolio.Apply(state, MarketState(), LongProposal(), ApprovedRisk(), RiskOptions(), newPositionsThisCycle: 0);

        Assert.Equal("WOULD_BUY", action.Action);
        Assert.Single(state.Positions);
    }

    [Fact]
    public void Zero_blackout_minutes_disables_the_window()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-07-03T00:30:00Z"));
        var portfolio = Portfolio(new ExecutionPolicyOptions { EntryBlackoutUtcFromHour = 0, EntryBlackoutMinutes = 0 }, clock);
        var state = FreshState();

        var action = portfolio.Apply(state, MarketState(), LongProposal(), ApprovedRisk(), RiskOptions(), newPositionsThisCycle: 0);

        Assert.Equal("WOULD_BUY", action.Action);
        Assert.Single(state.Positions);
    }

    [Fact]
    public void Peak_pnl_percent_tracks_the_running_maximum_across_marks()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-07-03T12:00:00Z"));
        var portfolio = Portfolio(new ExecutionPolicyOptions(), clock);
        var state = new PortfolioState
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            CashEur = 0m,
            Positions = new List<PortfolioPosition>
            {
                new()
                {
                    Pair = "NEW/EUR",
                    Side = "LONG",
                    Quantity = 100m,
                    EntryPrice = 1m,
                    EntryNotionalEur = 100m,
                    LastPrice = 1m,
                    MarketValueEur = 100m
                }
            }
        };

        // First mark at a high bid establishes a positive peak.
        var high = portfolio.CloneAndMark(state, new[] { MarketState(bid: 1.06m, ask: 1.07m, last: 1.06m) });
        var peakAfterHigh = high.Positions[0].PeakPnlPercent;
        Assert.NotNull(peakAfterHigh);
        Assert.True(peakAfterHigh > 0m);

        // A later mark at a lower bid retraces PnL but must NOT lower the peak.
        var low = portfolio.CloneAndMark(high, new[] { MarketState(bid: 1.01m, ask: 1.02m, last: 1.01m) });
        Assert.Equal(peakAfterHigh, low.Positions[0].PeakPnlPercent);
        Assert.True(low.Positions[0].UnrealizedPnlPercent < low.Positions[0].PeakPnlPercent);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
