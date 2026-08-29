namespace TradingBot.SpotWorker;

// Pure, side-effect-free re-validation of the original BUY intent and the
// execution/risk constraints immediately before the IOC taker fallback is sent.
// It never re-runs strategy scoring, EMA, price action, regime, ranking or sizing:
// it only checks that the intent is still actionable against a FRESH quote and the
// CURRENT portfolio. Keeping it pure makes every rejection branch unit-testable.
internal static class FallbackEntryGuards
{
    internal enum Verdict
    {
        Allow,
        RejectStaleQuote,
        RejectSpread,
        RejectSlippage,
        RejectRisk
    }

    internal readonly record struct Result(Verdict Verdict, string Reason, decimal IocPrice, decimal MaxAllowedPrice);

    internal readonly record struct Inputs(
        decimal OriginalMakerBid,
        decimal FreshBid,
        decimal FreshAsk,
        bool QuoteFresh,
        decimal SpreadPercent,
        decimal MaxEntrySpreadPercent,
        decimal MaxBuySlippagePercent,
        decimal TargetNotionalEur,
        decimal Volume,
        int PairDecimals,
        decimal OrderMinimum,
        decimal CostMinimum,
        bool PositionAlreadyOpen,
        bool EntryInFlight,
        int OpenPositions,
        int MaxOpenPositions,
        decimal CashEur,
        decimal CashReserveEur,
        decimal CurrentExposureEur,
        decimal MaxTotalExposureEur,
        int RecentEntriesLastHour,
        int MaxNewPositionsPerHour);

    internal static Result Evaluate(Inputs input)
    {
        var maxAllowed = input.OriginalMakerBid > 0m
            ? input.OriginalMakerBid * (1m + input.MaxBuySlippagePercent / 100m)
            : 0m;

        // 1) Quote freshness / validity.
        if (!input.QuoteFresh
            || input.OriginalMakerBid <= 0m
            || input.FreshBid <= 0m
            || input.FreshAsk <= 0m
            || input.FreshAsk < input.FreshBid)
        {
            return new Result(Verdict.RejectStaleQuote, "fresh quote missing or invalid (bid/ask <= 0 or crossed)", 0m, maxAllowed);
        }

        // 2) Spread must still be within the existing entry ceiling.
        if (input.MaxEntrySpreadPercent > 0m && input.SpreadPercent > input.MaxEntrySpreadPercent)
        {
            return new Result(
                Verdict.RejectSpread,
                $"spread {input.SpreadPercent:0.####}% exceeds entry max {input.MaxEntrySpreadPercent:0.####}%",
                0m,
                maxAllowed);
        }

        // 3) Slippage cap: reference price is the ORIGINAL maker bid, never last.
        if (input.FreshAsk > maxAllowed)
        {
            return new Result(
                Verdict.RejectSlippage,
                $"ask {input.FreshAsk:0.########} exceeds slippage cap {maxAllowed:0.########} (bid {input.OriginalMakerBid:0.########} + {input.MaxBuySlippagePercent:0.####}%)",
                0m,
                maxAllowed);
        }

        // 4) Execution/risk constraints against the CURRENT portfolio.
        if (input.PositionAlreadyOpen)
        {
            return new Result(Verdict.RejectRisk, "position for pair already open", 0m, maxAllowed);
        }

        if (input.EntryInFlight)
        {
            return new Result(Verdict.RejectRisk, "another entry order for pair already in flight", 0m, maxAllowed);
        }

        if (input.MaxOpenPositions > 0 && input.OpenPositions >= input.MaxOpenPositions)
        {
            return new Result(Verdict.RejectRisk, $"max open positions reached ({input.OpenPositions} >= {input.MaxOpenPositions})", 0m, maxAllowed);
        }

        if (input.CashReserveEur > 0m && input.CashEur - input.TargetNotionalEur < input.CashReserveEur)
        {
            return new Result(Verdict.RejectRisk, $"cash reserve breached: cash {input.CashEur:0.##} - notional {input.TargetNotionalEur:0.##} < reserve {input.CashReserveEur:0.##}", 0m, maxAllowed);
        }

        if (input.MaxTotalExposureEur > 0m && input.CurrentExposureEur + input.TargetNotionalEur > input.MaxTotalExposureEur)
        {
            return new Result(Verdict.RejectRisk, $"max exposure exceeded: {input.CurrentExposureEur:0.##} + {input.TargetNotionalEur:0.##} > {input.MaxTotalExposureEur:0.##}", 0m, maxAllowed);
        }

        if (input.MaxNewPositionsPerHour > 0 && input.RecentEntriesLastHour >= input.MaxNewPositionsPerHour)
        {
            return new Result(Verdict.RejectRisk, $"hourly entry limit reached ({input.RecentEntriesLastHour} >= {input.MaxNewPositionsPerHour})", 0m, maxAllowed);
        }

        // 5) Kraken minimums for the taker order.
        if (input.OrderMinimum > 0m && input.Volume < input.OrderMinimum)
        {
            return new Result(Verdict.RejectRisk, $"volume {input.Volume} below pair ordermin {input.OrderMinimum}", 0m, maxAllowed);
        }

        var iocPrice = TruncateTo(input.FreshAsk, input.PairDecimals);
        if (iocPrice <= 0m)
        {
            return new Result(Verdict.RejectStaleQuote, "IOC price rounded to zero", 0m, maxAllowed);
        }

        var notional = input.Volume * iocPrice;
        if (input.CostMinimum > 0m && notional < input.CostMinimum)
        {
            return new Result(Verdict.RejectRisk, $"order notional {notional:0.####} below pair costmin {input.CostMinimum}", 0m, maxAllowed);
        }

        return new Result(Verdict.Allow, "fallback guards passed", iocPrice, maxAllowed);
    }

    internal static decimal TruncateTo(decimal value, int decimals)
    {
        if (decimals < 0)
        {
            decimals = 0;
        }

        var factor = 1m;
        for (var i = 0; i < decimals; i++)
        {
            factor *= 10m;
        }

        return Math.Truncate(value * factor) / factor;
    }
}
