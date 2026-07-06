using TradingBot.SpotWorker;
using Xunit;

namespace TradingBot.SpotWorker.Tests;

public class WatchlistCacheTests
{
    [Fact]
    public async Task Within_ttl_the_advisor_is_not_called_again()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-07-03T12:00:00Z"));
        var primary = new CountingAdvisor("openai-compatible", "AAA/EUR");
        var advisor = new CachedWatchlistAdvisor(primary, new CountingAdvisor("heuristic", "BBB/EUR"), 3600, clock);
        var candidates = Candidates("AAA/EUR", "BBB/EUR");

        var first = await advisor.SelectAsync(candidates, 20, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(14));
        var second = await advisor.SelectAsync(candidates, 20, CancellationToken.None);

        Assert.Equal(1, primary.CallCount);
        Assert.Equal("AAA/EUR", Assert.Single(first.Recommendations).Pair);
        Assert.Contains("cached 14m ago", second.Provider);
        Assert.Equal("AAA/EUR", Assert.Single(second.Recommendations).Pair);
    }

    [Fact]
    public async Task After_ttl_expiry_the_advisor_is_called_and_cache_is_replaced()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-07-03T12:00:00Z"));
        var primary = new CountingAdvisor("openai-compatible", "AAA/EUR", "BBB/EUR");
        var advisor = new CachedWatchlistAdvisor(primary, new CountingAdvisor("heuristic", "CCC/EUR"), 60, clock);
        var candidates = Candidates("AAA/EUR", "BBB/EUR", "CCC/EUR");

        var first = await advisor.SelectAsync(candidates, 20, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(61));
        var second = await advisor.SelectAsync(candidates, 20, CancellationToken.None);

        Assert.Equal(2, primary.CallCount);
        Assert.Equal("AAA/EUR", Assert.Single(first.Recommendations).Pair);
        Assert.Equal("BBB/EUR", Assert.Single(second.Recommendations).Pair);
    }

    [Fact]
    public async Task Advisor_failure_within_double_ttl_serves_stale_cache_with_warning_then_falls_back()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-07-03T12:00:00Z"));
        var primary = new CountingAdvisor("openai-compatible", "AAA/EUR") { ThrowAfterCalls = 1 };
        var fallback = new CountingAdvisor("heuristic", "CCC/EUR");
        var advisor = new CachedWatchlistAdvisor(primary, fallback, 60, clock);
        var candidates = Candidates("AAA/EUR", "CCC/EUR");

        await advisor.SelectAsync(candidates, 20, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(61));
        var stale = await advisor.SelectAsync(candidates, 20, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(60));
        var heuristic = await advisor.SelectAsync(candidates, 20, CancellationToken.None);

        Assert.Equal("AAA/EUR", Assert.Single(stale.Recommendations).Pair);
        Assert.Contains(stale.Warnings, warning => warning.Contains("serving stale cached selection"));
        Assert.Equal("CCC/EUR", Assert.Single(heuristic.Recommendations).Pair);
        Assert.Equal(1, fallback.CallCount);
    }

    [Fact]
    public async Task Zero_refresh_seconds_calls_advisor_every_time()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-07-03T12:00:00Z"));
        var primary = new CountingAdvisor("openai-compatible", "AAA/EUR", "BBB/EUR");
        var advisor = new CachedWatchlistAdvisor(primary, new CountingAdvisor("heuristic", "CCC/EUR"), 0, clock);
        var candidates = Candidates("AAA/EUR", "BBB/EUR", "CCC/EUR");

        await advisor.SelectAsync(candidates, 20, CancellationToken.None);
        await advisor.SelectAsync(candidates, 20, CancellationToken.None);

        Assert.Equal(2, primary.CallCount);
    }

    [Fact]
    public async Task Duplicate_recommendations_are_deduped_by_pair_keeping_best_priority()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-07-03T12:00:00Z"));
        var advisor = new CachedWatchlistAdvisor(new DuplicatingAdvisor(), new CountingAdvisor("heuristic", "CCC/EUR"), 3600, clock);
        var candidates = Candidates("AAA/EUR", "BBB/EUR");

        var advice = await advisor.SelectAsync(candidates, 20, CancellationToken.None);

        Assert.Equal(2, advice.Recommendations.Count);
        Assert.Single(advice.Recommendations, recommendation => recommendation.Pair == "AAA/EUR");
        // The best (lowest) source priority wins and gets re-indexed to #1.
        Assert.Equal("AAA/EUR", advice.Recommendations[0].Pair);
        Assert.Equal(1, advice.Recommendations[0].Priority);
        Assert.Contains("best", advice.Recommendations[0].Reason);
    }

    private sealed class DuplicatingAdvisor : IWatchlistAdvisor
    {
        public Task<WatchlistAdvice> SelectAsync(
            IReadOnlyList<InstrumentMarketState> candidates,
            int maxRecommendations,
            CancellationToken cancellationToken) =>
            Task.FromResult(new WatchlistAdvice(
                "openai-compatible",
                new[]
                {
                    new WatchlistRecommendation("AAA/EUR", 2, "duplicate lower priority"),
                    new WatchlistRecommendation("AAA/EUR", 1, "best priority"),
                    new WatchlistRecommendation("BBB/EUR", 3, "other pair")
                },
                Array.Empty<string>()));
    }

    private static IReadOnlyList<InstrumentMarketState> Candidates(params string[] pairs) =>
        pairs.Select(pair => new InstrumentMarketState
        {
            Instrument = new InstrumentOptions { Pair = pair, KrakenPair = pair.Replace("/", string.Empty, StringComparison.Ordinal), Venue = "Kraken", Enabled = true },
            Candles = Array.Empty<Candle>(),
            Quote = new Quote(1m, 1.01m, 1m, 100m, 0m)
        }).ToList();

    private sealed class CountingAdvisor(string provider, params string[] pairs) : IWatchlistAdvisor
    {
        public int CallCount { get; private set; }
        public int ThrowAfterCalls { get; init; } = int.MaxValue;

        public Task<WatchlistAdvice> SelectAsync(
            IReadOnlyList<InstrumentMarketState> candidates,
            int maxRecommendations,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount > ThrowAfterCalls)
            {
                throw new InvalidOperationException("planned watchlist failure");
            }

            var pair = pairs[Math.Min(CallCount - 1, pairs.Length - 1)];
            return Task.FromResult(new WatchlistAdvice(
                provider,
                new[] { new WatchlistRecommendation(pair, 1, "fake pick") },
                Array.Empty<string>()));
        }
    }

    private sealed class FakeClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = utcNow;
        public void Advance(TimeSpan value) => UtcNow += value;
    }
}
