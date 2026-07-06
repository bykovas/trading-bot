
using Xunit;

namespace TradingBot.Core.Tests;

public class EntryRankingTests
{
    private static EntryCandidate Candidate(
        string pair,
        decimal score,
        decimal emaGap = 0.2m,
        decimal? rsi = 55m,
        decimal targetEur = 10m,
        int index = 0) => new(pair, score, emaGap, rsi, targetEur, index);

    [Fact]
    public void With_cycle_limit_2_the_two_highest_ranked_win_not_the_first_two_in_input_order()
    {
        // Input order deliberately puts the weakest candidates first.
        var candidates = new[]
        {
            Candidate("WEAK1/EUR", 0.70m, emaGap: 0.16m, index: 0),
            Candidate("WEAK2/EUR", 0.72m, emaGap: 0.17m, index: 1),
            Candidate("BEST/EUR", 0.90m, emaGap: 0.35m, index: 2),
            Candidate("SECOND/EUR", 0.85m, emaGap: 0.30m, index: 3)
        };

        var opened = EntryRanking.Rank(candidates).Take(2).Select(candidate => candidate.Pair).ToArray();

        Assert.Equal(new[] { "BEST/EUR", "SECOND/EUR" }, opened);
    }

    [Fact]
    public void Overheated_rsi_ranks_below_healthy_rsi_at_equal_score_and_gap()
    {
        var overheated = Candidate("HOT/EUR", 0.85m, emaGap: 0.20m, rsi: 72m, index: 0);
        var healthy = Candidate("HEALTHY/EUR", 0.85m, emaGap: 0.20m, rsi: 58m, index: 1);

        var ranked = EntryRanking.Rank(new[] { overheated, healthy });

        Assert.Equal("HEALTHY/EUR", ranked[0].Pair);
        Assert.Equal("HOT/EUR", ranked[1].Pair);
    }

    [Fact]
    public void Higher_score_beats_larger_target_notional()
    {
        var bigButWeak = Candidate("BIG/EUR", 0.75m, targetEur: 15m, index: 0);
        var smallButStrong = Candidate("STRONG/EUR", 0.85m, targetEur: 5m, index: 1);

        var ranked = EntryRanking.Rank(new[] { bigButWeak, smallButStrong });

        Assert.Equal("STRONG/EUR", ranked[0].Pair);
    }

    [Fact]
    public void Equal_ranking_values_keep_original_input_order_as_stable_tie_breaker()
    {
        var candidates = new[]
        {
            Candidate("A/EUR", 0.85m, emaGap: 0.20m, rsi: 55m, targetEur: 10m, index: 0),
            Candidate("B/EUR", 0.85m, emaGap: 0.20m, rsi: 55m, targetEur: 10m, index: 1),
            Candidate("C/EUR", 0.85m, emaGap: 0.20m, rsi: 55m, targetEur: 10m, index: 2)
        };

        var ranked = EntryRanking.Rank(candidates);

        Assert.Equal(new[] { "A/EUR", "B/EUR", "C/EUR" }, ranked.Select(candidate => candidate.Pair).ToArray());
    }

    [Theory]
    [InlineData(55.0, 2)]   // healthy
    [InlineData(45.0, 2)]   // healthy lower bound
    [InlineData(65.0, 2)]   // healthy upper bound
    [InlineData(40.0, 1)]   // acceptable
    [InlineData(66.0, 1)]   // acceptable, below overheated threshold
    [InlineData(68.0, 0)]   // overheated threshold
    [InlineData(72.0, 0)]   // overheated
    [InlineData(null, 1)]   // unknown
    public void Rsi_quality_buckets(double? rsi, int expected)
    {
        Assert.Equal(expected, EntryRanking.RsiQuality(rsi is null ? null : (decimal)rsi.Value));
    }
}
