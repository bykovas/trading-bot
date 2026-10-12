namespace TradingBot.FuturesWorker;

// Working TP/SL policy per open position. Levels are frozen at entry by
// FuturesVirtualPortfolio. For live quotes, LONG exits are tested against bid
// and SHORT exits against ask: the price stream must represent where exposure
// can actually be reduced, not a stale candle/last value.
internal sealed class TpSlOrchestrator(FuturesBotConfiguration config)
{
    public sealed record TpSlTrigger(
        string Pair,
        string Kind,
        decimal TriggerPrice,
        string TriggerSource,
        string? PreviousTpOrderState = null,
        string? PreviousSlOrderState = null);

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

        if (!config.Futures.LiveTradingEnabled && config.Exits.AtrTrailingRegimeEnabled
            && position.Origin?.Equals(PositionOrigins.Bot, StringComparison.OrdinalIgnoreCase) == true
            && position.AtrPct is > 0m && position.StopDistancePct is > 0m
            && config.Exits.TrailingActivationRMultiple > 0m)
        {
            var profit = FuturesDecisionWorker.ProfitPercentInDirection(position, price);
            if (position.PeakPnlPercent is not { } peak || profit > peak)
            {
                position.PeakPnlPercent = profit;
            }
            var active = position.TrailingStopState == "SIMULATED_OPEN";
            if (!active && profit >= config.Exits.TrailingActivationRMultiple * position.StopDistancePct.Value)
            {
                var distance = config.Exits.TrailingAtrMultiple > 0m
                    ? config.Exits.TrailingAtrMultiple * position.AtrPct.Value
                    : config.TpSl.WorkingTrailingStopPercent(position.FlippedEntry);
                var spread = bidPrice is > 0m && askPrice is > 0m
                    ? (askPrice.Value - bidPrice.Value) / ((askPrice.Value + bidPrice.Value) / 2m) * 100m : 0m;
                position.TrailingStopPercent = config.TpSl.EffectiveTrailingStopPercent(distance, spread);
                position.TrailingStopState = "SIMULATED_OPEN";
                position.TrailingActivatedAtUtc = DateTimeOffset.UtcNow;
                position.PeakPnlPercent = profit;
                position.TrailingInitialStopPrice ??= price * (1m + (isShort ? 1m : -1m) * position.TrailingStopPercent.Value / 100m);
                active = true;
            }
            if (active && position.TrailingStopPercent is > 0m)
            {
                var extreme = position.EntryPrice * (1m + (isShort ? -1m : 1m) * position.PeakPnlPercent!.Value / 100m);
                var trail = extreme * (1m + (isShort ? 1m : -1m) * position.TrailingStopPercent.Value / 100m);
                if (isShort ? price >= trail : price <= trail)
                {
                    position.TrailingStopState = "TRIGGERED";
                    return new TpSlTrigger(position.Pair, "TRAILING_STOP", trail, priceSource, position.TpOrderState, position.SlOrderState);
                }
            }
        }

        if (IsWorkingOrderOpen(position, position.SlOrderState) && position.StopLossPrice is { } stop)
        {
            var stopHit = isShort ? price >= stop : price <= stop;
            if (stopHit)
            {
                var previousTpOrderState = position.TpOrderState;
                var previousSlOrderState = position.SlOrderState;
                position.SlOrderState = "TRIGGERED";
                position.TpOrderState = position.TpOrderState == "SIMULATED_OPEN" ? "CANCELLED" : position.TpOrderState;
                return new TpSlTrigger(position.Pair, "STOP_LOSS", stop, priceSource, previousTpOrderState, previousSlOrderState);
            }
        }

        if (IsWorkingOrderOpen(position, position.TpOrderState) && position.TakeProfitPrice is { } take)
        {
            var takeHit = isShort ? price <= take : price >= take;
            if (takeHit)
            {
                var previousTpOrderState = position.TpOrderState;
                var previousSlOrderState = position.SlOrderState;
                position.TpOrderState = "TRIGGERED";
                position.SlOrderState = position.SlOrderState == "SIMULATED_OPEN" ? "CANCELLED" : position.SlOrderState;
                return new TpSlTrigger(position.Pair, "TAKE_PROFIT", take, priceSource, previousTpOrderState, previousSlOrderState);
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
