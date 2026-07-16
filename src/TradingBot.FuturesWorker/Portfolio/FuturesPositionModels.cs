namespace TradingBot.FuturesWorker;

// Futures-side desired exposure. Deliberately separate from the spot worker's
// persisted "LONG_MICRO"/"NONE" strings and from Core's SignalIntent: Core says
// what a candidate looks like, this says what exposure the futures worker wants.
internal enum FuturesDesiredExposure
{
    Flat,
    Long,
    Short
}

// Outcome of applying one decision to the virtual portfolio.
internal sealed record FuturesFillResult(DryRunAction Action, bool PositionOpened, bool PositionClosed);

internal sealed record FuturesMaxHoldExit(bool ShouldClose, string? Reason);

internal sealed record FuturesEntryPlan(
    decimal RequestedNotionalEur,
    decimal FilledNotionalEur,
    decimal AtrPct,
    decimal StopDistancePct,
    decimal TakeProfitDistancePct,
    decimal RoundTripCostEstimatePct,
    decimal ExpectedFundingPct,
    decimal QueueAheadEur,
    decimal MakerFillRate,
    long TimeToFillMs,
    int RepegCount,
    decimal OpenRiskEur,
    string FundingState,
    string BtcRegimeState,
    string ShortAllowed,
    decimal TargetRiskEur = 0m,
    decimal SizedNotionalEur = 0m,
    decimal RequiredMarginEur = 0m,
    decimal EffectiveLeverage = 0m,
    decimal ProjectedStopLossEur = 0m,
    string ExecutionCostModel = FuturesExecutionCostModel.TakerIocRoundTrip,
    string StopSource = "UNKNOWN",
    string? NotionalCapReason = null);

internal sealed record BtcRegimeState(
    bool AllowsLongs,
    bool AllowsShorts,
    bool BlocksLongsDueToRegime,
    string Description);

internal static class FuturesMath
{
    // Rough isolated-margin liquidation estimate. Real Kraken Futures uses tiered
    // maintenance margin per contract; this conservative approximation is enough
    // for dry-run diagnostics and the pre-trade distance gate.
    public static decimal EstimateLiquidationPrice(
        string side,
        decimal entryPrice,
        decimal leverage,
        decimal maintenanceMarginRatePercent)
    {
        if (entryPrice <= 0m || leverage <= 0m)
        {
            return 0m;
        }

        var lossFraction = (1m / leverage) * (1m - maintenanceMarginRatePercent / 100m);
        return side == "SHORT"
            ? decimal.Round(entryPrice * (1m + lossFraction), 10)
            : decimal.Round(entryPrice * (1m - lossFraction), 10);
    }

    public static decimal LiquidationDistancePercent(decimal markPrice, decimal liquidationPrice) =>
        markPrice <= 0m ? 0m : decimal.Round(Math.Abs(markPrice - liquidationPrice) / markPrice * 100m, 2);

    public static decimal UnrealizedPnlEur(string side, decimal entryPrice, decimal markPrice, decimal quantity) =>
        side == "SHORT"
            ? (entryPrice - markPrice) * quantity
            : (markPrice - entryPrice) * quantity;
}
