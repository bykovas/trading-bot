namespace TradingBot.Core.Common;

public sealed record Candle(
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    int TradeCount);

public sealed record PairRules(
    string Pair,
    string Status,
    decimal OrderMinimum,
    decimal CostMinimum,
    int LotDecimals,
    int PairDecimals);

public sealed class InstrumentMarketState
{
    public required InstrumentOptions Instrument { get; init; }
    public required IReadOnlyList<Candle> Candles { get; init; }
    public PairRules? PairRules { get; init; }
    public Quote? Quote { get; init; }
    public string? DataWarning { get; init; }

    public bool IsUsable => Candles.Count >= 30 && string.IsNullOrWhiteSpace(DataWarning);
    public decimal LastPrice => Candles.Count == 0 ? Quote?.Last ?? 0m : Candles[^1].Close;
    public decimal BestBid => Quote?.Bid ?? LastPrice;
    public decimal BestAsk => Quote?.Ask ?? LastPrice;
    public decimal LastVolume => Candles.Count == 0 ? Quote?.VolumeToday ?? 0m : Candles[^1].Volume;

    public decimal ChangePercent
    {
        get
        {
            if (Candles.Count == 0 && Quote?.ChangePercent is { } tickerChange)
            {
                return tickerChange;
            }

            if (Candles.Count < 2)
            {
                return 0m;
            }

            var first = Candles[Math.Max(0, Candles.Count - 24)].Close;
            return first == 0m ? 0m : decimal.Round((LastPrice - first) / first * 100m, 2);
        }
    }

    public decimal VolatilityPercent
    {
        get
        {
            if (Candles.Count < 10 || LastPrice == 0m)
            {
                return 0m;
            }

            var recent = Candles.TakeLast(Math.Min(24, Candles.Count)).ToList();
            var averageRange = recent.Average(candle => (double)((candle.High - candle.Low) / LastPrice * 100m));
            return decimal.Round((decimal)averageRange, 2);
        }
    }
}

public sealed record Quote(
    decimal Bid,
    decimal Ask,
    decimal Last,
    decimal VolumeToday,
    decimal? ChangePercent = null,
    decimal? FundingRatePercent = null,
    decimal? MarkPrice = null,
    decimal? IndexPrice = null);

public sealed record IndicatorSnapshot(
    decimal? FastEma,
    decimal? SlowEma,
    decimal? Rsi);

public sealed record TechnicalSignal(
    decimal Score,
    string Direction,
    bool AllowsLong,
    bool HasBullishStructure,
    bool EmaFullyConfirmed,
    decimal? BullishEmaGapPercent,
    decimal? EmaGapVelocityPercent,
    IReadOnlyList<SignalContribution> Contributions,
    // Score before the missing-volume cap was applied. Equals Score when no cap fired.
    decimal UncappedScore = 0m,
    bool VolumeConfirmed = false,
    bool AllowsShort = false,
    bool HasBearishStructure = false,
    decimal? BearishEmaGapPercent = null);

public sealed record SignalContribution(
    string Name,
    decimal Value,
    string Reason);

public sealed record DecisionProposal(
    string Pair,
    string DesiredPosition,
    decimal Score,
    decimal TargetNotionalEur,
    IReadOnlyList<SignalContribution> Contributions,
    // Compact machine-readable reason (REJECT_*) why this pair did NOT become a firm
    // entry this cycle. Null for firm entries and for held positions.
    string? EntryRejectionReason = null,
    // Dry-run exploratory candidate: passed every quality gate at the exploratory
    // threshold but not the live one. It may be upgraded to a real entry by the
    // ranking layer when it lands in the top ExploratoryMaxRank candidates.
    bool ExploratoryCandidate = false,
    decimal SpreadPercent = 0m,
    bool HasBullishStructure = false,
    bool EmaFullyConfirmed = false,
    decimal? BullishEmaGapPercent = null,
    decimal? EmaGapVelocityPercent = null,
    bool EarlyEntryEligible = false,
    string? EarlyEntryReason = null,
    decimal EarlyEntryDiagnosticScore = 0m,
    decimal EarlyEntrySuggestedNotionalEur = 0m,
    // First-class early entry on a forming EMA cross (gap above the early floor and
    // still widening). Ranked entries are capped at Strategy.EarlyEntryMaxRank.
    bool EarlyEntryCandidate = false);

public sealed record PositionSizeSelection(
    decimal TargetNotionalEur,
    string Reason);

public sealed record RiskEvaluation(
    bool Approved,
    IReadOnlyList<string> Reasons);

public sealed record WatchlistAdvice(
    string Provider,
    IReadOnlyList<WatchlistRecommendation> Recommendations,
    IReadOnlyList<string> Warnings);

public sealed record WatchlistRecommendation(
    string Pair,
    int Priority,
    string Reason);

// One light market snapshot row per universe pair per cycle. Captures the bid/ask/
// spread state that Kraken candles cannot reconstruct after the fact.
public sealed record MarketSnapshotRecord(
    string CycleId,
    DateTimeOffset Utc,
    string Pair,
    decimal Bid,
    decimal Ask,
    decimal Last,
    decimal Volume24h,
    decimal ChangePercent,
    string BotInstanceId = "default");
