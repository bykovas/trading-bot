namespace TradingBot.SpotWorker;

internal sealed record EntrySafetyResult(
    bool Approved,
    string? RejectionCode,
    string Reason,
    decimal Atr,
    decimal AtrPercent,
    decimal ReferencePrice,
    DateTimeOffset? AtrCandleOpenTime,
    decimal StopDistancePercent,
    decimal TakeProfitDistancePercent,
    decimal StopPrice,
    decimal TakeProfitPrice,
    decimal RoundTripCostEstimatePct,
    decimal QuoteVolume24hEur,
    decimal EntryDepthEur,
    decimal ExitDepthEur,
    decimal QueueAheadEur,
    decimal CurrentOpenRiskEur,
    decimal NewRiskEur,
    decimal ProjectedOpenRiskEur)
{
    public static EntrySafetyResult Reject(string code, string reason) =>
        new(false, code, reason, 0m, 0m, 0m, null, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m);
}

internal static class EntrySafety
{
    public static EntrySafetyResult EvaluateNewLong(
        InstrumentMarketState marketState,
        PortfolioState portfolio,
        BotConfiguration config,
        decimal targetNotionalEur,
        DateTimeOffset nowUtc)
    {
        if (marketState.Quote is not { } quote || quote.Bid <= 0m || quote.Ask <= 0m || quote.Ask <= quote.Bid)
        {
            return EntrySafetyResult.Reject("ENTRY_INVALID_SPREAD", "entry fail-closed: bid/ask missing or invalid");
        }

        var referencePrice = quote.Last > 0m ? quote.Last : marketState.LastPrice;
        if (referencePrice <= 0m)
        {
            return EntrySafetyResult.Reject("ENTRY_INVALID_REFERENCE_PRICE", "entry fail-closed: reference price missing");
        }

        if (marketState.Candles.Count == 0)
        {
            return EntrySafetyResult.Reject("ENTRY_ATR_MISSING", "entry fail-closed: no candles for ATR");
        }

        var newestClosed = marketState.Candles[^1];
        var timeframe = TimeSpan.FromMinutes(config.Trading.TimeframeMinutes);
        if (newestClosed.OpenTime + timeframe < nowUtc - timeframe)
        {
            return EntrySafetyResult.Reject("ENTRY_ATR_STALE", $"entry fail-closed: newest closed candle {newestClosed.OpenTime:O} is stale for {config.Trading.TimeframeMinutes}m timeframe");
        }

        var atr = AtrIndicator.CalculateLatestClosedAtr(marketState.Candles, config.PositionExit.AtrPeriod);
        if (atr is not { } atrValue || atrValue <= 0m)
        {
            return EntrySafetyResult.Reject("ENTRY_ATR_MISSING", $"entry fail-closed: ATR({config.PositionExit.AtrPeriod}) unavailable from {marketState.Candles.Count} closed candles");
        }

        var quoteVolumeEur = quote.VolumeToday * referencePrice;
        if (config.Filters.MinQuoteVolume24h > 0m && quoteVolumeEur < config.Filters.MinQuoteVolume24h)
        {
            return EntrySafetyResult.Reject("ENTRY_VOLUME", $"entry fail-closed: 24h quote volume EUR {quoteVolumeEur:0} below minimum EUR {config.Filters.MinQuoteVolume24h:0}");
        }

        if (marketState.OrderBook is not { } book || book.Bids.Count == 0 || book.Asks.Count == 0)
        {
            return EntrySafetyResult.Reject("ENTRY_DEPTH_MISSING", "entry fail-closed: order book depth missing");
        }

        var target = Math.Max(0m, Math.Min(config.Risk.MaxOrderEur, targetNotionalEur));
        var requiredEntryDepth = config.Filters.MinDepthMultiple * target;
        var maxEntrySpreadPct = config.Strategy.MaxEntrySpreadPercent;
        var entryDepth = BidDepthWithinPercent(book, quote.Bid, maxEntrySpreadPct);
        if (requiredEntryDepth > 0m && entryDepth < requiredEntryDepth)
        {
            return EntrySafetyResult.Reject("ENTRY_DEPTH", $"entry fail-closed: bid depth EUR {entryDepth:0.##} below required EUR {requiredEntryDepth:0.##}");
        }

        var exitDepth = BidDepthWithinPercent(book, quote.Bid, config.Filters.MaxExitImpactPct);
        if (target > 0m && exitDepth < target)
        {
            return EntrySafetyResult.Reject("ENTRY_EXIT_DEPTH", $"entry fail-closed: emergency bid depth EUR {exitDepth:0.##} below order size EUR {target:0.##}");
        }

        var spreadPct = EntryGate.SpreadPercentOf(marketState);
        var costPct = config.Fees.MakerPct + config.Fees.TakerPct + spreadPct + config.Filters.SlippageBufferPct;
        if (costPct <= 0m)
        {
            return EntrySafetyResult.Reject("ENTRY_COST_INVALID", "entry fail-closed: round-trip cost estimate invalid");
        }

        var atrPct = atrValue / referencePrice * 100m;
        var stopDistancePct = Math.Max(config.PositionExit.StopLossAtrMultiplier, config.PositionExit.MinStopAtrFloor) * atrPct;
        var takeProfitDistancePct = Math.Max(
            config.PositionExit.TakeProfitAtrMultiplier * atrPct,
            config.PositionExit.MinTpVsCostMult * costPct);

        if (stopDistancePct <= 0m || takeProfitDistancePct <= 0m)
        {
            return EntrySafetyResult.Reject("ENTRY_EXITS_INVALID", "entry fail-closed: ATR exit distances invalid");
        }

        var entryPrice = quote.Bid;
        var stopPrice = entryPrice * (1m - stopDistancePct / 100m);
        var takeProfitPrice = entryPrice * (1m + takeProfitDistancePct / 100m);
        var currentRisk = CalculateOpenRiskEur(portfolio, config, out var unsafeReason);
        if (unsafeReason is not null)
        {
            return EntrySafetyResult.Reject("ENTRY_OPEN_RISK_UNSAFE", unsafeReason);
        }

        var newRisk = target * stopDistancePct / 100m + target * config.Fees.TakerPct / 100m + target * config.Filters.SlippageBufferPct / 100m;
        var projectedRisk = currentRisk + newRisk;
        if (config.Risk.MaxConcurrentOpenRisk > 0m && projectedRisk > config.Risk.MaxConcurrentOpenRisk)
        {
            return EntrySafetyResult.Reject("ENTRY_OPEN_RISK_CAP", $"entry fail-closed: projected open risk EUR {projectedRisk:0.####} exceeds cap EUR {config.Risk.MaxConcurrentOpenRisk:0.####}");
        }

        var queueAhead = book.Bids
            .Where(level => level.Price >= entryPrice)
            .Sum(level => level.Price * level.Volume);

        return new EntrySafetyResult(
            true,
            null,
            $"entry safety pass: ATR({config.PositionExit.AtrPeriod})={atrValue:0.##########}, reference={referencePrice:0.########}, atrPercent={atrPct:0.###}%, roundTripCost={costPct:0.###}%",
            atrValue,
            atrPct,
            referencePrice,
            newestClosed.OpenTime,
            stopDistancePct,
            takeProfitDistancePct,
            stopPrice,
            takeProfitPrice,
            costPct,
            quoteVolumeEur,
            entryDepth,
            exitDepth,
            queueAhead,
            currentRisk,
            newRisk,
            projectedRisk);
    }

