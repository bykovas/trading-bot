using System.Reflection;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

// Flipped-logic experiment: Futures.FlipLongEntries may execute a fully approved
// LONG as a SHORT when the closed-candle 24h regime permits a countertrend trade.
// These tests pin both the config safety contract and the regime boundary.
public sealed class FuturesFlipLongEntriesTests
{
    [Fact]
    public void Flip_is_off_by_default()
    {
        var config = new FuturesBotConfiguration();
        InvokeNormalize(config);
        Assert.False(config.Futures.FlipLongEntries);
        Assert.Equal(3m, config.Futures.FlipMaxPair24hRisePercent);
        Assert.Equal(0m, config.Futures.FlipMaxBtc24hRisePercent);
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
    public void Flip_applies_when_btc_is_weak_and_pair_is_not_in_a_strong_daily_rise()
    {
        var options = EnabledFlipOptions();

        var decision = FuturesFlipRegimeGate.Evaluate(
            FuturesDesiredExposure.Long,
            options,
            pair24hChangePct: 2.4m,
            btc24hChangePct: -0.6m);

        Assert.True(decision.Requested);
        Assert.True(decision.ApplyFlip);
        Assert.Contains("countertrend flip allowed", decision.Reason);
    }

    [Fact]
    public void Strong_pair_rise_preserves_the_original_long()
    {
        var decision = FuturesFlipRegimeGate.Evaluate(
            FuturesDesiredExposure.Long,
            EnabledFlipOptions(),
            pair24hChangePct: 3.01m,
            btc24hChangePct: -0.6m);

        Assert.True(decision.Requested);
        Assert.False(decision.ApplyFlip);
        Assert.Contains("pair 24h change", decision.Reason);
        Assert.Contains("original LONG preserved", decision.Reason);
    }

    [Fact]
    public void Rising_btc_preserves_the_original_long()
    {
        var decision = FuturesFlipRegimeGate.Evaluate(
            FuturesDesiredExposure.Long,
            EnabledFlipOptions(),
            pair24hChangePct: -1.5m,
            btc24hChangePct: 0.01m);

        Assert.True(decision.Requested);
        Assert.False(decision.ApplyFlip);
        Assert.Contains("BTC 24h change", decision.Reason);
    }

    [Fact]
    public void Missing_24h_context_fails_safe_to_the_original_long()
    {
        var decision = FuturesFlipRegimeGate.Evaluate(
            FuturesDesiredExposure.Long,
            EnabledFlipOptions(),
            pair24hChangePct: null,
            btc24hChangePct: null);

        Assert.True(decision.Requested);
        Assert.False(decision.ApplyFlip);
        Assert.Contains("unavailable", decision.Reason);
    }

    [Fact]
    public void Native_short_signal_is_not_touched_by_the_flip_gate()
    {
        var decision = FuturesFlipRegimeGate.Evaluate(
            FuturesDesiredExposure.Short,
            EnabledFlipOptions(),
            pair24hChangePct: -2m,
            btc24hChangePct: -1m);

        Assert.False(decision.Requested);
        Assert.False(decision.ApplyFlip);
    }

    [Fact]
    public void Closed_candle_change_uses_one_complete_24h_window()
    {
        var candles = Enumerable.Range(0, 96)
            .Select(index => new Candle(
                DateTimeOffset.UnixEpoch.AddMinutes(index * 15),
                Open: 100m,
                High: 103m,
                Low: 99m,
                Close: index == 95 ? 102m : 100m,
                Volume: 1m,
                TradeCount: 1))
            .ToList();

        Assert.Equal(2m, FuturesFlipRegimeGate.CalculateClosedCandle24hChangePct(candles, 15));
        Assert.Null(FuturesFlipRegimeGate.CalculateClosedCandle24hChangePct(candles.Take(95).ToList(), 15));
    }

    [Fact]
    public void Invalid_flip_regime_thresholds_reset_to_safe_defaults()
    {
        var config = new FuturesBotConfiguration
        {
            Futures = new FuturesOptions
            {
                FlipMaxPair24hRisePercent = -1m,
                FlipMaxBtc24hRisePercent = 101m
            }
        };

        InvokeNormalize(config);

        Assert.Equal(3m, config.Futures.FlipMaxPair24hRisePercent);
        Assert.Equal(0m, config.Futures.FlipMaxBtc24hRisePercent);
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
    public void Flipped_short_is_held_when_a_short_signal_confirms_its_executed_side()
    {
        var strategy = new LongShortStrategy(new FuturesBotConfiguration());
        var position = new PortfolioPosition { Pair = "SOL/USD", Side = "SHORT", FlippedEntry = true };

        // Price-based TP/SL/trailing owns the flipped experiment's exit.
        var decision = strategy.DecideHeld(position, ShortSignal());
        Assert.Equal(FuturesDesiredExposure.Short, decision);
    }

    [Fact]
    public void Active_exchange_trailing_prevents_strategy_reversal_close()
    {
        var strategy = new LongShortStrategy(new FuturesBotConfiguration());
        var position = new PortfolioPosition
        {
            Pair = "SOL/USD",
            Side = "SHORT",
            FlippedEntry = false,
            TrailingStopState = "EXCHANGE_OPEN"
        };

        var decision = strategy.DecideHeld(position, LongSignal());
        Assert.Equal(FuturesDesiredExposure.Short, decision);
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

    private static FuturesOptions EnabledFlipOptions() => new()
    {
        AllowShorts = true,
        FlipLongEntries = true,
        FlipMaxPair24hRisePercent = 3m,
        FlipMaxBtc24hRisePercent = 0m
    };

    private static void InvokeNormalize(FuturesBotConfiguration config)
    {
        var method = typeof(FuturesBotConfiguration).GetMethod("Normalize", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(config, null);
    }
}
