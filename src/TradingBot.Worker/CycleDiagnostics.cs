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
    bool HardFiltersPassed,
    bool QualityFiltersPassed,
    IReadOnlyList<string> MissingConfirmations,
    string? RejectionReason,
    bool Exploratory);

// Universe pair that received a light snapshot but was NOT evaluated with full data
// this cycle (typically not picked by the watchlist advisor).
internal sealed record ExcludedPairDiagnostic(
    string Pair,
    string Reason,
    decimal Last,
    decimal ChangePercent);

// Cycle-level entry funnel: snapshot universe -> active set -> hard filters ->
// quality filters -> ranking -> chosen entry (or an explicit no-trade reason).
// Persisted inside the cycle record so silent inactivity is impossible: every cycle
// carries its own explanation.
internal sealed record CycleEntryDiagnostics(
    int SnapshotPairsAvailable,
    int ActivePairsEvaluated,
    int EntryPairsEvaluated,
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
