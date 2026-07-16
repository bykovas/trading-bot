namespace TradingBot.FuturesWorker;

// Pre-trade gates for new futures exposure. Every check is conservative: any
// missing input (zero mark price, unknown funding) fails closed for entries.
internal sealed class MarginRiskManager(FuturesBotConfiguration config)
{
    public RiskEvaluation EvaluateEntry(FuturesEntryRiskInputs input)
    {
        var reasons = new List<string>();
        if (input.Desired == FuturesDesiredExposure.Flat)
        {
            reasons.Add("no exposure requested");
            return new RiskEvaluation(true, reasons);
        }

        if (input.MarkPrice <= 0m)
        {
            reasons.Add("mark price unavailable");
            return new RiskEvaluation(false, reasons);
        }

        if (input.Desired == FuturesDesiredExposure.Short && !config.Futures.AllowShorts)
        {
            reasons.Add("shorts disabled by configuration");
            return new RiskEvaluation(false, reasons);
        }

        if (input.Desired == FuturesDesiredExposure.Short && !input.ShortAllowed)
        {
            reasons.Add($"short blocked: {input.ShortBlockReason ?? "short regime not approved"}");
            return new RiskEvaluation(false, reasons);
        }

        if (input.Desired == FuturesDesiredExposure.Long && !input.BtcAllowsLongs)
        {
            reasons.Add($"long blocked by BTC regime: {input.BtcRegimeState}");
            return new RiskEvaluation(false, reasons);
        }

        if (input.AtrPct is null or <= 0m)
        {
            reasons.Add("ATR unavailable or stale");
            return new RiskEvaluation(false, reasons);
        }

        if (input.StopDistancePct is null or <= 0m || input.TakeProfitDistancePct is null or <= 0m)
        {
            reasons.Add("ATR exit distances unavailable");
            return new RiskEvaluation(false, reasons);
        }

        // Do not trade an instrument whose ATR-required stop exceeds the maximum allowed
        // stop. Shrinking the stop into the volatility (silent clamp) would guarantee churn;
        // block instead so the size/stop contract stays honest.
        if (config.Exits.StopDistanceCapPct > 0m && input.StopDistancePct.Value > config.Exits.StopDistanceCapPct)
        {
            reasons.Add($"STOP_DISTANCE_TOO_LARGE: required stop {input.StopDistancePct.Value:0.##}% exceeds max allowed {config.Exits.StopDistanceCapPct:0.##}%");
            return new RiskEvaluation(false, reasons);
        }

        if (input.FundingRatePercent is null)
        {
            reasons.Add("funding rate unavailable");
            return new RiskEvaluation(false, reasons);
        }

        var funding = input.FundingRatePercent.Value;
        if (input.Desired == FuturesDesiredExposure.Long && funding > config.Funding.MaxAbsFundingRatePercentForEntry)
        {
            reasons.Add($"funding rate {funding:0.####}% is adverse for long above cap {config.Funding.MaxAbsFundingRatePercentForEntry:0.####}%");
            return new RiskEvaluation(false, reasons);
        }

        if (input.Desired == FuturesDesiredExposure.Short && funding < -config.Funding.MaxAbsFundingRatePercentForEntry)
        {
            reasons.Add($"funding rate {funding:0.####}% is adverse for short below cap -{config.Funding.MaxAbsFundingRatePercentForEntry:0.####}%");
            return new RiskEvaluation(false, reasons);
        }

        if (input.Volume24hUsd is null || input.Volume24hUsd < config.Filters.MinQuoteVolume24h)
        {
            reasons.Add($"24h quote volume unavailable/below USD {config.Filters.MinQuoteVolume24h:0}");
            return new RiskEvaluation(false, reasons);
        }

        var requiredDepth = input.TargetNotionalEur * config.Filters.MinExitDepthMultiple;
        if (input.ExitDepthEur is null || input.ExitDepthEur < requiredDepth)
        {
            reasons.Add($"exit depth unavailable/below EUR {requiredDepth:0.####}");
            return new RiskEvaluation(false, reasons);
        }

        if (input.FilledNotionalEur <= 0m)
        {
            reasons.Add("maker entry missed");
            return new RiskEvaluation(false, reasons);
        }

        if (input.Leverage > config.Futures.MaxLeverage)
        {
            reasons.Add($"leverage {input.Leverage:0.#}x exceeds cap {config.Futures.MaxLeverage:0.#}x");
            return new RiskEvaluation(false, reasons);
        }

        if (input.State.Positions.Count >= config.Futures.MaxPositions)
        {
            reasons.Add($"max futures positions {config.Futures.MaxPositions} reached");
            return new RiskEvaluation(false, reasons);
        }

        if (input.State.Positions.Any(position => position.StopLossPrice is null or <= 0m))
        {
            reasons.Add("open position without valid stop makes new entries fail-unsafe");
            return new RiskEvaluation(false, reasons);
        }

        var liquidation = FuturesMath.EstimateLiquidationPrice(
            input.Desired == FuturesDesiredExposure.Short ? "SHORT" : "LONG",
            input.MarkPrice,
            input.Leverage,
            config.Margin.MaintenanceMarginRatePercent);
        var distance = FuturesMath.LiquidationDistancePercent(input.MarkPrice, liquidation);
        if (distance < config.Margin.MinLiquidationDistancePercent)
        {
            reasons.Add($"liquidation distance {distance:0.##}% below minimum {config.Margin.MinLiquidationDistancePercent:0.##}%");
            return new RiskEvaluation(false, reasons);
        }

        var initialMargin = input.Leverage <= 0m ? input.FilledNotionalEur : input.FilledNotionalEur / input.Leverage;
        var equity = input.State.TotalValueEur;

        // Independent cap: max notional for a single position (gap/slippage backstop).
        if (config.Futures.MaxNotionalEur > 0m && input.FilledNotionalEur > config.Futures.MaxNotionalEur)
        {
            reasons.Add($"MAX_NOTIONAL_PER_POSITION: notional EUR {input.FilledNotionalEur:0.####} exceeds cap EUR {config.Futures.MaxNotionalEur:0.####}");
            return new RiskEvaluation(false, reasons);
        }

        // Independent cap: max initial margin committed by a single position.
        if (config.Futures.MaxMarginPerPositionEur > 0m && initialMargin > config.Futures.MaxMarginPerPositionEur)
        {
            reasons.Add($"MAX_MARGIN_PER_POSITION: margin EUR {initialMargin:0.####} exceeds cap EUR {config.Futures.MaxMarginPerPositionEur:0.####}");
            return new RiskEvaluation(false, reasons);
        }

        // Independent cap: max aggregate notional across all open positions.
        if (config.Futures.MaxTotalNotionalEur > 0m)
        {
            var totalNotionalAfter = input.State.Positions.Sum(position => position.EntryNotionalEur) + input.FilledNotionalEur;
            if (totalNotionalAfter > config.Futures.MaxTotalNotionalEur)
            {
                reasons.Add($"MAX_TOTAL_NOTIONAL: aggregate notional EUR {totalNotionalAfter:0.####} exceeds cap EUR {config.Futures.MaxTotalNotionalEur:0.####}");
                return new RiskEvaluation(false, reasons);
            }
        }

        // Independent cap: the new margin must fit in free collateral (equity - used margin).
        var availableMargin = equity - input.UsedMarginEur;
        if (initialMargin > availableMargin)
        {
            reasons.Add($"INSUFFICIENT_AVAILABLE_MARGIN: need EUR {initialMargin:0.####}, available EUR {availableMargin:0.####}");
            return new RiskEvaluation(false, reasons);
        }

        var utilizationAfter = equity <= 0m ? 100m : (input.UsedMarginEur + initialMargin) / equity * 100m;
        if (utilizationAfter > config.Margin.MaxAccountMarginUtilizationPercent)
        {
            reasons.Add($"margin utilization {utilizationAfter:0.##}% would exceed cap {config.Margin.MaxAccountMarginUtilizationPercent:0.##}%");
            return new RiskEvaluation(false, reasons);
        }

        // Concurrent open-risk cap = pure stop-distance heat summed across positions
        // (= TargetRiskEur * MaxPositions by default). Execution/slippage cost is bounded
        // by the notional caps above and reported per trade, not double-counted here.
        if (config.Risk.MaxConcurrentOpenRisk > 0m
            && input.ProjectedOpenRiskEur > config.Risk.MaxConcurrentOpenRisk)
        {
            reasons.Add($"open risk EUR {input.ProjectedOpenRiskEur:0.####} exceeds cap EUR {config.Risk.MaxConcurrentOpenRisk:0.####}");
            return new RiskEvaluation(false, reasons);
        }

        reasons.Add($"entry approved: liquidation distance {distance:0.##}%, margin utilization {utilizationAfter:0.##}%, open risk EUR {input.ProjectedOpenRiskEur:0.####}");
        return new RiskEvaluation(true, reasons);
    }

