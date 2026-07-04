namespace TradingBot.Worker;

// Per-pair entry diagnostics for one cycle: everything needed to answer "why did
// this pair not become a trade?" without re-deriving it from free-text reasons.
internal sealed record CandidateDiagnostic(
    string Pair,
    decimal Score,
    string DesiredPosition,
    decimal SpreadPercent,
    decimal Price,
    decimal Bid,
    decimal Ask,
    string PriceActionDirection,
    decimal? PriceActionTrendPercent,
    string PriceActionState,
    int PriceActionSamplesAvailable,
    int PriceActionSamplesRequired,
    DateTimeOffset? PriceActionOldestSampleUtc,
    DateTimeOffset? PriceActionNewestSampleUtc,
    bool HardFiltersPassed,
    bool QualityFiltersPassed,
    IReadOnlyList<string> MissingConfirmations,
    string? RejectionReason,
    bool Exploratory);

// Universe pair that received a light snapshot but was NOT evaluated with full data
// this cycle (typically not picked by the watchlist advisor). VolumeRank is the
// pair's position when all snapshot pairs are ordered by estimated 24h EUR volume,
// which is what the heuristic advisor ranks by; AdvisorRank is only present when
// the advisor recommended the pair but it still did not enter the active set.
internal sealed record ExcludedPairDiagnostic(
    string Pair,
    string Reason,
    decimal Last,
    decimal ChangePercent,
    int? VolumeRank = null,
    decimal? Est24hVolumeEur = null,
    decimal? SpreadPercent = null,
    int? AdvisorRank = null);

// Cycle-level entry funnel: snapshot universe -> active set -> hard filters ->
// quality filters -> ranking -> chosen entry (or an explicit no-trade reason).
// Persisted inside the cycle record so silent inactivity is impossible: every cycle
// carries its own explanation.
internal sealed record CycleEntryDiagnostics(
    int SnapshotPairsAvailable,
    int ActivePairsEvaluated,
    int EntryPairsEvaluated,
    int PriceActionReadyCount,
    int ScoreAtLeast075,
    int ScoreAtLeast080,
    int ScoreAtLeast085,
    int ScoreAtLeast090,
    int HardFilterPassCount,
    int EligibleEntryCandidates,
    string? ChosenPair,
    string? NoTradeReason,
    IReadOnlyDictionary<string, int> RejectionCounts,
    IReadOnlyList<CandidateDiagnostic> TopCandidates,
    IReadOnlyList<ExcludedPairDiagnostic> ExcludedPairs);
