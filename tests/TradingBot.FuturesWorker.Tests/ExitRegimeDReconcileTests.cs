using System.Reflection;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

// Reconciliation preserves entry ATR risk while supplying backup TP until trailing is active.
public sealed class ExitRegimeDReconcileTests
{
    private static object InvokeImportedTpSl(FuturesBotConfiguration config, PortfolioPosition? existing, string side, decimal entryPrice)
    {
        var worker = new FuturesDecisionWorker(config, null!, null!, null!, null!, null!, null!);
        var method = typeof(FuturesDecisionWorker).GetMethod("ImportedTpSl", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return method.Invoke(worker, [existing, side, entryPrice, null, null, null])!;
    }

    private static decimal? Field(object state, string name) =>
        (decimal?)state.GetType().GetProperty(name)!.GetValue(state);

    private static string? Text(object state, string name) =>
        (string?)state.GetType().GetProperty(name)!.GetValue(state);

    [Fact]
    public void Reconcile_keeps_the_atr_stop_and_restores_backup_tp_until_trailing()
    {
        var config = new FuturesBotConfiguration
        {
            TpSl = new TpSlOptions { Enabled = true, StopLossPercent = 1.75m, TakeProfitPercent = 3.5m },
            Exits = new FuturesExitOptions { AtrTrailingRegimeEnabled = true }
        };

        // A live regime-D position: ATR stop 0.6% placed at open, no take-profit.
        var existing = new PortfolioPosition
        {
            Pair = "XBT/USD",
            Side = "LONG",
            EntryPrice = 100m,
            Origin = PositionOrigins.Bot,
            StopDistancePct = 0.6m,
            StopLossPrice = 99.40m,
            ExchangeStopLossPrice = 98.80m,
            AtrPct = 0.5m,
            TakeProfitPrice = null
        };

        var state = InvokeImportedTpSl(config, existing, "LONG", 100m);

        // The stop stays the ATR stop the position was opened with - not rebuilt to 1.75%.
        Assert.Equal(99.40m, Field(state, "StopLossPrice"));
        Assert.Equal(0.6m, Field(state, "StopDistancePct"));
        Assert.Equal(103.5m, Field(state, "TakeProfitPrice"));
        Assert.Equal(3.5m, Field(state, "TakeProfitDistancePct"));
        Assert.NotNull(Field(state, "ExchangeTakeProfitPrice"));
        Assert.Equal("SIMULATED_OPEN", Text(state, "TpOrderState"));
    }

    [Fact]
    public void Reconcile_still_builds_the_fixed_tp_sl_when_the_regime_is_off()
    {
        var config = new FuturesBotConfiguration
        {
            TpSl = new TpSlOptions { Enabled = true, StopLossPercent = 1.75m, TakeProfitPercent = 3.5m },
            Exits = new FuturesExitOptions { AtrTrailingRegimeEnabled = false }
        };

        // A freshly adopted position with no stored levels: legacy reconcile derives both from config.
        var state = InvokeImportedTpSl(config, existing: null, side: "LONG", entryPrice: 100m);

        Assert.Equal(98.25m, Field(state, "StopLossPrice"));   // -1.75%
        Assert.Equal(103.5m, Field(state, "TakeProfitPrice")); // +3.5%, still present off the regime
    }
}
