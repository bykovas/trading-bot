using TradingBot.Core.Common;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

// The Reversal book's entire entry signal: a sharp fast move fires the fade, and the
// SIGN of the move picks the side. These tests pin the arithmetic (which closes, which
// window), the two thresholds, the sanity cap, and the fact that the momentum scorer
// has no say in any of it.
public sealed class FuturesReversalStrategyTests
{
    private static FuturesReversalOptions Options(
        bool enabled = true,
        int windowMinutes = 15,
        decimal minMove = 3m,
        decimal maxMove = 15m) => new()
    {
        Enabled = enabled,
        TriggerWindowMinutes = windowMinutes,
        MinMovePercent = minMove,
        MaxMovePercent = maxMove
    };

    // A flat tape ending with the given closes; highs/lows/volume are irrelevant to
    // this strategy and stay degenerate on purpose.
    private static List<Candle> Closes(params decimal[] closes)
    {
        var start = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
        return closes
            .Select((close, i) => new Candle(start.AddMinutes(15 * i), close, close, close, close, 100m, 1))
            .ToList();
    }

    [Fact]
    public void A_sharp_rise_is_faded_with_a_short()
    {
        var signal = FuturesReversalStrategy.Evaluate(Closes(100m, 100m, 104m), 15, Options());

        Assert.True(signal.Fires);
        Assert.Equal(FuturesDesiredExposure.Short, signal.Desired);
        Assert.Equal(4m, signal.MovePercent);
    }

    [Fact]
    public void A_sharp_fall_is_faded_with_a_long()
    {
        var signal = FuturesReversalStrategy.Evaluate(Closes(100m, 100m, 96m), 15, Options());

        Assert.True(signal.Fires);
        Assert.Equal(FuturesDesiredExposure.Long, signal.Desired);
        Assert.Equal(-4m, signal.MovePercent);
    }

    // 15 minutes on 15m candles is exactly the LAST closed bar: the move is measured
    // from the close before it, and older candles must not leak into the window.
    [Fact]
    public void The_window_is_the_last_bar_not_the_whole_tape()
    {
        // +10% two bars ago, flat since: the 15-minute window sees 0%.
        var signal = FuturesReversalStrategy.Evaluate(Closes(100m, 110m, 110m), 15, Options());

        Assert.False(signal.Fires);
    }

    [Fact]
    public void A_wider_window_spans_the_matching_number_of_bars()
    {
        // +4% spread over two bars: invisible to a 15-minute window, caught by 30.
        var tape = Closes(100m, 102m, 104m);

        Assert.False(FuturesReversalStrategy.Evaluate(tape, 15, Options(windowMinutes: 15, minMove: 3m)).Fires);
        var wide = FuturesReversalStrategy.Evaluate(tape, 15, Options(windowMinutes: 30, minMove: 3m));
        Assert.True(wide.Fires);
        Assert.Equal(2, wide.WindowBars);
        Assert.Equal(FuturesDesiredExposure.Short, wide.Desired);
    }

    [Fact]
    public void A_move_below_the_threshold_does_not_fire()
    {
        Assert.False(FuturesReversalStrategy.Evaluate(Closes(100m, 100m, 102.9m), 15, Options()).Fires);
        Assert.True(FuturesReversalStrategy.Evaluate(Closes(100m, 100m, 103m), 15, Options()).Fires);
    }

    // Beyond the cap the move is news or a delisting, not an overreaction: a coin
    // down 40% is not a dip.
    [Fact]
    public void A_move_beyond_the_sanity_cap_is_not_faded()
    {
        Assert.False(FuturesReversalStrategy.Evaluate(Closes(100m, 100m, 60m), 15, Options()).Fires);
        // Cap 0 disables the ceiling.
        Assert.True(FuturesReversalStrategy.Evaluate(Closes(100m, 100m, 60m), 15, Options(maxMove: 0m)).Fires);
    }

    [Fact]
    public void Disabled_or_short_tapes_never_fire()
    {
        Assert.False(FuturesReversalStrategy.Evaluate(Closes(100m, 104m), 15, Options(enabled: false)).Fires);
        Assert.False(FuturesReversalStrategy.Evaluate(Closes(104m), 15, Options()).Fires);
        Assert.False(FuturesReversalStrategy.Evaluate(new List<Candle>(), 15, Options()).Fires);
    }

    // The binder default is OFF with the studied thresholds, so an instance without a
    // Reversal section - the control - cannot grow a second book by upgrading.
    [Fact]
    public void The_default_configuration_is_off_with_the_studied_thresholds()
    {
        var defaults = new FuturesReversalOptions();

        Assert.False(defaults.Enabled);
        Assert.Equal(15, defaults.TriggerWindowMinutes);
        Assert.Equal(3m, defaults.MinMovePercent);
        Assert.Equal(15m, defaults.MaxMovePercent);
    }

    // The strategy tag must survive the round trip a position makes through a clone -
    // the persistence layer serializes clones, so a tag the clone drops is a tag the
    // database never sees.
    [Fact]
    public void The_strategy_tag_survives_a_position_clone()
    {
        var position = new PortfolioPosition { Pair = "ETH/USD", Strategy = TradeStrategies.Reversal };

        Assert.Equal(TradeStrategies.Reversal, position.Clone().Strategy);
    }
}
