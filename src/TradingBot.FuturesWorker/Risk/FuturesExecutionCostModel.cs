namespace TradingBot.FuturesWorker;

// Single live/virtual execution cost model. Live futures entries are taker FOK, so
// virtual planning, risk, TP floors, and simulated fees all use the same taker
// round-trip (entry fee + exit fee + slippage buffer + expected funding).
internal static class FuturesExecutionCostModel
{
    public const string TakerFokRoundTrip = "taker_fok_round_trip";

    public static ExecutionCostEstimate Estimate(
        FuturesBotConfiguration config,
        FuturesDesiredExposure desired,
        decimal? fundingRatePercent)
    {
        var entryFeePct = Math.Max(0m, config.Fees.TakerPct);
        var exitFeePct = Math.Max(0m, config.Fees.TakerPct);
        var slippagePct = Math.Max(0m, config.Exits.SlippageBufferPct);
        var expectedFundingPct = ExpectedFundingPct(desired, fundingRatePercent);
        var roundTrip = entryFeePct + exitFeePct + slippagePct + expectedFundingPct;
        return new ExecutionCostEstimate(
            Model: TakerFokRoundTrip,
            EntryFeePct: entryFeePct,
            ExitFeePct: exitFeePct,
            SlippageBufferPct: slippagePct,
            ExpectedFundingPct: expectedFundingPct,
            RoundTripCostPct: decimal.Round(roundTrip, 6));
    }

    public static decimal FeeEur(decimal notionalEur, decimal feePct) =>
        notionalEur <= 0m || feePct <= 0m ? 0m : decimal.Round(notionalEur * feePct / 100m, 8);

    private static decimal ExpectedFundingPct(FuturesDesiredExposure desired, decimal? fundingRatePercent)
    {
        if (fundingRatePercent is null)
        {
            return 0m;
        }

        // Adverse funding only: longs pay when funding > 0, shorts pay when funding < 0.
        return desired switch
        {
            FuturesDesiredExposure.Long => Math.Max(0m, fundingRatePercent.Value),
            FuturesDesiredExposure.Short => Math.Max(0m, -fundingRatePercent.Value),
            _ => 0m
        };
    }
}

internal sealed record ExecutionCostEstimate(
    string Model,
    decimal EntryFeePct,
    decimal ExitFeePct,
    decimal SlippageBufferPct,
    decimal ExpectedFundingPct,
    decimal RoundTripCostPct);
