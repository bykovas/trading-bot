namespace TradingBot.Worker;

internal sealed record Candle(
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    int TradeCount);

internal sealed record PairRules(
    string Pair,
    string Status,
    decimal OrderMinimum,
    decimal CostMinimum,
    int LotDecimals,
    int PairDecimals);

internal sealed class InstrumentMarketState
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

internal sealed record Quote(
    decimal Bid,
    decimal Ask,
    decimal Last,
    decimal VolumeToday,
    decimal? ChangePercent = null);

internal sealed record IndicatorSnapshot(
    decimal? FastEma,
    decimal? SlowEma,
    decimal? Rsi);

internal sealed record TechnicalSignal(
    decimal Score,
    string Direction,
    bool AllowsLong,
    decimal? BullishEmaGapPercent,
    IReadOnlyList<SignalContribution> Contributions);

internal sealed record SignalContribution(
    string Name,
    decimal Value,
    string Reason);

internal sealed record DecisionProposal(
    string Pair,
    string DesiredPosition,
    decimal Score,
    decimal TargetNotionalEur,
    IReadOnlyList<SignalContribution> Contributions);

internal sealed record PositionSizeSelection(
    decimal TargetNotionalEur,
    string Reason);

internal sealed record RiskEvaluation(
    bool Approved,
    IReadOnlyList<string> Reasons);

internal sealed record WatchlistAdvice(
    string Provider,
    IReadOnlyList<WatchlistRecommendation> Recommendations,
    IReadOnlyList<string> Warnings);

internal sealed record WatchlistRecommendation(
    string Pair,
    int Priority,
    string Reason);

// One light market snapshot row per universe pair per cycle. Captures the bid/ask/
// spread state that Kraken candles cannot reconstruct after the fact.
internal sealed record MarketSnapshotRecord(
    string CycleId,
    DateTimeOffset Utc,
    string Pair,
    decimal Bid,
    decimal Ask,
    decimal Last,
    decimal Volume24h,
    decimal ChangePercent);
