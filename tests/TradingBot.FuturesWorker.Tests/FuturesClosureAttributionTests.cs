using TradingBot.Core.Common;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

// A position can leave the exchange without the bot doing it, and the journal has to
// say which happened. It used to name whichever of stop and target the fill landed
// nearer to, so a close by hand in the middle of the range always arrived labelled as
// one of them: ETH/USD on 2026-08-24 exited 0.21% from its entry against a 2% stop and
// the page reported a stop-loss.
public sealed class FuturesClosureAttributionTests
{
    [Fact]
    public void A_close_that_reached_neither_level_is_not_attributed_to_one()
    {
        // SHORT from 2437.02: stop above at 2485.8, target below at 2340.5.
        var position = ShortPosition(entry: 2437.01577236m, stop: 2485.76m, target: 2340.50m);

        var reason = FuturesDecisionWorker.ClosureReason(
            position,
            [Fill("someone-elses-order", 2442.10357724m)],
            2442.10357724m);

        Assert.Equal("EXCHANGE_CLOSE", reason);
    }

    [Fact]
    public void A_fill_that_reached_the_stop_is_the_stop_even_without_an_order_id()
    {
        var position = ShortPosition(entry: 2437.01577236m, stop: 2485.76m, target: 2340.50m);

        // Past the stop, as a trigger fills: at or beyond, never short of it.
        var reason = FuturesDecisionWorker.ClosureReason(position, [Fill("x", 2487.40m)], 2487.40m);

        Assert.Equal("EXCHANGE_STOP_LOSS", reason);
    }

    [Fact]
    public void A_fill_that_reached_the_target_is_the_target()
    {
        var position = ShortPosition(entry: 2437.01577236m, stop: 2485.76m, target: 2340.50m);

        var reason = FuturesDecisionWorker.ClosureReason(position, [Fill("x", 2339.10m)], 2339.10m);

        Assert.Equal("EXCHANGE_TAKE_PROFIT", reason);
    }

    // The order id outranks the price: it is evidence, where the price is inference.
    [Fact]
    public void The_order_that_produced_the_fill_decides_when_it_is_one_of_ours()
    {
        var position = ShortPosition(entry: 2437.01577236m, stop: 2485.76m, target: 2340.50m);
        position.StopLossOrderId = "stop-order-1";

        var reason = FuturesDecisionWorker.ClosureReason(
            position,
            [Fill("stop-order-1", 2442.10m)],
            2442.10m);

        Assert.Equal("EXCHANGE_STOP_LOSS", reason);
    }

    [Fact]
    public void Liquidation_outranks_everything()
    {
        var position = ShortPosition(entry: 2437.01577236m, stop: 2485.76m, target: 2340.50m);
        position.TakeProfitOrderId = "tp-order-1";

        var reason = FuturesDecisionWorker.ClosureReason(
            position,
            [Fill("tp-order-1", 2339.00m, fillType: "liquidation")],
            2339.00m);

        Assert.Equal("EXCHANGE_LIQUIDATION", reason);
    }

    private static PortfolioPosition ShortPosition(decimal entry, decimal stop, decimal target) => new()
    {
        Pair = "ETH/USD",
        Side = "SHORT",
        Quantity = 0.615m,
        EntryPrice = entry,
        StopLossPrice = stop,
        TakeProfitPrice = target
    };

    private static FuturesFill Fill(string orderId, decimal price, string fillType = "taker") =>
        new(orderId, $"fill-{orderId}", "PF_ETHUSD", "sell", 0.615m, price, DateTimeOffset.UtcNow, fillType, null);
}
