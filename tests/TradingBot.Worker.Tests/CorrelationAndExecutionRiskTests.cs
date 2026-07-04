using TradingBot.Worker;
using Xunit;

namespace TradingBot.Worker.Tests;

// Covers the correlation-risk layer plus the hourly entry throttle and the per-pair
// post-stop-loss cooldown, all wired into DryRunPortfolio.Apply's BUY-rejection chain.
public class CorrelationAndExecutionRiskTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-03T12:00:00Z");

    private static CorrelationRiskOptions CorrelationConfig() => new()
    {
        MaxOpenPositionsPerGroup = 1,
        MaxExposureEurPerGroup = 10m,
        MaxHighBetaPositions = 2,
        MaxHighBetaExposureEur = 20m,
        HighBetaGroups = new List<string> { "L1_L2", "AI_HIGH_BETA", "MEME_HIGH_BETA" },
        Groups = new Dictionary<string, List<string>>
        {
            ["BTC_BETA"] = new() { "XBT/EUR" },
            ["L1_L2"] = new() { "SOL/EUR", "ADA/EUR", "DOT/EUR" },
            ["AI_HIGH_BETA"] = new() { "FET/EUR", "TAO/EUR" },
            ["MEME_HIGH_BETA"] = new() { "XDG/EUR", "WIF/EUR" },
            ["COMMODITY"] = new() { "PAXG/EUR" }
        }
    };

    private static DryRunPortfolio Portfolio(
        IClock clock,
        CorrelationRiskOptions? correlationRisk = null,
        ExecutionPolicyOptions? executionPolicy = null,
        StrategyOptions? strategy = null,
        PositionExitOptions? positionExit = null) => new(
        new DryRunOptions
        {
            ApplyVirtualFills = true,
            OutputDirectory = Path.Combine(Path.GetTempPath(), "trading-bot-tests", Guid.NewGuid().ToString("N"))
        },
        new PortfolioOptions { StartingCashEur = 75m },
        executionPolicy ?? new ExecutionPolicyOptions { MaxNewPositionsPerHour = 0, CooldownAfterBuySeconds = 0, CooldownAfterSellSeconds = 0, CooldownAfterStopLossSeconds = 0, MinHoldSeconds = 0 },
        positionExit ?? new PositionExitOptions(),
        new PositionSizingOptions { Enabled = true, CashReserveEur = 15m },
        store: null,
        clock: clock,
        strategy: strategy,
        correlationRisk: correlationRisk);

    private static InstrumentMarketState MarketState(string pair, decimal price = 1m) => new()
    {
        Instrument = new InstrumentOptions { Pair = pair, KrakenPair = pair.Replace("/", string.Empty, StringComparison.Ordinal), Venue = "Kraken", Enabled = true },
        Candles = Enumerable.Range(0, 30)
            .Select(index => new Candle(Now.AddMinutes(-index), price, price, price, price, 100m, 10))
            .ToArray(),
        Quote = new Quote(price, price * 1.001m, price, 1000m)
    };

    private static PortfolioPosition Position(string pair, decimal notionalEur) => new()
    {
        Pair = pair,
        Side = "LONG",
        Quantity = notionalEur,
        EntryPrice = 1m,
        EntryNotionalEur = notionalEur,
        LastPrice = 1m,
        MarketValueEur = notionalEur,
        OpenedAtUtc = Now.AddHours(-1)
    };

    private static PortfolioState State(decimal cashEur, params PortfolioPosition[] positions) => new()
    {
        UpdatedAt = Now,
        CashEur = cashEur,
        Positions = positions.ToList()
    };

    private static DecisionProposal LongProposal(string pair, decimal targetEur = 10m) =>
        new(pair, "LONG_MICRO", 0.90m, targetEur, Array.Empty<SignalContribution>());

    private static RiskEvaluation ApprovedRisk() => new(true, Array.Empty<string>());

    private static RiskOptions Risk() => new() { MaxOrderEur = 15m, MaxOpenPositions = 6, MaxTotalExposureEur = 100m, MaxDailyLossEur = 0m };

    private static readonly FixedClock Clock = new(Now);

    // ---- 1. Correlation group count limit --------------------------------------

    [Fact]
    public void Second_buy_in_the_same_group_is_rejected_and_a_different_group_passes()
    {
        var portfolio = Portfolio(Clock, CorrelationConfig());

        // SOL is L1_L2; a second L1_L2 buy (ADA) hits the per-group count cap.
        var blocked = portfolio.Apply(
            State(75m, Position("SOL/EUR", 10m)),
            MarketState("ADA/EUR"),
            LongProposal("ADA/EUR"),
            ApprovedRisk(),
            Risk(),
            newPositionsThisCycle: 0);

        Assert.Equal("WOULD_BUY_BLOCKED", blocked.Action);
        Assert.Equal("CORRELATION_GROUP_LIMIT", blocked.HoldReasonCode);
        Assert.Equal("CORRELATION_GROUP_LIMIT", blocked.CorrelationRejectedReason);
        Assert.Equal("L1_L2", blocked.CorrelationGroup);

        // XBT is BTC_BETA (a different, non-high-beta group) and passes.
        var passed = portfolio.Apply(
            State(75m, Position("SOL/EUR", 10m)),
            MarketState("XBT/EUR"),
            LongProposal("XBT/EUR"),
            ApprovedRisk(),
            Risk(),
            newPositionsThisCycle: 0);

        Assert.Equal("WOULD_BUY", passed.Action);
        Assert.Null(passed.CorrelationRejectedReason);
        Assert.Equal("BTC_BETA", passed.CorrelationGroup);
    }

    // ---- 2. High-beta caps ------------------------------------------------------

    [Fact]
    public void Third_high_beta_buy_is_rejected_while_paxg_still_passes()
    {
        var portfolio = Portfolio(Clock, CorrelationConfig());

        // Two high-beta positions in different groups fill the high-beta count cap (2).
        var held = new[] { Position("SOL/EUR", 8m), Position("FET/EUR", 8m) };

        // XDG is a third high-beta group with no open position, so the per-group cap
        // passes but the aggregate high-beta count cap (2) rejects it.
        var blocked = portfolio.Apply(
            State(75m, held),
            MarketState("XDG/EUR"),
            LongProposal("XDG/EUR", 5m),
            ApprovedRisk(),
            Risk(),
            newPositionsThisCycle: 0);

        Assert.Equal("WOULD_BUY_BLOCKED", blocked.Action);
        Assert.Equal("HIGH_BETA_LIMIT", blocked.HoldReasonCode);
        Assert.Equal("HIGH_BETA_LIMIT", blocked.CorrelationRejectedReason);

        // PAXG (COMMODITY) is deliberately NOT high-beta, so it stays eligible even
        // while the high-beta cap is full.
        var passed = portfolio.Apply(
            State(75m, held),
            MarketState("PAXG/EUR"),
            LongProposal("PAXG/EUR", 5m),
            ApprovedRisk(),
            Risk(),
            newPositionsThisCycle: 0);

        Assert.Equal("WOULD_BUY", passed.Action);
        Assert.Null(passed.CorrelationRejectedReason);
        Assert.Equal("COMMODITY", passed.CorrelationGroup);
    }

    // ---- 3. Ungrouped pair = high-beta singleton --------------------------------

    [Fact]
    public void Ungrouped_pair_is_a_high_beta_singleton_and_is_reported_at_startup()
    {
        var config = CorrelationConfig();
        var portfolio = Portfolio(Clock, config);
        var held = new[] { Position("SOL/EUR", 8m), Position("FET/EUR", 8m) };

        // GHOST is in no configured group -> UNGROUPED singleton -> high-beta, so it
        // counts against the (already full) high-beta count cap.
        var blocked = portfolio.Apply(
            State(75m, held),
            MarketState("GHOST/EUR"),
            LongProposal("GHOST/EUR", 5m),
            ApprovedRisk(),
            Risk(),
            newPositionsThisCycle: 0);

        Assert.Equal("WOULD_BUY_BLOCKED", blocked.Action);
        Assert.Equal("HIGH_BETA_LIMIT", blocked.CorrelationRejectedReason);
        Assert.StartsWith("UNGROUPED:", blocked.CorrelationGroup);

        // The startup-warning source of truth lists the ungrouped universe pair.
        var ungrouped = CorrelationRiskResolver.UngroupedPairs(
            config,
            new[] { "SOL/EUR", "FET/EUR", "GHOST/EUR", "XBT/EUR" });
        Assert.Contains("GHOST/EUR", ungrouped);
        Assert.DoesNotContain("SOL/EUR", ungrouped);
    }

    // ---- 4. Hourly entry throttle ----------------------------------------------

    [Fact]
    public void Third_buy_inside_a_rolling_hour_is_rejected()
    {
        var execution = new ExecutionPolicyOptions { MaxNewPositionsPerHour = 2, CooldownAfterBuySeconds = 0, CooldownAfterSellSeconds = 0, MinHoldSeconds = 0 };
        var portfolio = Portfolio(Clock, correlationRisk: new CorrelationRiskOptions(), executionPolicy: execution);

        var recent = State(75m);
        recent.ActionHistory.Add(new PairActionHistory { Pair = "AAA/EUR", LastBuyAtUtc = Now.AddMinutes(-10) });
        recent.ActionHistory.Add(new PairActionHistory { Pair = "BBB/EUR", LastBuyAtUtc = Now.AddMinutes(-20) });

        var blocked = portfolio.Apply(
            recent,
            MarketState("CCC/EUR"),
            LongProposal("CCC/EUR"),
            ApprovedRisk(),
            Risk(),
            newPositionsThisCycle: 0);

        Assert.Equal("WOULD_BUY_BLOCKED", blocked.Action);
        Assert.Equal("HOURLY_ENTRY_LIMIT", blocked.HoldReasonCode);

        // The same two buys but older than the rolling window no longer count.
        var stale = State(75m);
        stale.ActionHistory.Add(new PairActionHistory { Pair = "AAA/EUR", LastBuyAtUtc = Now.AddHours(-2) });
        stale.ActionHistory.Add(new PairActionHistory { Pair = "BBB/EUR", LastBuyAtUtc = Now.AddHours(-3) });

        var passed = portfolio.Apply(
            stale,
            MarketState("CCC/EUR"),
            LongProposal("CCC/EUR"),
            ApprovedRisk(),
            Risk(),
            newPositionsThisCycle: 0);

        Assert.Equal("WOULD_BUY", passed.Action);
    }

    // ---- 7. Per-pair post-stop-loss cooldown -----------------------------------

    [Fact]
    public void Rebuy_is_blocked_during_stop_loss_cooldown_and_allowed_after_expiry()
    {
        var execution = new ExecutionPolicyOptions
        {
            MaxNewPositionsPerHour = 0,
            CooldownAfterBuySeconds = 0,
            CooldownAfterSellSeconds = 3600,
            CooldownAfterStopLossSeconds = 14400,
            MinHoldSeconds = 0
        };
        var portfolio = Portfolio(Clock, correlationRisk: new CorrelationRiskOptions(), executionPolicy: execution);

        // Plain sell cooldown expired (4000 > 3600) but stop-loss cooldown still active.
        var blocked = State(75m);
        blocked.ActionHistory.Add(new PairActionHistory
        {
            Pair = "SOL/EUR",
            LastSellAtUtc = Now.AddSeconds(-4000),
            LastStopLossAtUtc = Now.AddSeconds(-4000)
        });

        var blockedAction = portfolio.Apply(
            blocked,
            MarketState("SOL/EUR"),
            LongProposal("SOL/EUR"),
            ApprovedRisk(),
            Risk(),
            newPositionsThisCycle: 0);

        Assert.Equal("WOULD_BUY_BLOCKED", blockedAction.Action);
        Assert.Equal("STOPLOSS_COOLDOWN", blockedAction.HoldReasonCode);

        // Both windows expired -> allowed.
        var expired = State(75m);
        expired.ActionHistory.Add(new PairActionHistory
        {
            Pair = "SOL/EUR",
            LastSellAtUtc = Now.AddSeconds(-15000),
            LastStopLossAtUtc = Now.AddSeconds(-15000)
        });

        var allowedAction = portfolio.Apply(
            expired,
            MarketState("SOL/EUR"),
            LongProposal("SOL/EUR"),
            ApprovedRisk(),
            Risk(),
            newPositionsThisCycle: 0);

        Assert.Equal("WOULD_BUY", allowedAction.Action);
    }

    [Fact]
    public void Plain_sell_uses_only_the_sell_cooldown_not_the_stop_loss_cooldown()
    {
        var execution = new ExecutionPolicyOptions
        {
            MaxNewPositionsPerHour = 0,
            CooldownAfterBuySeconds = 0,
            CooldownAfterSellSeconds = 3600,
            CooldownAfterStopLossSeconds = 14400,
            MinHoldSeconds = 0
        };
        var portfolio = Portfolio(Clock, correlationRisk: new CorrelationRiskOptions(), executionPolicy: execution);

        // A plain sell (no stop-loss stamp) within the sell window blocks via the sell
        // cooldown only.
        var blocked = State(75m);
        blocked.ActionHistory.Add(new PairActionHistory { Pair = "SOL/EUR", LastSellAtUtc = Now.AddSeconds(-1000) });

        var blockedAction = portfolio.Apply(
            blocked,
            MarketState("SOL/EUR"),
            LongProposal("SOL/EUR"),
            ApprovedRisk(),
            Risk(),
            newPositionsThisCycle: 0);

        Assert.Equal("WOULD_BUY_BLOCKED", blockedAction.Action);
        Assert.Equal("COOLDOWN_BLOCK", blockedAction.HoldReasonCode);

        // Past the sell cooldown, with no stop-loss stamp, the re-buy is allowed — no
        // additional 4h stop-loss window applies to plain sells.
        var expired = State(75m);
        expired.ActionHistory.Add(new PairActionHistory { Pair = "SOL/EUR", LastSellAtUtc = Now.AddSeconds(-4000) });

        var allowedAction = portfolio.Apply(
            expired,
            MarketState("SOL/EUR"),
            LongProposal("SOL/EUR"),
            ApprovedRisk(),
            Risk(),
            newPositionsThisCycle: 0);

        Assert.Equal("WOULD_BUY", allowedAction.Action);
    }

    [Fact]
    public void Stop_loss_fill_stamps_the_last_stop_loss_timestamp()
    {
        var execution = new ExecutionPolicyOptions { MaxNewPositionsPerHour = 0, MinHoldSeconds = 0, AllowImmediateExitOnSignalFlip = true };
        var portfolio = Portfolio(
            Clock,
            correlationRisk: new CorrelationRiskOptions(),
            executionPolicy: execution,
            positionExit: new PositionExitOptions { StopLossPercent = 2.5m, TakeProfitPercent = 0m, MaxHoldMinutes = 0 });

        // Held SOL priced well below entry -> conservative PnL breaches the stop-loss.
        var state = State(10m, Position("SOL/EUR", 10m));
        state.Positions[0].EntryPrice = 1m;

        var action = portfolio.Apply(
            state,
            MarketState("SOL/EUR", price: 0.9m),
            new DecisionProposal("SOL/EUR", "LONG_MICRO", 0.90m, 0m, Array.Empty<SignalContribution>()),
            ApprovedRisk(),
            Risk(),
            newPositionsThisCycle: 0);

        Assert.Equal("WOULD_SELL", action.Action);
        Assert.Equal("SELL_STOP_LOSS", action.ExitReasonCode);

        var history = state.ActionHistory.Single(item => item.Pair == "SOL/EUR");
        Assert.NotNull(history.LastStopLossAtUtc);
        Assert.Equal(Now, history.LastStopLossAtUtc);
    }

    // ---- 8. Correlation never blocks exits or holds -----------------------------

    [Fact]
    public void Correlation_layer_never_blocks_a_hold_or_sell_of_a_held_position()
    {
        // Group L1_L2 is full (one SOL held, cap 1), but exits/holds are never gated.
        var portfolio = Portfolio(
            Clock,
            CorrelationConfig(),
            executionPolicy: new ExecutionPolicyOptions { MinHoldSeconds = 0, AllowImmediateExitOnSignalFlip = true },
            strategy: new StrategyOptions { ExitEmaGapPercent = 0m },
            positionExit: new PositionExitOptions { MinProfitToExitOnSignalFlipPercent = 0m, StopLossPercent = 2.5m, TakeProfitPercent = 0m, MaxHoldMinutes = 0 });

        // HOLD: desired still LONG for the held SOL.
        var holdState = State(10m, Position("SOL/EUR", 10m));
        var hold = portfolio.Apply(
            holdState,
            MarketState("SOL/EUR", price: 1.05m),
            new DecisionProposal("SOL/EUR", "LONG_MICRO", 0.90m, 0m, Array.Empty<SignalContribution>()),
            ApprovedRisk(),
            Risk(),
            newPositionsThisCycle: 0);

        Assert.Equal("WOULD_HOLD", hold.Action);
        Assert.Null(hold.CorrelationRejectedReason);
        Assert.Equal("L1_L2", hold.CorrelationGroup);

        // SELL: desired flips to NONE for the held SOL; profit is positive so it sells.
        var sellState = State(10m, new PortfolioPosition
        {
            Pair = "SOL/EUR",
            Side = "LONG",
            Quantity = 10m,
            EntryPrice = 0.9m,
            EntryNotionalEur = 9m,
            LastPrice = 1m,
            MarketValueEur = 10m,
            OpenedAtUtc = Now.AddHours(-1)
        });

        var sell = portfolio.Apply(
            sellState,
            MarketState("SOL/EUR", price: 1.05m),
            new DecisionProposal("SOL/EUR", "NONE", 0.30m, 0m, Array.Empty<SignalContribution>()),
            ApprovedRisk(),
            Risk(),
            newPositionsThisCycle: 0);

        Assert.Equal("WOULD_SELL", sell.Action);
        Assert.Null(sell.CorrelationRejectedReason);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
