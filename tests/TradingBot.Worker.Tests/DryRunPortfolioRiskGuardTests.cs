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
}
