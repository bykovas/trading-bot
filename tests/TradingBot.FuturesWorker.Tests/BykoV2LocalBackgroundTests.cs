using System.Reflection;
using TradingBot.Core.Common;
using TradingBot.Core.MarketData;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class BykoV2LocalBackgroundTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 15, TimeSpan.Zero);

    [Fact]
    public async Task Blocked_HTTP_returns_immediately_and_does_not_start_duplicate_work()
    {
        var h = new Harness();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var first = h.Runner.EvaluateLatestAsync(cancellation.Token);
            Assert.True(first.IsCompletedSuccessfully);
            Assert.Empty(await first);
            await h.Client.Started.Task.WaitAsync(Timeout);
            var background = Background(h.Runner);
            Assert.False(background.IsCompleted);

            var repeated = h.Runner.EvaluateLatestAsync(cancellation.Token);
            Assert.True(repeated.IsCompletedSuccessfully);
            Assert.Empty(await repeated);
            Assert.Same(background, Background(h.Runner));
            Assert.Equal(1, h.Client.Calls);
        }
        finally
        {
            cancellation.Cancel();
            await h.Client.Stopped.Task.WaitAsync(Timeout);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Background(h.Runner).WaitAsync(Timeout));
        }
    }

    [Fact]
    public async Task Pause_cancels_blocked_HTTP_without_delaying_protective_loop()
    {
        var h = new Harness();
        using var cancellation = new CancellationTokenSource();
        try
        {
            Assert.Empty(await h.Runner.EvaluateLatestAsync(cancellation.Token));
            await h.Client.Started.Task.WaitAsync(Timeout);
            var background = Background(h.Runner);
            h.Config.SetRuntimeLimits(h.Config.RuntimeLimits with { NewEntriesPaused = true });
            var paused = h.Runner.EvaluateLatestAsync(cancellation.Token);
            Assert.True(paused.IsCompletedSuccessfully);
            Assert.Empty(await paused);
            await h.Client.Stopped.Task.WaitAsync(Timeout);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => background.WaitAsync(Timeout));
            Assert.True(h.Client.RequestToken.IsCancellationRequested);
            Assert.Equal(1, h.Client.Calls);

            h.Config.SetRuntimeLimits(h.Config.RuntimeLimits with { NewEntriesPaused = false });
            Assert.Empty(await h.Runner.EvaluateLatestAsync(cancellation.Token));
            Assert.Equal(1, h.Client.Calls);
        }
        finally { cancellation.Cancel(); }
    }

    [Fact]
    public async Task Completed_fault_is_suppressed_and_ten_second_backoff_prevents_relaunch()
    {
        var h = new Harness();
        h.Client.Fault = new InvalidDataException("invalid historical response");
        using var cancellation = new CancellationTokenSource();
        try
        {
            Assert.Empty(await h.Runner.EvaluateLatestAsync(cancellation.Token));
            await h.Client.Started.Task.WaitAsync(Timeout);
            await Assert.ThrowsAsync<InvalidDataException>(() => Background(h.Runner).WaitAsync(Timeout));

            var observed = h.Runner.EvaluateLatestAsync(cancellation.Token);
            Assert.True(observed.IsCompletedSuccessfully);
            Assert.Empty(await observed);
            h.Clock.UtcNow = Now.AddSeconds(9);
            Assert.Empty(await h.Runner.EvaluateLatestAsync(cancellation.Token));
            Assert.Equal(1, h.Client.Calls);

            h.Clock.UtcNow = Now.AddSeconds(10);
            Assert.Empty(await h.Runner.EvaluateLatestAsync(cancellation.Token));
            await Assert.ThrowsAsync<InvalidDataException>(() => Background(h.Runner).WaitAsync(Timeout));
            Assert.Equal(2, h.Client.Calls);
            Assert.Empty(await h.Runner.EvaluateLatestAsync(cancellation.Token));
        }
        finally { cancellation.Cancel(); }
    }

    [Fact]
    public async Task Missing_owner_key_returns_no_entries_without_throwing_or_local_fallback()
    {
        var h = new Harness(missingKey: true);
        var evaluation = h.Runner.EvaluateLatestAsync(default);
        Assert.True(evaluation.IsCompletedSuccessfully);
        Assert.Empty(await evaluation);
        h.Clock.UtcNow = Now.AddMinutes(4);
        Assert.Empty(await h.Runner.EvaluateLatestAsync(default));
        h.Clock.UtcNow = Now.AddMinutes(5);
        Assert.Empty(await h.Runner.EvaluateLatestAsync(default));
        Assert.Equal(0, h.Client.Calls);
        Assert.Empty(h.Runner.PendingAlignedNotices());
    }

    // Observe completion deterministically without introducing production-only test hooks.
    private static Task<IReadOnlyList<BykoV2LocalSignal>> Background(BykoV2LocalSignalRunner runner) =>
        Assert.IsAssignableFrom<Task<IReadOnlyList<BykoV2LocalSignal>>>(typeof(BykoV2LocalSignalRunner)
            .GetField("_alignedEvaluation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runner));

    private sealed class Harness
    {
        public FuturesBotConfiguration Config { get; } = new();
        public FakeClock Clock { get; } = new() { UtcNow = Now };
        public BlockingClient Client { get; } = new();
        public BykoV2LocalSignalRunner Runner { get; }
        public Harness(bool missingKey = false)
        {
            Config.BykoV2.Enabled = true;
            Config.BykoV2.LocalEvaluationEnabled = true;
            Config.BykoV2.DebykoAlignedDataEnabled = true;
            Config.BykoV2.Universe = ["SOL/USD"];
            Config.BykoV2.DebykoRuleIds["BYKO_FLUSH_LONG"] = Guid.Parse("3214ba4c-613a-4b0a-aeb4-715ec84cbb88");
            Config.BykoV2.DebykoRuleVersions["BYKO_FLUSH_LONG"] = new string('a', 64);
            var verdicts = new RejectingVerdictStore();
            var aligned = missingKey ? null : new BykoV2AlignedRunner(Config, Client, verdicts, new RejectingAlignedStore(), Clock);
            Runner = new(Config, DispatchProxy.Create<IMarketDataStore, RejectingMarketData>(), verdicts, Clock, aligned);
        }
    }

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow { get; set; } }

    private sealed class BlockingClient : IDebykoMarketClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? Fault { get; set; }
        public CancellationToken RequestToken { get; private set; }
        public async Task<DebykoBarResult> GetResultAsync(Guid ruleId, string instrument, DateTimeOffset barEnd, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            RequestToken = cancellationToken;
            Started.TrySetResult(true);
            try
            {
                if (Fault is { } fault) throw fault;
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Blocked request unexpectedly completed");
            }
            finally { Stopped.TrySetResult(true); }
        }
        public Task<DebykoFrameRead> GetFrameAsync(string pair, DateTimeOffset barEnd, DateTimeOffset dataAsOf,
            CancellationToken cancellationToken, BykoV2Rule? rule = null, bool btcReference = false) =>
            throw new InvalidOperationException("No frame request expected");
    }

    public class RejectingMarketData : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException("Aligned evaluation must not fall back to local market data");
    }

    private sealed class RejectingVerdictStore : IBykoV2VerdictStore
    {
        public bool IsAvailable => true;
        public bool Record(string botInstanceId, string pair, DateTimeOffset barClosedAt, BykoV2Verdict verdict,
            DateTimeOffset evaluatedAt, TimeSpan cooldown) => throw new InvalidOperationException("No verdict expected");
        public void RecordDebykoFiring(string botInstanceId, string pair, string rule, DateTimeOffset firedAt,
            string? firingId, DateTimeOffset receivedAt) => throw new InvalidOperationException("No firing expected");
    }

    private sealed class RejectingAlignedStore : IBykoV2AlignedStore
    {
        public bool IsFinished(string bot, Guid ruleId, string version, string pair, DateTimeOffset barEnd) => false;
        public void Record(string bot, BykoV2AlignedEvaluation evaluation) => throw new InvalidOperationException("No audit expected");
        public IReadOnlyList<BykoV2SignalNotice> PendingNotices(string bot, DateTimeOffset now) => [];
    }
}
