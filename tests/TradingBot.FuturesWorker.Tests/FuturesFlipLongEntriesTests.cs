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

    private static void InvokeNormalize(FuturesBotConfiguration config)
    {
        var method = typeof(FuturesBotConfiguration).GetMethod("Normalize", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(config, null);
    }
}
