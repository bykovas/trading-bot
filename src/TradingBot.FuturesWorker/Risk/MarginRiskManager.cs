namespace TradingBot.FuturesWorker;

// Pre-trade gates for new futures exposure. Every check is conservative: any
// missing input (zero mark price, unknown funding) fails closed for entries.
internal sealed class MarginRiskManager(FuturesBotConfiguration config)
{
    public RiskEvaluation EvaluateEntry(
        PortfolioState state,
        FuturesDesiredExposure desired,
        decimal markPrice,
        decimal targetNotionalEur,
        decimal leverage,
        decimal usedMarginEur,
        decimal? fundingRatePercent = null)
    {
        var reasons = new List<string>();

        if (desired == FuturesDesiredExposure.Flat)
        {
            reasons.Add("no exposure requested");
            return new RiskEvaluation(true, reasons);
        }

        if (desired == FuturesDesiredExposure.Short && !config.Futures.AllowShorts)
        {
            reasons.Add("shorts disabled by configuration");
            return new RiskEvaluation(false, reasons);
        }

        if (leverage > config.Futures.MaxLeverage)
        {
            reasons.Add($"leverage {leverage:0.#}x exceeds cap {config.Futures.MaxLeverage:0.#}x");
            return new RiskEvaluation(false, reasons);
        }

        if (state.Positions.Count >= config.Futures.MaxPositions)
        {
            reasons.Add($"max futures positions {config.Futures.MaxPositions} reached");
            return new RiskEvaluation(false, reasons);
        }

        if (markPrice <= 0m)
        {
            reasons.Add("mark price unavailable");
            return new RiskEvaluation(false, reasons);
        }

        var liquidation = FuturesMath.EstimateLiquidationPrice(
            desired == FuturesDesiredExposure.Short ? "SHORT" : "LONG",
            markPrice,
            leverage,
            config.Margin.MaintenanceMarginRatePercent);
        var distance = FuturesMath.LiquidationDistancePercent(markPrice, liquidation);
        if (distance < config.Margin.MinLiquidationDistancePercent)
        {
            reasons.Add($"liquidation distance {distance:0.##}% below minimum {config.Margin.MinLiquidationDistancePercent:0.##}%");
            return new RiskEvaluation(false, reasons);
        }

        var initialMargin = leverage <= 0m ? targetNotionalEur : targetNotionalEur / leverage;
        var equity = state.TotalValueEur;
        var utilizationAfter = equity <= 0m ? 100m : (usedMarginEur + initialMargin) / equity * 100m;
        if (utilizationAfter > config.Margin.MaxAccountMarginUtilizationPercent)
        {
            reasons.Add($"margin utilization {utilizationAfter:0.##}% would exceed cap {config.Margin.MaxAccountMarginUtilizationPercent:0.##}%");
            return new RiskEvaluation(false, reasons);
        }

        if (config.Funding.MaxAbsFundingRatePercentForEntry > 0m
            && fundingRatePercent is { } funding
            && Math.Abs(funding) > config.Funding.MaxAbsFundingRatePercentForEntry)
        {
            reasons.Add($"funding rate {funding:0.####}% exceeds entry cap {config.Funding.MaxAbsFundingRatePercentForEntry:0.####}%");
            return new RiskEvaluation(false, reasons);
        }

        reasons.Add($"entry approved: liquidation distance {distance:0.##}%, margin utilization {utilizationAfter:0.##}%");
        return new RiskEvaluation(true, reasons);
    }
}