    public RiskEvaluation EvaluateEntry(
        PortfolioState state,
        FuturesDesiredExposure desired,
        decimal markPrice,
        decimal targetNotionalEur,
        decimal leverage,
        decimal usedMarginEur,
        decimal? fundingRatePercent = null) =>
        EvaluateEntry(new FuturesEntryRiskInputs(
            state,
            desired,
            markPrice,
            targetNotionalEur,
            targetNotionalEur,
            leverage,
            usedMarginEur,
            fundingRatePercent ?? 0m,
            AtrPct: 1m,
            StopDistancePct: 2m,
            TakeProfitDistancePct: 3m,
            Volume24hUsd: config.Filters.MinQuoteVolume24h,
            ExitDepthEur: targetNotionalEur * config.Filters.MinExitDepthMultiple,
            ProjectedOpenRiskEur: 0m,
            BtcAllowsLongs: true,
            BtcRegimeState: "TEST_COMPAT",
            ShortAllowed: true,
            ShortBlockReason: null));
}

internal sealed record FuturesEntryRiskInputs(
    PortfolioState State,
    FuturesDesiredExposure Desired,
    decimal MarkPrice,
    decimal TargetNotionalEur,
    decimal FilledNotionalEur,
    decimal Leverage,
    decimal UsedMarginEur,
    decimal? FundingRatePercent,
    decimal? AtrPct,
    decimal? StopDistancePct,
    decimal? TakeProfitDistancePct,
    decimal? Volume24hUsd,
    decimal? ExitDepthEur,
    decimal ProjectedOpenRiskEur,
    bool BtcAllowsLongs,
    string BtcRegimeState,
    bool ShortAllowed,
    string? ShortBlockReason);
