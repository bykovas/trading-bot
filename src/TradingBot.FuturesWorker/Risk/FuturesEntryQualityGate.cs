namespace TradingBot.FuturesWorker;

// Market-quality gate for NEW futures entries, mirroring the spot worker's entry
// protections that the futures pipeline lacked: spread limit, anti-lag price
// action guard (direction-aware for shorts), price-action warm-up, and the
// anti-extension guard. Pure and static so every branch is unit testable.
//
// It deliberately does NOT re-check margin/funding/regime/slots — those belong
// to MarginRiskManager and the portfolio guards; this gate only answers "is the
// market microstructure and recent tape good enough to pay the entry costs".
internal static class FuturesEntryQualityGate
{
    public static RiskEvaluation Evaluate(
        InstrumentMarketState marketState,
        IndicatorSnapshot indicators,
        FuturesDesiredExposure desired,
        PriceActionAssessment? priceAction,
        StrategyOptions strategy)
    {
        if (desired == FuturesDesiredExposure.Flat)
        {
            return new RiskEvaluation(true, new[] { "no exposure requested" });
        }

        // Spread limit: a market-taker entry on a wide book starts several ATRs
        // behind before the trade even exists.
        var spreadPercent = SpreadPercentOf(marketState);
        if (strategy.MaxEntrySpreadPercent > 0m && spreadPercent > strategy.MaxEntrySpreadPercent)
        {
            return Reject($"entry spread {spreadPercent:0.###}% exceeds max {strategy.MaxEntrySpreadPercent:0.###}%");
        }

        // Warm-up: when configured, an entry needs enough snapshot history for the
        // anti-lag guard to judge it. UNKNOWN price action never passes for safe.
        if (strategy.RequirePriceActionData && priceAction is not { DataSufficient: true })
        {
            return Reject(
                $"price-action history is {PriceActionAssessment.WarmupStateOf(priceAction)} ({priceAction?.SnapshotCount ?? 0}/{strategy.PriceActionMinSnapshots} snapshots); entries wait for warm-up");
        }

        // Anti-lag guard. Longs reuse the shared spot guard; shorts get the mirror:
        // a tape that is still RISING is exactly the wrong moment to open a short,
        // no matter what the lagging candle indicators say.
        if (desired == FuturesDesiredExposure.Long)
        {
            var longRejection = PriceActionGuard.RejectLongEntryReason(priceAction, strategy);
            if (longRejection is not null)
            {
                return Reject($"price action blocks long: {longRejection}");
            }
        }
        else if (priceAction is { DataSufficient: true }
            && strategy.PriceActionMaxDeclinePercent > 0m
            && priceAction.TrendPercent >= strategy.PriceActionMaxDeclinePercent)
        {
            return Reject(
                $"price action blocks short: recent trend +{priceAction.TrendPercent:0.###}% is rising (limit +{strategy.PriceActionMaxDeclinePercent:0.###}%)");
        }

        // Anti-extension: entering after the price ran away from its own fast EMA
        // (or after a large lookback run-up in the trade direction) is a chase.
        if (strategy.MaxEntryExtensionPercent > 0m && indicators.FastEma is { } fastEma && fastEma > 0m)
        {
            var extensionPercent = (marketState.LastPrice - fastEma) / fastEma * 100m;
            if (desired == FuturesDesiredExposure.Long && extensionPercent > strategy.MaxEntryExtensionPercent)
            {
                return Reject($"price is {extensionPercent:0.###}% above fast EMA (max extension {strategy.MaxEntryExtensionPercent:0.###}%)");
            }

            if (desired == FuturesDesiredExposure.Short && extensionPercent < -strategy.MaxEntryExtensionPercent)
            {
                return Reject($"price is {Math.Abs(extensionPercent):0.###}% below fast EMA (max extension {strategy.MaxEntryExtensionPercent:0.###}%)");
            }
        }

        if (strategy.MaxEntryRunupPercent > 0m && priceAction is { DataSufficient: true })
        {
            if (desired == FuturesDesiredExposure.Long && priceAction.TrendPercent > strategy.MaxEntryRunupPercent)
            {
                return Reject($"recent run-up {priceAction.TrendPercent:0.###}% exceeds max {strategy.MaxEntryRunupPercent:0.###}% (the move already happened)");
            }

            if (desired == FuturesDesiredExposure.Short && priceAction.TrendPercent < -strategy.MaxEntryRunupPercent)
            {
                return Reject($"recent sell-off {priceAction.TrendPercent:0.###}% exceeds max -{strategy.MaxEntryRunupPercent:0.###}% (the move already happened)");
            }
        }

        return new RiskEvaluation(true, new[] { $"market quality ok: spread {spreadPercent:0.###}%, price action {PriceActionAssessment.WarmupStateOf(priceAction)}" });
    }

    private static RiskEvaluation Reject(string reason) =>
        new(false, new[] { $"entry quality gate: {reason}" });

    private static decimal SpreadPercentOf(InstrumentMarketState marketState)
    {
        var bid = marketState.BestBid;
        var ask = marketState.BestAsk;
        if (bid <= 0m || ask <= 0m || ask < bid)
        {
            return 0m;
        }

        var mid = (bid + ask) / 2m;
        return mid == 0m ? 0m : (ask - bid) / mid * 100m;
    }
}
