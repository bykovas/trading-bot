using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class FuturesMaxHoldExitTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-15T03:00:00Z");

    [Fact]
    public void Old_profitable_position_is_not_closed_by_max_hold_timer()
    {
        var position = Position(openedMinutesAgo: 420, unrealizedPnlEur: 5.65m);

        var result = FuturesDecisionWorker.EvaluateMaxHoldExit(position, Now, maxHoldMinutes: 360, minStopProgressPct: 60m);

        Assert.False(result.ShouldClose);
        Assert.Contains("healthy hold", result.Reason);
    }

    [Fact]
    public void Old_small_losing_position_far_from_stop_is_not_closed_by_max_hold_timer()
    {
        var position = Position(
            openedMinutesAgo: 420,
            unrealizedPnlEur: -0.25m,
            entryPrice: 100m,
            markPrice: 101m,
            stopLossPrice: 105m);

        var result = FuturesDecisionWorker.EvaluateMaxHoldExit(position, Now, maxHoldMinutes: 360, minStopProgressPct: 60m);

        Assert.False(result.ShouldClose);
        Assert.Contains("stale-loss hold", result.Reason);
        Assert.Contains("20", result.Reason);
    }

    [Fact]
    public void Old_losing_position_near_stop_is_closed_by_max_hold_timer()
    {
        var position = Position(
            openedMinutesAgo: 420,
            unrealizedPnlEur: -0.25m,
            entryPrice: 100m,
            markPrice: 104m,
            stopLossPrice: 105m);

        var result = FuturesDecisionWorker.EvaluateMaxHoldExit(position, Now, maxHoldMinutes: 360, minStopProgressPct: 60m);

        Assert.True(result.ShouldClose);
        Assert.Contains("stale-loss close", result.Reason);
        Assert.Contains("80", result.Reason);
    }

    [Fact]
    public void Kraken_synced_position_is_classified_as_external()
    {
        var synced = Position(openedMinutesAgo: 420, unrealizedPnlEur: -1m);
        synced.Origin = PositionOrigins.KrakenSync;
        var botOwned = Position(openedMinutesAgo: 420, unrealizedPnlEur: -1m);
        botOwned.Origin = PositionOrigins.Bot;

        Assert.True(FuturesDecisionWorker.IsExternalFuturesPosition(synced));
        Assert.False(FuturesDecisionWorker.IsExternalFuturesPosition(botOwned));
    }

    [Fact]
    public void Young_losing_position_is_not_closed_by_max_hold_timer()
    {
        var position = Position(openedMinutesAgo: 120, unrealizedPnlEur: -0.25m);

        var result = FuturesDecisionWorker.EvaluateMaxHoldExit(position, Now, maxHoldMinutes: 360, minStopProgressPct: 60m);

        Assert.False(result.ShouldClose);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void Flipped_entry_is_not_closed_by_max_hold_when_policy_disables_it()
    {
        var position = Position(
            openedMinutesAgo: 420,
            unrealizedPnlEur: -0.25m,
            entryPrice: 100m,
            markPrice: 104m,
            stopLossPrice: 105m);
        position.FlippedEntry = true;

        var result = FuturesDecisionWorker.EvaluateMaxHoldExit(
            position,
            Now,
            maxHoldMinutes: 360,
            minStopProgressPct: 60m,
            maxHoldForFlippedEntriesEnabled: false);

        Assert.False(result.ShouldClose);
        Assert.Contains("disabled for flipped entry", result.Reason);
    }

    private static PortfolioPosition Position(
        int openedMinutesAgo,
        decimal unrealizedPnlEur,
        decimal entryPrice = 100m,
        decimal markPrice = 100m,
        decimal? stopLossPrice = null) => new()
    {
        Pair = "UNI/USD",
        Side = "SHORT",
        OpenedAtUtc = Now.AddMinutes(-openedMinutesAgo),
        EntryPrice = entryPrice,
        LastPrice = markPrice,
        MarkPrice = markPrice,
        StopLossPrice = stopLossPrice,
        UnrealizedPnlEur = unrealizedPnlEur
    };
}
