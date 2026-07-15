using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class FuturesMaxHoldExitTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-15T03:00:00Z");

    [Fact]
    public void Old_profitable_position_is_not_closed_by_max_hold_timer()
    {
        var position = Position(openedMinutesAgo: 420, unrealizedPnlEur: 5.65m);

        var result = FuturesDecisionWorker.EvaluateMaxHoldExit(position, Now, maxHoldMinutes: 360);

        Assert.False(result.ShouldClose);
        Assert.Contains("healthy hold", result.Reason);
    }

    [Fact]
    public void Old_losing_position_is_closed_by_max_hold_timer()
    {
        var position = Position(openedMinutesAgo: 420, unrealizedPnlEur: -0.25m);

        var result = FuturesDecisionWorker.EvaluateMaxHoldExit(position, Now, maxHoldMinutes: 360);

        Assert.True(result.ShouldClose);
        Assert.Contains("stale-loss close", result.Reason);
    }

    [Fact]
    public void Young_losing_position_is_not_closed_by_max_hold_timer()
    {
        var position = Position(openedMinutesAgo: 120, unrealizedPnlEur: -0.25m);

        var result = FuturesDecisionWorker.EvaluateMaxHoldExit(position, Now, maxHoldMinutes: 360);

        Assert.False(result.ShouldClose);
        Assert.Null(result.Reason);
    }

    private static PortfolioPosition Position(int openedMinutesAgo, decimal unrealizedPnlEur) => new()
    {
        Pair = "UNI/USD",
        Side = "SHORT",
        OpenedAtUtc = Now.AddMinutes(-openedMinutesAgo),
        UnrealizedPnlEur = unrealizedPnlEur
    };
}
