namespace TradingBot.SpotWorker;

// Rank input for a prospective NEW position (a pair with no currently open
// position). Held positions never enter ranking: exit/hold logic runs in its own
// phase before any entry is considered, so exits can never be delayed by entries.
internal sealed record EntryCandidate(
    string Pair,
    decimal Score,
    decimal BullishEmaGapPercent,
    decimal? Rsi,
    decimal TargetNotionalEur,
    int OriginalIndex);

// Deterministic best-first ordering of new-entry candidates. Without this, entries
// were opened in raw CandidateUniverse iteration order, so per-cycle / max-open
// limits were consumed by whichever pairs happened to come first.
internal static class EntryRanking
{
    // Priority (fixed, documented order):
    //   1. Higher strategy score first.
    //   2. Larger bullish EMA gap percent first.
    //   3. RSI quality: healthy (45-65) > acceptable/unknown > overheated (>= 68).
    //   4. Larger target notional first (only after signal quality).
    //   5. Original candidate order as the final stable tie-breaker.
    public static IReadOnlyList<EntryCandidate> Rank(IEnumerable<EntryCandidate> candidates) =>
        candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.BullishEmaGapPercent)
            .ThenByDescending(candidate => RsiQuality(candidate.Rsi))
            .ThenByDescending(candidate => candidate.TargetNotionalEur)
            .ThenBy(candidate => candidate.OriginalIndex)
            .ToList();

    // 2 = healthy range (45-65), 1 = acceptable or unknown, 0 = overheated (>= 68).
    public static int RsiQuality(decimal? rsi)
    {
        if (rsi is null)
        {
            return 1;
        }

        if (rsi.Value >= 68m)
        {
            return 0;
        }

        return rsi.Value is >= 45m and <= 65m ? 2 : 1;
    }
}
