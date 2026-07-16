namespace TradingBot.FuturesWorker;

// ATR stop + risk-budget notional + TP floors as one contract.
//
//   requiredStop = StopAtrMult * atrPct
//   stopPct      = max(requiredStop, floor=Exits.MinStopDistancePct)   (ATR unavailable → floor)
//                  If stopPct > cap=Exits.StopDistanceCapPct the sizer does NOT silently
//                  shrink the stop; it flags StopExceedsMaxAllowed so the risk manager can
//                  block the entry with STOP_DISTANCE_TOO_LARGE. A capped-down stop would
//                  place the exit inside the instrument's own volatility (guaranteed churn).
//   notional     = TargetRiskEur / (stopPct/100)   then apply max-notional / max-margin caps
//   margin       = notional / leverage             (leverage sets collateral, not EUR risk)
//   tpPct        = max(MinRewardRiskMultiple * stopPct,
//                      MinTpVsCostMult * roundTripCostPct,
//                      Exits.MinTakeProfitPct floor)
//                  then optional TakeProfitCapPct
//
// Stop/TP FLOORS are read from Exits.MinStopDistancePct / Exits.MinTakeProfitPct. Normalize
// maps the legacy TpSl.StopLossPercent / TpSl.TakeProfitPercent into those floors when the
// new fields are unset (backward-compatible migration). Legacy fields are NOT the sole
// stop/TP once ATR is available — MinRewardRiskMultiple and MinTpVsCostMult always apply.
internal static class FuturesPositionSizer
{
    public static PositionSizePlan Size(
        FuturesBotConfiguration config,
        decimal atrPct,
        ExecutionCostEstimate costs,
        decimal leverage)
    {
        var requestedLeverage = leverage <= 0m ? 1m : leverage;
        leverage = Math.Clamp(requestedLeverage, 1m, config.Futures.MaxLeverage);

        var stopFloorPct = config.Exits.MinStopDistancePct > 0m
            ? config.Exits.MinStopDistancePct
            : (config.TpSl.StopLossPercent > 0m ? config.TpSl.StopLossPercent : 0.75m);
        var stopCapPct = config.Exits.StopDistanceCapPct > 0m
            ? config.Exits.StopDistanceCapPct
            : Math.Max(stopFloorPct * 4m, 3m);
        var atrMult = config.Exits.StopAtrMult > 0m ? config.Exits.StopAtrMult : 1m;

        decimal stopDistancePct;
        string stopSource;
        if (atrPct > 0m)
        {
            var requiredStop = atrMult * atrPct;
            // Floor is applied UP (a stop tighter than the floor is unsafe); the cap is a
            // BLOCK trigger, never a silent down-clamp — see StopExceedsMaxAllowed below.
            stopDistancePct = Math.Max(requiredStop, stopFloorPct);
            stopSource = requiredStop < stopFloorPct ? "ATR_FLOORED" : "ATR";
        }
        else
        {
            stopDistancePct = stopFloorPct;
            stopSource = "LEGACY_STOP_FLOOR";
        }

        var stopExceedsMaxAllowed = stopDistancePct > stopCapPct;
        if (stopExceedsMaxAllowed)
        {
            stopSource = "ATR_EXCEEDS_MAX";
        }

        var targetRiskEur = config.Risk.TargetRiskEur > 0m
            ? config.Risk.TargetRiskEur
            : stopFloorPct / 100m * config.Futures.DerivedNotionalEur(leverage);

        // Risk budget drives notional. Leverage only converts notional → margin.
        var rawNotional = stopDistancePct <= 0m
            ? 0m
            : targetRiskEur / (stopDistancePct / 100m);

        var maxNotional = config.Futures.MaxNotionalEur > 0m
            ? config.Futures.MaxNotionalEur
            : config.Futures.DerivedNotionalEur(config.Futures.MaxLeverage);
        // Per-position margin CAP → notional ceiling. Prefer the explicit
        // MaxMarginPerPositionEur; fall back to TargetMarginEur (legacy) when unset.
        var marginCapEur = config.Futures.MaxMarginPerPositionEur > 0m
            ? config.Futures.MaxMarginPerPositionEur
            : config.Futures.TargetMarginEur;
        var maxNotionalFromMargin = marginCapEur > 0m ? marginCapEur * leverage : maxNotional;
        var notionalCap = Math.Min(maxNotional, maxNotionalFromMargin);

        var sizedNotional = Math.Min(rawNotional, notionalCap);
        var capReason = sizedNotional < rawNotional - 0.0000001m
            ? (maxNotionalFromMargin <= maxNotional ? "MAX_MARGIN" : "MAX_NOTIONAL")
            : null;

        var requiredMarginEur = leverage <= 0m ? sizedNotional : sizedNotional / leverage;
        var projectedStopLossEur = decimal.Round(sizedNotional * stopDistancePct / 100m, 8);
        // Realistic per-trade worst case: stop move PLUS round-trip execution cost and any
        // configured emergency-exit slippage. This is the number reported per trade so the
        // projected risk explicitly includes stop-exit slippage. The concurrent portfolio
        // cap (MaxConcurrentOpenRisk) uses PURE stop-distance heat = TargetRiskEur * N.
        var projectedOpenRiskEur = decimal.Round(
            projectedStopLossEur + sizedNotional * (costs.RoundTripCostPct + config.Risk.EstimatedEmergencyExitCostPct) / 100m,
            8);

        var minR = config.Exits.MinRewardRiskMultiple > 0m ? config.Exits.MinRewardRiskMultiple : 2m;
        var tpFromR = minR * stopDistancePct;
        var tpFromCost = config.Exits.MinTpVsCostMult * costs.RoundTripCostPct;
        var tpFloor = config.Exits.MinTakeProfitPct > 0m
            ? config.Exits.MinTakeProfitPct
            : (config.TpSl.TakeProfitPercent > 0m ? config.TpSl.TakeProfitPercent : 0m);
        var takeProfitDistancePct = Math.Max(tpFromR, Math.Max(tpFromCost, tpFloor));
        if (config.Exits.TakeProfitCapPct > 0m)
        {
            takeProfitDistancePct = Math.Min(takeProfitDistancePct, config.Exits.TakeProfitCapPct);
        }

        return new PositionSizePlan(
            AtrPct: decimal.Round(Math.Max(0m, atrPct), 6),
            StopDistancePct: decimal.Round(stopDistancePct, 6),
            MaxAllowedStopPct: decimal.Round(stopCapPct, 6),
            StopExceedsMaxAllowed: stopExceedsMaxAllowed,
            TakeProfitDistancePct: decimal.Round(takeProfitDistancePct, 6),
            TargetRiskEur: decimal.Round(targetRiskEur, 6),
            SizedNotionalEur: decimal.Round(sizedNotional, 6),
            RequiredMarginEur: decimal.Round(requiredMarginEur, 6),
            RequestedLeverage: decimal.Round(requestedLeverage, 6),
            EffectiveLeverage: leverage,
            ProjectedStopLossEur: projectedStopLossEur,
            ProjectedOpenRiskEur: projectedOpenRiskEur,
            RoundTripCostPct: costs.RoundTripCostPct,
            ExecutionCostModel: costs.Model,
            StopSource: stopSource,
            NotionalCapReason: capReason,
            RawNotionalBeforeCapsEur: decimal.Round(rawNotional, 6));
    }
}

internal sealed record PositionSizePlan(
    decimal AtrPct,
    decimal StopDistancePct,
    decimal MaxAllowedStopPct,
    bool StopExceedsMaxAllowed,
    decimal TakeProfitDistancePct,
    decimal TargetRiskEur,
    decimal SizedNotionalEur,
    decimal RequiredMarginEur,
    decimal RequestedLeverage,
    decimal EffectiveLeverage,
    decimal ProjectedStopLossEur,
    decimal ProjectedOpenRiskEur,
    decimal RoundTripCostPct,
    string ExecutionCostModel,
    string StopSource,
    string? NotionalCapReason,
    decimal RawNotionalBeforeCapsEur);
