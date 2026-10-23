using System.Net;
using TradingBot.Core.Common;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class BykoV2AlignedRunnerTests
{
    private static readonly DateTimeOffset End = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Version = new('a', 64);
    private static readonly Guid RuleId = Guid.Parse("3214ba4c-613a-4b0a-aeb4-715ec84cbb88");

    [Theory]
    [InlineData((int)BykoV2Rule.FlushLong)]
    [InlineData((int)BykoV2Rule.BreakoutLong)]
    public async Task Waits_until_exact_E_plus_15(int ruleValue)
    {
        var h = new Harness((BykoV2Rule)ruleValue);
        h.Clock.UtcNow = End.AddSeconds(14.999);
        Assert.Empty(await h.Runner.EvaluateAsync(default));
        Assert.Empty(h.Client.Results);
        h.Clock.UtcNow = End.AddSeconds(15);
        await h.Runner.EvaluateAsync(default);
        Assert.Equal((RuleId, "SOL-PERP", End), Assert.Single(h.Client.Results));
    }

    [Theory]
    [InlineData("not_live")]
    [InlineData("not_measured")]
    [InlineData("http_missing")]
    public async Task Unavailable_result_is_not_false_and_never_fetches_fallback(string reason)
    {
        var h = new Harness();
        h.Client.Result = (id, instrument, bar) => reason == "http_missing"
            ? throw new HttpRequestException("missing", null, HttpStatusCode.NotFound)
            : Result(id, instrument, bar) with { DataAsOf = null, Version = null, Verdict = null, Reason = reason };
        Assert.Empty(await h.Runner.EvaluateAsync(default));
        Assert.Empty(h.Client.Frames);
        Assert.Empty(h.Aligned.Records);
        Assert.Empty(h.Verdicts.Records);
    }

    [Fact]
    public async Task Both_frames_use_official_T_and_final_is_persisted_without_repeat()
    {
        var h = new Harness();
        h.Clock.UtcNow = End.AddSeconds(25);
        h.Client.Result = (id, instrument, bar) => Result(id, instrument, bar) with { DataAsOf = End.AddSeconds(17), Verdict = false };
        var signal = Assert.Single(await h.Runner.EvaluateAsync(default));
        Assert.True(signal.Verdict.Fired);
        Assert.Equal(99.71m, signal.Price);
        Assert.Collection(h.Client.Frames,
            p => Assert.Equal(("SOL/USD", End, End.AddSeconds(17), (BykoV2Rule?)BykoV2Rule.FlushLong, false), p),
            p => Assert.Equal(("XBT/USD", End, End.AddSeconds(17), (BykoV2Rule?)BykoV2Rule.FlushLong, true), p));
        var recorded = Assert.Single(h.Aligned.Records);
        Assert.True(recorded.LocalVerdict);
        Assert.False(recorded.DebykoVerdict);
        Assert.Equal(Version, recorded.Version);
        Assert.Contains("published", recorded.RawInputs);
        h.Clock.UtcNow = End.AddSeconds(36);
        Assert.Empty(await h.Runner.EvaluateAsync(default));
        Assert.Single(h.Client.Results);
        // Restart also consults durable completion, including the exact pinned version.
        Assert.Empty(await h.CreateRunner().EvaluateAsync(default));
        Assert.Single(h.Client.Results);
        Assert.All(h.Aligned.Checks, check => Assert.Equal(Version, check.Version));
    }

    [Theory]
    [InlineData("rule")]
    [InlineData("instrument")]
    [InlineData("bar")]
    public async Task Rejects_result_identity_mismatch_before_reading_frames(string field)
    {
        var h = new Harness();
        h.Client.Result = (id, instrument, bar) => field switch
        {
            "rule" => Result(Guid.Empty, instrument, bar),
            "instrument" => Result(id, "sol-perp", bar),
            _ => Result(id, instrument, bar.AddSeconds(1))
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.EvaluateAsync(default));
        Assert.Empty(h.Client.Frames);
        Assert.Empty(h.Aligned.Records);
    }

    [Fact]
    public async Task Mismatched_version_is_ignored_without_false_or_fallback()
    {
        var h = new Harness();
        h.Client.Result = (id, instrument, bar) => Result(id, instrument, bar) with { Version = new string('b', 64) };
        Assert.Empty(await h.Runner.EvaluateAsync(default));
        Assert.Empty(h.Client.Frames);
        Assert.Empty(h.Aligned.Records);
        Assert.Empty(h.Verdicts.Records);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("abcd")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public async Task Missing_or_invalid_pin_never_polls(string? pin)
    {
        var h = new Harness();
        if (pin is null) h.Config.BykoV2.DebykoRuleVersions.Clear();
        else h.Config.BykoV2.DebykoRuleVersions["BYKO_FLUSH_LONG"] = pin;
        Assert.Empty(await h.Runner.EvaluateAsync(default));
        Assert.Empty(h.Client.Results);
    }

    [Theory]
    [InlineData(14, 0)]
    [InlineData(60, 0)]
    [InlineData(26, 0)]
    [InlineData(15, -1)]
    public async Task Rejects_invalid_slice_or_attempt(int seconds, int attempt)
    {
        var h = new Harness();
        h.Clock.UtcNow = End.AddSeconds(25);
        h.Client.Result = (id, instrument, bar) => Result(id, instrument, bar) with { DataAsOf = End.AddSeconds(seconds), Attempt = attempt };
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.EvaluateAsync(default));
        Assert.Empty(h.Client.Frames);
    }

    [Fact]
    public async Task Provisional_unknown_retries_new_slice_without_freezing_local_verdict()
    {
        var h = new Harness();
        h.Client.Result = (id, instrument, bar) => Result(id, instrument, bar) with
        { Final = false, Verdict = null, Attempt = h.Client.Results.Count - 1, DataAsOf = h.Clock.UtcNow };
        h.Client.Frame = (_, _, _) => new(null, "input unavailable", []);
        Assert.Empty(await h.Runner.EvaluateAsync(default));
        Assert.Null(Assert.Single(h.Aligned.Records).LocalVerdict);
        Assert.Empty(h.Verdicts.Records);
        h.Clock.UtcNow = End.AddSeconds(24);
        await h.Runner.EvaluateAsync(default);
        Assert.Single(h.Client.Results);
        h.Clock.UtcNow = End.AddSeconds(25);
        h.Client.Frame = (pair, _, _) => new(Market(pair), null, []);
        Assert.Empty(await h.Runner.EvaluateAsync(default));
        Assert.Equal(2, h.Client.Results.Count);
        Assert.Equal(4, h.Client.Frames.Count);
        Assert.True(h.Aligned.Records[1].LocalVerdict);
        Assert.Empty(h.Verdicts.Records);
        h.Clock.UtcNow = End.AddSeconds(35);
        h.Client.Result = (id, instrument, bar) => Result(id, instrument, bar) with
        { Attempt = 2, DataAsOf = h.Clock.UtcNow };
        Assert.Single(await h.Runner.EvaluateAsync(default));
        Assert.Equal(3, h.Client.Results.Count);
        Assert.Equal(6, h.Client.Frames.Count);
        Assert.True(h.Aligned.Records[2].Final);
        Assert.Single(h.Verdicts.Records);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Definitive_provisional_verdict_is_audit_only_until_final_new_slice(bool localTrue)
    {
        var h = new Harness();
        h.Client.Result = (id, instrument, bar) => Result(id, instrument, bar) with
        { Final = false, Attempt = 0, DataAsOf = h.Clock.UtcNow };
        h.Client.Frame = (pair, _, reference) => new(
            !localTrue && !reference ? Market(pair) with { Bid = 90m, Ask = 110m } : Market(pair), null, []);
        Assert.Empty(await h.Runner.EvaluateAsync(default));
        var provisional = Assert.Single(h.Aligned.Records);
        Assert.Equal(localTrue, provisional.LocalVerdict);
        Assert.False(provisional.Final);
        Assert.Empty(h.Verdicts.Records);

        h.Clock.UtcNow = End.AddSeconds(25);
        h.Client.Result = (id, instrument, bar) => Result(id, instrument, bar) with
        { Final = true, Attempt = 1, DataAsOf = h.Clock.UtcNow };
        h.Client.Frame = (pair, _, _) => new(Market(pair), null, []);
        Assert.Single(await h.Runner.EvaluateAsync(default));
        Assert.Equal(2, h.Aligned.Records.Count);
        Assert.True(h.Aligned.Records[1].Final);
        Assert.True(h.Aligned.Records[1].LocalVerdict);
        Assert.NotEqual(provisional.DataAsOf, h.Aligned.Records[1].DataAsOf);
        Assert.True(Assert.Single(h.Verdicts.Records).Fired);
        Assert.Equal(4, h.Client.Frames.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Authorization_failure_backs_off_globally_for_five_minutes(HttpStatusCode status)
    {
        var h = new Harness();
        h.Config.BykoV2.Universe.Add("ETH/USD");
        h.Client.Result = (_, _, _) => throw new HttpRequestException("authorization", null, status);
        await h.Runner.EvaluateAsync(default);
        Assert.Single(h.Client.Results);
        h.Clock.UtcNow = End.AddMinutes(5).AddSeconds(14);
        await h.Runner.EvaluateAsync(default);
        Assert.Single(h.Client.Results);
        h.Clock.UtcNow = End.AddMinutes(5).AddSeconds(15);
        await h.Runner.EvaluateAsync(default);
        Assert.Equal(2, h.Client.Results.Count);
        Assert.Empty(h.Client.Frames);
        Assert.Empty(h.Verdicts.Records);
    }

    [Fact]
    public async Task Response_finishing_in_next_bar_is_recorded_but_cannot_emit_stale_signal()
    {
        var h = new Harness();
        h.Config.BykoV2.SignalMaxAgeSeconds = 10;
        h.Client.Frame = (pair, _, reference) =>
        {
            if (reference) h.Clock.UtcNow = End.AddMinutes(1);
            return new(Market(pair), null, []);
        };
        Assert.Empty(await h.Runner.EvaluateAsync(default));
        Assert.True(Assert.Single(h.Aligned.Records).LocalVerdict);
        Assert.Single(h.Verdicts.Records);
    }

    [Theory]
    [InlineData(65, true)]
    [InlineData(149, true)]
    [InlineData(150, false)]
    public async Task Pending_flush_previous_bar_final_is_recorded_and_entry_expires_from_slice(int receivedSeconds, bool expectedEntry)
    {
        var h = new Harness();
        h.Config.BykoV2.SignalMaxAgeSeconds = 90;
        h.Client.Result = (id, instrument, bar) => Result(id, instrument, bar) with
        { Final = false, Verdict = null, DataAsOf = End.AddSeconds(15), Attempt = 0 };
        h.Client.Frame = (_, _, _) => new(null, "provisional input unavailable", []);
        Assert.Empty(await h.Runner.EvaluateAsync(default));
        var provisional = Assert.Single(h.Aligned.Records);
        Assert.Null(provisional.LocalVerdict);
        Assert.Null(provisional.DebykoVerdict);
        Assert.False(provisional.Final);
        Assert.Empty(h.Verdicts.Records);

        h.Clock.UtcNow = End.AddSeconds(receivedSeconds);
        h.Client.Result = (id, instrument, bar) => bar == End
            ? Result(id, instrument, bar) with { Final = true, DataAsOf = End.AddSeconds(59), Attempt = 1 }
            : Result(id, instrument, bar) with { Final = false, DataAsOf = null, Version = null, Verdict = null, Reason = "not_measured" };
        h.Client.Frame = (pair, _, _) => new(Market(pair), null, []);
        var signals = await h.Runner.EvaluateAsync(default);
        Assert.Equal(2, h.Client.Results.Count(r => r.Item3 == End));
        Assert.Equal(2, h.Aligned.Records.Count);
        var final = h.Aligned.Records[1];
        Assert.Equal(End, final.BarEnd);
        Assert.Equal(End.AddSeconds(59), final.DataAsOf);
        Assert.Equal(1, final.Attempt);
        Assert.True(final.Final);
        Assert.True(final.LocalVerdict);
        Assert.True(final.DebykoVerdict);
        Assert.True(Assert.Single(h.Verdicts.Records).Fired);
        Assert.All(h.Client.Frames.Skip(2), request =>
        {
            Assert.Equal(End, request.Item2);
            Assert.Equal(End.AddSeconds(59), request.Item3);
        });
        Assert.Equal(4, h.Client.Frames.Count);
        if (expectedEntry)
        {
            var signal = Assert.Single(signals);
            Assert.Equal(End, signal.BarClosedAt);
            Assert.Equal(End.AddSeconds(59), signal.DataAsOf);
            Assert.Equal(99.71m, signal.Price);
        }
        else Assert.Empty(signals);

        h.Clock.UtcNow = h.Clock.UtcNow.AddSeconds(1);
        Assert.Empty(await h.Runner.EvaluateAsync(default));
        Assert.Equal(2, h.Client.Results.Count(r => r.Item3 == End));
        Assert.Single(h.Verdicts.Records);
    }

    [Fact]
    public async Task Bar_seen_before_E_plus_15_is_not_lost_when_next_pass_crosses_a_minute()
    {
        var h = new Harness();
        h.Clock.UtcNow = End.AddSeconds(5);
        Assert.Empty(await h.Runner.EvaluateAsync(default));
        h.Clock.UtcNow = End.AddMinutes(1).AddSeconds(20);
        await h.Runner.EvaluateAsync(default);
        Assert.Equal(new[] { End, End.AddMinutes(1) }, h.Client.Results.Select(r => r.Item3));
        Assert.Equal(2, h.Aligned.Records.Count);
    }

    [Fact]
    public async Task Recovers_never_started_intervening_bars_once_in_chronological_order()
    {
        var h = new Harness();
        await h.Runner.EvaluateAsync(default);
        h.Clock.UtcNow = End.AddMinutes(2).AddSeconds(20);
        await h.Runner.EvaluateAsync(default);
        Assert.Equal(new[] { End, End.AddMinutes(1), End.AddMinutes(2) }, h.Client.Results.Select(r => r.Item3));
        h.Clock.UtcNow = h.Clock.UtcNow.AddSeconds(1);
        await h.Runner.EvaluateAsync(default);
        Assert.Equal(3, h.Client.Results.Count);
        Assert.Equal(3, h.Verdicts.Records.Count);
    }

    [Fact]
    public async Task Long_pause_bounds_catch_up_and_audits_stale_bars_without_emitting_entries()
    {
        var h = new Harness();
        h.Config.BykoV2.SignalMaxAgeSeconds = 10;
        await h.Runner.EvaluateAsync(default);
        h.Client.Results.Clear();
        h.Clock.UtcNow = End.AddMinutes(10).AddSeconds(30);
        Assert.Empty(await h.Runner.EvaluateAsync(default));
        Assert.Equal(new[] { End.AddMinutes(8), End.AddMinutes(9), End.AddMinutes(10) },
            h.Client.Results.Select(r => r.Item3));
        Assert.Equal(4, h.Aligned.Records.Count);
    }

    [Fact]
    public async Task Slow_earlier_rule_does_not_freeze_later_rules_clock()
    {
        var h = new Harness();
        var flushId = Guid.NewGuid();
        h.Config.BykoV2.DebykoRuleIds["BYKO_FLUSH_SHORT"] = flushId;
        h.Config.BykoV2.DebykoRuleVersions["BYKO_FLUSH_SHORT"] = Version;
        h.Client.Frame = (pair, _, _) =>
        {
            h.Clock.UtcNow = End.AddMinutes(1).AddSeconds(20);
            return new(Market(pair), null, []);
        };
        await h.Runner.EvaluateAsync(default);
        Assert.Contains(h.Client.Results, r => r.Item1 == flushId && r.Item3 == End.AddMinutes(1));
        Assert.Contains(h.Client.Results, r => r.Item1 == flushId && r.Item3 == End);
    }

    [Fact]
    public async Task New_version_does_not_inherit_old_versions_completed_poll_marker()
    {
        var h = new Harness();
        await h.Runner.EvaluateAsync(default);
        var newVersion = new string('b', 64);
        h.Config.BykoV2.DebykoRuleVersions["BYKO_FLUSH_LONG"] = newVersion;
        h.Client.Result = (id, instrument, bar) => Result(id, instrument, bar) with { Version = newVersion };
        await h.Runner.EvaluateAsync(default);
        Assert.Equal(2, h.Client.Results.Count);
        Assert.Equal(newVersion, h.Aligned.Records[1].Version);
    }

    [Fact]
    public async Task Cold_start_does_not_replay_earlier_bars()
    {
        var h = new Harness();
        h.Clock.UtcNow = End.AddMinutes(10).AddSeconds(20);
        await h.Runner.EvaluateAsync(default);
        Assert.Equal(End.AddMinutes(10), Assert.Single(h.Client.Results).Item3);
    }

    private static DebykoBarResult Result(Guid id, string instrument, DateTimeOffset bar) =>
        new(id, Version, instrument, bar, bar.AddSeconds(15), 0, true, null, true, "receipt",
            [new("/v2/agent/result", 200, null, "{}", new string('c', 64), "receipt", null, new Dictionary<string, string[]>())]);

    private static BykoV2MarketFrame Market(string pair)
    {
        var bars = Enumerable.Range(0, 57).Select(i => new Candle(End.AddMinutes(i - 57), 102m, 102.2m, 101.8m, 102m, 100m, 0)).ToArray();
        bars[^4] = new(End.AddMinutes(-4), 101m, 101.1m, 100m, 100.5m, 100m, 0);
        bars[^3] = new(End.AddMinutes(-3), 100.5m, 100.6m, 99m, 99.5m, 100m, 0);
        bars[^2] = new(End.AddMinutes(-2), 99.5m, 99.6m, 98m, 98.5m, 100m, 0);
        bars[^1] = new(End.AddMinutes(-1), 98.6m, 99.8m, 98.4m, 99.7m, 100m, 0);
        var five = Enumerable.Range(0, 13).Select(i => new Candle(End.AddMinutes((i - 13) * 5), 100m, 101m, 99m, 100m, 1m, 0)).ToArray();
        return new(pair, bars, five, [], [], 99.69m, 99.71m, 100_000_000m, 0m, new(200_000m, 0m, true));
    }

    private sealed class Harness
    {
        public FuturesBotConfiguration Config { get; } = new();
        public FakeClock Clock { get; } = new() { UtcNow = End.AddSeconds(15) };
        public FakeClient Client { get; } = new();
        public FakeVerdicts Verdicts { get; } = new();
        public FakeAligned Aligned { get; } = new();
        public BykoV2AlignedRunner Runner { get; }
        public Harness(BykoV2Rule rule = BykoV2Rule.FlushLong)
        {
            Config.BykoV2.Universe = ["SOL/USD"];
            var name = PostgresBykoV2VerdictStore.RuleName(rule);
            Config.BykoV2.DebykoRuleIds[name] = RuleId;
            Config.BykoV2.DebykoRuleVersions[name] = Version;
            Runner = CreateRunner();
        }
        public BykoV2AlignedRunner CreateRunner() => new(Config, Client, Verdicts, Aligned, Clock);
    }

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow { get; set; } }
    private sealed class FakeClient : IDebykoMarketClient
    {
        public List<(Guid, string, DateTimeOffset)> Results { get; } = [];
        public List<(string, DateTimeOffset, DateTimeOffset, BykoV2Rule?, bool)> Frames { get; } = [];
        public Func<Guid, string, DateTimeOffset, DebykoBarResult> Result { get; set; } = BykoV2AlignedRunnerTests.Result;
        public Func<string, DateTimeOffset, bool, DebykoFrameRead> Frame { get; set; } = (pair, _, _) => new(Market(pair), null, []);
        public Task<DebykoBarResult> GetResultAsync(Guid id, string instrument, DateTimeOffset bar, CancellationToken cancellationToken)
        {
            Results.Add((id, instrument, bar));
            return Task.FromResult(Result(id, instrument, bar));
        }
        public Task<DebykoFrameRead> GetFrameAsync(string pair, DateTimeOffset bar, DateTimeOffset slice, CancellationToken cancellationToken, BykoV2Rule? rule = null, bool btcReference = false)
        {
            Frames.Add((pair, bar, slice, rule, btcReference));
            return Task.FromResult(Frame(pair, slice, btcReference));
        }
    }
    private sealed class FakeVerdicts : IBykoV2VerdictStore
    {
        public bool IsAvailable => true;
        public List<BykoV2Verdict> Records { get; } = [];
        public bool Record(string bot, string pair, DateTimeOffset bar, BykoV2Verdict verdict, DateTimeOffset now, TimeSpan cooldown)
        { Records.Add(verdict); return verdict.Fired == true; }
        public void RecordDebykoFiring(string bot, string pair, string rule, DateTimeOffset firedAt, string? firingId, DateTimeOffset receivedAt) => throw new InvalidOperationException("Unexpected fallback");
    }
    private sealed class FakeAligned : IBykoV2AlignedStore
    {
        public List<BykoV2AlignedEvaluation> Records { get; } = [];
        public List<(string Bot, Guid Rule, string Version, string Pair, DateTimeOffset Bar)> Checks { get; } = [];
        public bool IsFinished(string bot, Guid id, string version, string pair, DateTimeOffset bar)
        {
            Checks.Add((bot, id, version, pair, bar));
            return Records.Any(e => e.Final && e.RuleId == id && e.Version == version && e.Pair == pair && e.BarEnd == bar);
        }
        public void Record(string bot, BykoV2AlignedEvaluation evaluation) => Records.Add(evaluation);
        public IReadOnlyList<BykoV2SignalNotice> PendingNotices(string bot, DateTimeOffset now) => [];
    }
}
