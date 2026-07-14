using System.Reflection;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class FuturesShortGateTests
{
    [Fact]
    public void Strong_confirmed_short_can_override_non_bearish_btc_regime()
    {
        var config = new FuturesBotConfiguration
        {
            Shorts = new FuturesShortOptions { MinShortScore = 0.85m },
            Regime = new FuturesRegimeOptions { ShortOverrideMinScore = 0.85m }
        };
        InvokeNormalize(config);
        var worker = new FuturesDecisionWorker(config, null!, null!, null!, null!, null!, null!);
        var signal = new TechnicalSignal(
            0.15m,
            "SHORT",
            AllowsLong: false,
            HasBullishStructure: false,
            EmaFullyConfirmed: false,
            BullishEmaGapPercent: null,
            EmaGapVelocityPercent: null,
            Contributions: [],
            AllowsShort: true,
            HasBearishStructure: true,
            BearishEmaGapPercent: 0.5m,
            ShortScore: 0.86m);
        var btcRegime = new BtcRegimeState(
            AllowsLongs: true,
            AllowsShorts: false,
            BlocksLongsDueToRegime: false,
            Description: "allowsLongs=True allowsShorts=False");

        var result = InvokeShortGate(worker, signal, btcRegime);

        Assert.True(result.Allowed);
        Assert.Contains("short override", result.Reason);
    }

    [Fact]
    public void Weak_short_still_respects_btc_regime_block()
    {
        var config = new FuturesBotConfiguration
        {
            Shorts = new FuturesShortOptions { MinShortScore = 0.80m },
            Regime = new FuturesRegimeOptions { ShortOverrideMinScore = 0.85m }
        };
        InvokeNormalize(config);
        var worker = new FuturesDecisionWorker(config, null!, null!, null!, null!, null!, null!);
        var signal = new TechnicalSignal(
            0.15m,
            "SHORT",
            AllowsLong: false,
            HasBullishStructure: false,
            EmaFullyConfirmed: false,
            BullishEmaGapPercent: null,
            EmaGapVelocityPercent: null,
            Contributions: [],
            AllowsShort: true,
            HasBearishStructure: true,
            BearishEmaGapPercent: 0.5m,
            ShortScore: 0.82m);
        var btcRegime = new BtcRegimeState(
            AllowsLongs: true,
            AllowsShorts: false,
            BlocksLongsDueToRegime: false,
            Description: "allowsLongs=True allowsShorts=False");

        var result = InvokeShortGate(worker, signal, btcRegime);

        Assert.False(result.Allowed);
        Assert.Contains("allowsShorts=False", result.Reason);
    }

    private static (bool Allowed, string? Reason) InvokeShortGate(
        FuturesDecisionWorker worker,
        TechnicalSignal signal,
        BtcRegimeState btcRegime)
    {
        var method = typeof(FuturesDecisionWorker).GetMethod("EvaluateShortGate", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(FuturesDecisionWorker), "EvaluateShortGate");
        return ((bool Allowed, string? Reason))method.Invoke(worker, [FuturesDesiredExposure.Short, signal, btcRegime])!;
    }

    private static void InvokeNormalize(FuturesBotConfiguration config)
    {
        var method = typeof(FuturesBotConfiguration).GetMethod("Normalize", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(config, null);
    }
}
