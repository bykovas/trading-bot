using TradingBot.Core.Common;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

// Exit regime D's trail arms at +R and trails at a multiple of ATR. These pin the two
// pure calculations behind it: the profit measured in the trade's direction, and the
// ATR-scaled distance run through the same spread floor / 2dp rounding every trail uses.
public sealed class ExitRegimeDTrailTests
{
    [Fact]
    public void Profit_in_direction_is_signed_by_side()
    {
        var lng = new PortfolioPosition { Side = "LONG", EntryPrice = 100m };
        var sht = new PortfolioPosition { Side = "SHORT", EntryPrice = 100m };

        // Price up 1%: +1% for the long, -1% for the short.
        Assert.Equal(1m, FuturesDecisionWorker.ProfitPercentInDirection(lng, 101m));
        Assert.Equal(-1m, FuturesDecisionWorker.ProfitPercentInDirection(sht, 101m));
        // Price down 1%: the short is +1% in profit.
        Assert.Equal(1m, FuturesDecisionWorker.ProfitPercentInDirection(sht, 99m));
    }

    // Trail distance = TrailingAtrMultiple x ATR, floored at 2x spread, rounded up to 2dp.
    [Fact]
    public void Regime_trail_distance_is_atr_scaled_then_spread_floored()
    {
        var tp = new TpSlOptions { TrailingStopMinSpreadMultiple = 2m, StopLossPercent = 3m };

        // ATR 0.44% x 1.5 = 0.66%, spread 0.1% -> floor 0.2% is below it, so stays 0.66.
        Assert.Equal(0.66m, tp.EffectiveTrailingStopPercent(1.5m * 0.44m, 0.1m));
        // Wide book: spread 0.5% -> floor 1.0% lifts a 0.66% ATR trail to 1.0%.
        Assert.Equal(1.0m, tp.EffectiveTrailingStopPercent(1.5m * 0.44m, 0.5m));
    }
}
