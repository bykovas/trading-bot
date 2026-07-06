namespace TradingBot.FuturesWorker;

// Simulated TP/SL order pair per open position. Levels are frozen at entry by
// FuturesVirtualPortfolio; each cycle this orchestrator checks the configured
// trigger price stream and, when a level is crossed, returns a reduce-only close
// instruction. It never emits anything that could increase exposure.
internal sealed class TpSlOrchestrator(FuturesBotConfiguration config)
{
    public sealed record TpSlTrigger(string Pair, string Kind, decimal TriggerPrice, string TriggerSource);

    public TpSlTrigger? Evaluate(PortfolioPosition position, decimal markPrice, decimal lastPrice)
    {
        if (!config.TpSl.Enabled)
        {
            return null;
        }

        var triggerSource = config.TpSl.TriggerSource.Equals("last", StringComparison.OrdinalIgnoreCase) ? "last" : "mark";
        var price = triggerSource == "last" ? lastPrice : markPrice;
        if (price <= 0m)
        {
            return null;
        }

        var isShort = position.Side == "SHORT";

        if (position.SlOrderState == "SIMULATED_OPEN" && position.StopLossPrice is { } stop)
        {
            var stopHit = isShort ? price >= stop : price <= stop;
            if (stopHit)
            {
                position.SlOrderState = "TRIGGERED";
                position.TpOrderState = position.TpOrderState == "SIMULATED_OPEN" ? "CANCELLED" : position.TpOrderState;
                return new TpSlTrigger(position.Pair, "STOP_LOSS", stop, triggerSource);
            }
        }

        if (position.TpOrderState == "SIMULATED_OPEN" && position.TakeProfitPrice is { } take)
        {
            var takeHit = isShort ? price <= take : price >= take;
            if (takeHit)
            {
                position.TpOrderState = "TRIGGERED";
                position.SlOrderState = position.SlOrderState == "SIMULATED_OPEN" ? "CANCELLED" : position.SlOrderState;
                return new TpSlTrigger(position.Pair, "TAKE_PROFIT", take, triggerSource);
            }
        }

        return null;
    }
}
