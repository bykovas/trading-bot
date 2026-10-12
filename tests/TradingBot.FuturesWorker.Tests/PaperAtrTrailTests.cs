using TradingBot.Core.Common;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class PaperAtrTrailTests
{
    private static FuturesBotConfiguration Config() => new()
    {
        Exits = new() { AtrTrailingRegimeEnabled = true, TrailingActivationRMultiple = 1.5m, TrailingAtrMultiple = 1.5m },
        TpSl = new() { Enabled = true, StopLossPercent = 2m },
        Futures = new() { LiveTradingEnabled = false }
    };

    [Theory]
    [InlineData("LONG", 103, 104, 103)]
    [InlineData("SHORT", 97, 96, 97)]
    public void Arms_at_R_tracks_peak_and_closes_on_retracement(string side, decimal arm, decimal peak, decimal retrace)
    {
        var position = new PortfolioPosition { Side = side, Origin = PositionOrigins.Bot, EntryPrice = 100m, AtrPct = 0.5m, StopDistancePct = 2m };
        var policy = new TpSlOrchestrator(Config());
        Assert.Null(policy.Evaluate(position, 100m, 100m));
        Assert.Null(position.TrailingStopState);
        Assert.NotNull(position.TakeProfitPrice);
        Assert.Null(policy.Evaluate(position, arm, arm));
        Assert.Equal("SIMULATED_OPEN", position.TrailingStopState);
        Assert.Null(position.TakeProfitPrice);
        Assert.Equal("CANCELLED", position.TpOrderState);
        Assert.Equal(0.75m, position.TrailingStopPercent);
        Assert.Null(policy.Evaluate(position, peak, peak));
        Assert.Equal("TRAILING_STOP", policy.Evaluate(position, retrace, retrace)?.Kind);
    }

    [Fact]
    public void Missing_ATR_does_not_invent_trailing_protection()
    {
        var position = new PortfolioPosition { Side = "LONG", Origin = PositionOrigins.Bot, EntryPrice = 100m, StopDistancePct = 2m };
        Assert.Equal("TAKE_PROFIT", new TpSlOrchestrator(Config()).Evaluate(position, 105m, 105m)?.Kind);
        Assert.Null(position.TrailingStopState);
    }

    [Fact]
    public void Existing_hard_stop_still_closes_before_activation()
    {
        var position = new PortfolioPosition { Side = "LONG", Origin = PositionOrigins.Bot, EntryPrice = 100m, AtrPct = 0.5m, StopDistancePct = 2m, StopLossPrice = 98m, SlOrderState = "SIMULATED_OPEN" };
        Assert.Equal("STOP_LOSS", new TpSlOrchestrator(Config()).Evaluate(position, 98m, 98m)?.Kind);
    }
}
