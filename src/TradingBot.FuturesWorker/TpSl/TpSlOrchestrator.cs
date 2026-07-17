namespace TradingBot.FuturesWorker;

// Working TP/SL policy per open position. Levels are frozen at entry by
// FuturesVirtualPortfolio. For live quotes, LONG exits are tested against bid
// and SHORT exits against ask: the price stream must represent where exposure
// can actually be reduced, not a stale candle/last value.
internal sealed class TpSlOrchestrator(FuturesBotConfiguration config)
{
    public sealed record TpSlTrigger(string Pair, string Kind, decimal TriggerPrice, string TriggerSource);

    public TpSlTrigger? Evaluate(
        PortfolioPosition position,
        decimal markPrice,
        decimal lastPrice,
        decimal? bidPrice = null,
        decimal? askPrice = null)
    {
        if (!config.TpSl.Enabled)
        {
            return null;
        }

        if (position.TrailingStopState?.Equals("EXCHANGE_OPEN", StringComparison.OrdinalIgnoreCase) == true)
        {
            return null;
        }

        var triggerSource = config.TpSl.TriggerSource.Equals("last", StringComparison.OrdinalIgnoreCase) ? "last" : "mark";
        var fallbackPrice = triggerSource == "last" ? lastPrice : markPrice;
        var isShort = position.Side == "SHORT";
        var closeablePrice = isShort
            ? askPrice is > 0m ? askPrice.Value : fallbackPrice
            : bidPrice is > 0m ? bidPrice.Value : fallbackPrice;
        var priceSource = isShort
            ? askPrice is > 0m ? "ask" : triggerSource
            : bidPrice is > 0m ? "bid" : triggerSource;
        var price = closeablePrice;
        if (price <= 0m)
        {
            return null;
        }

        if (IsWorkingOrderOpen(position, position.SlOrderState) && position.StopLossPrice is { } stop)
        {
            var stopHit = isShort ? price >= stop : price <= stop;
            if (stopHit)
            {
                position.SlOrderState = "TRIGGERED";
                position.TpOrderState = position.TpOrderState == "SIMULATED_OPEN" ? "CANCELLED" : position.TpOrderState;
                return new TpSlTrigger(position.Pair, "STOP_LOSS", stop, priceSource);
            }
        }

        if (IsWorkingOrderOpen(position, position.TpOrderState) && position.TakeProfitPrice is { } take)
        {
            var takeHit = isShort ? price <= take : price >= take;
            if (takeHit)
            {
                position.TpOrderState = "TRIGGERED";
                position.SlOrderState = position.SlOrderState == "SIMULATED_OPEN" ? "CANCELLED" : position.SlOrderState;
                return new TpSlTrigger(position.Pair, "TAKE_PROFIT", take, priceSource);
            }
        }

        return null;
    }

    private static bool IsWorkingOrderOpen(PortfolioPosition position, string? state)
    {
        if (position.Origin?.Equals(PositionOrigins.KrakenSync, StringComparison.OrdinalIgnoreCase) == true)
        {
            return false;
        }

        if (state?.Equals("SIMULATED_OPEN", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        return state?.Equals("EXCHANGE_OPEN", StringComparison.OrdinalIgnoreCase) == true
            && position.Origin?.Equals(PositionOrigins.Bot, StringComparison.OrdinalIgnoreCase) == true;
    }
}
