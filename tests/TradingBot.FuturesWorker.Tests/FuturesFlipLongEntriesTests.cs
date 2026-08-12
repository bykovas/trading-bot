using System.Reflection;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

// Flipped-logic experiment: Futures.FlipLongEntries executes a fully approved LONG
// entry as a SHORT. These tests pin the config contract: off by default, and never
// active while shorts are disabled (a flip with AllowShorts=false would silently
// open nothing at the portfolio layer).
public sealed class FuturesFlipLongEntriesTests
{
    [Fact]
    public void Flip_is_off_by_default()
    {
        var config = new FuturesBotConfiguration();
        InvokeNormalize(config);
        Assert.False(config.Futures.FlipLongEntries);
    }

    [Fact]
    public void Flip_survives_normalize_when_shorts_are_allowed()
    {
        var config = new FuturesBotConfiguration
        {
            Futures = new FuturesOptions { AllowShorts = true, FlipLongEntries = true }
        };
        InvokeNormalize(config);
        Assert.True(config.Futures.FlipLongEntries);
    }

    [Fact]
    public void Flip_is_disabled_when_shorts_are_not_allowed()
    {
        var config = new FuturesBotConfiguration
        {
            Futures = new FuturesOptions { AllowShorts = false, FlipLongEntries = true }
        };
        InvokeNormalize(config);
        Assert.False(config.Futures.FlipLongEntries);
    }

    [Fact]
    public void Flipped_exit_policy_defaults_are_normalized_without_changing_normal_policy()
    {
        var config = new FuturesBotConfiguration
        {
            TpSl = new TpSlOptions
            {
                TakeProfitPercent = 4m,
                StopLossPercent = 2m,
                TrailingStopPercent = 2m,
                FlippedTakeProfitPercent = 0m,
                FlippedTrailingStopPercent = -1m
            }
        };

        InvokeNormalize(config);

        Assert.Equal(4m, config.TpSl.TakeProfitPercent);
        Assert.Equal(2m, config.TpSl.StopLossPercent);
        Assert.Equal(2m, config.TpSl.TrailingStopPercent);
        Assert.Equal(1.5m, config.TpSl.FlippedTakeProfitPercent);
        Assert.Equal(0.75m, config.TpSl.FlippedTrailingStopPercent);
    }

    [Fact]
    public void Flipped_short_is_held_while_the_long_signal_persists()
    {
        var strategy = new LongShortStrategy(new FuturesBotConfiguration());
        var position = new PortfolioPosition { Pair = "SOL/USD", Side = "SHORT", FlippedEntry = true };

        // The same LONG signal that opened the flipped short must not close it.
        var decision = strategy.DecideHeld(position, LongSignal());
        Assert.Equal(FuturesDesiredExposure.Short, decision);
    }

    [Fact]
    public void Flipped_short_closes_when_the_original_long_would_have_closed()
    {
        var strategy = new LongShortStrategy(new FuturesBotConfiguration());
        var position = new PortfolioPosition { Pair = "SOL/USD", Side = "SHORT", FlippedEntry = true };

        // A ShortCandidate is the reversal that would have closed the original long.
        var decision = strategy.DecideHeld(position, ShortSignal());
        Assert.Equal(FuturesDesiredExposure.Flat, decision);
    }

    [Fact]
    public void Normal_short_still_closes_on_a_long_signal()
    {
        var strategy = new LongShortStrategy(new FuturesBotConfiguration());
        var position = new PortfolioPosition { Pair = "SOL/USD", Side = "SHORT", FlippedEntry = false };

        var decision = strategy.DecideHeld(position, LongSignal());
        Assert.Equal(FuturesDesiredExposure.Flat, decision);
    }

    private static TechnicalSignal LongSignal() => new(
        Score: 0.95m,
        Direction: "LONG",
        AllowsLong: true,
        HasBullishStructure: true,
        EmaFullyConfirmed: true,
        BullishEmaGapPercent: 0.3m,
        EmaGapVelocityPercent: null,
        Contributions: Array.Empty<SignalContribution>());

    private static TechnicalSignal ShortSignal() => new(
        Score: 0m,
        Direction: "SHORT",
        AllowsLong: false,
        HasBullishStructure: false,
        EmaFullyConfirmed: false,
        BullishEmaGapPercent: null,
        EmaGapVelocityPercent: null,
        Contributions: Array.Empty<SignalContribution>(),
        AllowsShort: true,
        HasBearishStructure: true,
        ShortScore: 0.85m);

    private static void InvokeNormalize(FuturesBotConfiguration config)
    {
        var method = typeof(FuturesBotConfiguration).GetMethod("Normalize", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(config, null);
    }
}