    public static decimal CalculateOpenRiskEur(PortfolioState portfolio, BotConfiguration config, out string? unsafeReason)
    {
        unsafeReason = null;
        var risk = 0m;
        foreach (var position in portfolio.Positions)
        {
            if (position.Quantity <= 0m || position.LastPrice <= 0m)
            {
                unsafeReason = $"entry fail-closed: open position {position.Pair} lacks valid quantity/mark price for risk";
                return 0m;
            }

            if (position.StopLossPrice is not { } stop || stop <= 0m)
            {
                unsafeReason = $"entry fail-closed: open position {position.Pair} has no valid stop";
                return 0m;
            }

            var priceRisk = Math.Max(0m, position.Quantity * (position.LastPrice - stop));
            var emergencyCost = position.MarketValueEur * (config.Fees.TakerPct + config.Filters.SlippageBufferPct) / 100m;
            risk += priceRisk + Math.Max(0m, emergencyCost);
        }

        return risk;
    }

    private static decimal BidDepthWithinPercent(OrderBookSnapshot book, decimal bestBid, decimal maxImpactPct)
    {
        if (bestBid <= 0m)
        {
            return 0m;
        }

        var minPrice = bestBid * (1m - Math.Max(0m, maxImpactPct) / 100m);
        return book.Bids
            .Where(level => level.Price >= minPrice)
            .Sum(level => level.Price * level.Volume);
    }
}
