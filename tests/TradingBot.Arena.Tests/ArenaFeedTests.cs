using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingBot.Arena;
using Xunit;

namespace TradingBot.Arena.Tests;

public sealed class ArenaFeedTests
{
    private static JsonObject Row(string fee = "1") => new() { ["trade_id"] = "kraken-PF_DOTUSD-native-fill", ["fees"] = fee };
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Source : IArenaFeedSource
    {
        public IReadOnlyList<JsonObject> Rows = [Row()];
        public int Snapshots;
        public Task<IReadOnlyList<JsonObject>> ClosedAsync(CancellationToken ct) => Task.FromResult(Rows);
        public Task<JsonObject> SnapshotAsync(CancellationToken ct)
        { Snapshots++; return Task.FromResult(new JsonObject { ["account"] = "live", ["positions"] = new JsonArray() }); }
    }
    private sealed class Store : IArenaFeedStore
    {
        private string _json = "{}";
        public bool Fail;
        public Task<ArenaFeedState> LoadAsync(CancellationToken ct) => Task.FromResult(JsonSerializer.Deserialize<ArenaFeedState>(_json)!);
        public Task SaveAsync(ArenaFeedState state, CancellationToken ct)
        { if (Fail) throw new IOException("disk full"); _json = JsonSerializer.Serialize(state); return Task.CompletedTask; }
    }
    private sealed class Transport : IArenaFeedTransport
    {
        public int Sends, Positions;
        public Func<Task<ArenaReceipt>>? Send;
        public Task<ArenaReceipt> TradesAsync(IReadOnlyList<JsonObject> rows, CancellationToken ct)
        { Sends++; return Send?.Invoke() ?? Task.FromResult(new ArenaReceipt("/v1/trades", 202, 1, 0, 1)); }
        public Task<ArenaReceipt> PositionsAsync(JsonObject rows, CancellationToken ct)
        { Positions++; return Task.FromResult(new ArenaReceipt("/v1/positions", 202, 0, 0, 0)); }
    }
    [Fact]
    public async Task RestartDoesNotResendDeliveredTrade_AndIdsAndFirstReceiptSurvive()
    {
        var source = new Source(); var store = new Store(); var transport = new Transport(); var clock = new Clock();
        var feed = new ArenaFeed(source, transport, store, _ => { }, clock);
        await feed.ReconcileAsync(default); await feed.DispatchAsync(default);
        var restarted = new ArenaFeed(source, transport, store, _ => { }, clock);
        await restarted.ReconcileAsync(default); await restarted.DispatchAsync(default);
        Assert.Equal(1, transport.Sends);
        var trade = Assert.Single((await store.LoadAsync(default)).Trades).Value;
        Assert.Equal("kraken-PF_DOTUSD-native-fill", trade.TradeId);
        Assert.Equal(clock.Now, trade.FirstAttempt);
    }
    [Fact]
    public async Task LateCostsUpdateBefore24Hours_DeadlineNeverMoves_ThenTheyFreeze()
    {
        var source = new Source(); var store = new Store(); var transport = new Transport(); var clock = new Clock();
        var feed = new ArenaFeed(source, transport, store, _ => { }, clock);
        await feed.ReconcileAsync(default); await feed.DispatchAsync(default);
        var first = clock.Now; clock.Now = first.AddHours(23); source.Rows = [Row("2")];
        await feed.ReconcileAsync(default); await feed.DispatchAsync(default);
        clock.Now = first.AddHours(24); source.Rows = [Row("3")];
        await feed.ReconcileAsync(default); await feed.DispatchAsync(default);
        Assert.Equal(2, transport.Sends);
        Assert.Equal(first, (await store.LoadAsync(default)).Trades.Values.Single().FirstAttempt);
    }
    [Fact]
    public async Task Imported200HasUnknownReceiptTime_NoGuessedCorrectionWindow()
    {
        var source = new Source(); var store = new Store(); var transport = new Transport { Send = () => Task.FromResult(new ArenaReceipt("/v1/trades", 200, 0, 0, 1)) };
        var feed = new ArenaFeed(source, transport, store, _ => { }, new Clock());
        await feed.ReconcileAsync(default); await feed.DispatchAsync(default);
        source.Rows = [Row("99")]; await feed.ReconcileAsync(default); await feed.DispatchAsync(default);
        Assert.Equal(1, transport.Sends);
    }
    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(409)]
    public async Task ClientErrorsAreNeverRetried(int status)
    {
        var source = new Source(); var store = new Store(); var clock = new Clock();
        var transport = new Transport { Send = () => throw new ArenaHttpException((HttpStatusCode)status, "rejected") };
        var feed = new ArenaFeed(source, transport, store, _ => { }, clock);
        await feed.ReconcileAsync(default); await feed.DispatchAsync(default); clock.Now += TimeSpan.FromDays(2);
        await feed.ReconcileAsync(default); await feed.DispatchAsync(default);
        Assert.Equal(1, transport.Sends);
        Assert.True((await store.LoadAsync(default)).Trades.Values.Single().Rejected);
    }
    [Fact]
    public async Task TemporaryFailureWaitsAndSurvivesRestart_WithoutLosingTrade()
    {
        var source = new Source(); var store = new Store(); var clock = new Clock();
        var transport = new Transport { Send = () => throw new HttpRequestException("offline") };
        var feed = new ArenaFeed(source, transport, store, _ => { }, clock);
        await feed.ReconcileAsync(default); await feed.DispatchAsync(default); await feed.DispatchAsync(default);
        Assert.Equal(1, transport.Sends);
        clock.Now += TimeSpan.FromSeconds(3); transport.Send = null;
        await new ArenaFeed(source, transport, store, _ => { }, clock).DispatchAsync(default);
        Assert.Equal(2, transport.Sends);
        Assert.True((await store.LoadAsync(default)).Trades.Values.Single().Received);
    }
    [Fact]
    public async Task Exhausted429StillHonoursLastRetryAfter()
    {
        var source = new Source(); var store = new Store(); var clock = new Clock();
        var transport = new Transport { Send = () => throw new ArenaHttpException(HttpStatusCode.TooManyRequests, "throttled", TimeSpan.FromMinutes(2)) };
        var feed = new ArenaFeed(source, transport, store, _ => { }, clock);
        await feed.ReconcileAsync(default); await feed.DispatchAsync(default); clock.Now += TimeSpan.FromSeconds(119);
        await feed.DispatchAsync(default); Assert.Equal(1, transport.Sends);
        clock.Now += TimeSpan.FromSeconds(1); transport.Send = null; await feed.DispatchAsync(default); Assert.Equal(2, transport.Sends);
    }
    [Fact]
    public async Task NewCostsDuringInflightResponseAreNotLost()
    {
        var source = new Source(); var store = new Store(); var transport = new Transport();
        var pending = new TaskCompletionSource<ArenaReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Send = () => pending.Task;
        var feed = new ArenaFeed(source, transport, store, _ => { }, new Clock());
        await feed.ReconcileAsync(default); var dispatch = feed.DispatchAsync(default);
        source.Rows = [Row("2")]; await feed.ReconcileAsync(default);
        pending.SetResult(new("/v1/trades", 202, 1, 0, 1)); await dispatch;
        transport.Send = null; await feed.DispatchAsync(default);
        Assert.Equal(2, transport.Sends);
    }
    [Fact]
    public async Task DiskFailurePreventsTransmission_AndFlatHeartbeatsAlwaysReadFreshSource()
    {
        var source = new Source(); var store = new Store(); var transport = new Transport();
        var feed = new ArenaFeed(source, transport, store, _ => { }, new Clock());
        await feed.ReconcileAsync(default); store.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => feed.DispatchAsync(default)); Assert.Equal(0, transport.Sends);
        await feed.SnapshotAsync(default); await feed.SnapshotAsync(default);
        Assert.Equal(2, source.Snapshots); Assert.Equal(2, transport.Positions);
    }
    [Fact]
    public async Task DuplicateSourceIsNotSilentlyTruncated()
    {
        var source = new Source { Rows = [Row(), Row()] }; var store = new Store(); var transport = new Transport();
        var feed = new ArenaFeed(source, transport, store, _ => { });
        await Assert.ThrowsAsync<InvalidDataException>(() => feed.ReconcileAsync(default));
        Assert.Empty((await store.LoadAsync(default)).Trades);
    }
    [Fact]
    public async Task FullOutboxRetainsThePreviousCheckpoint_InsteadOfDroppingPositions()
    {
        var source = new Source { Rows = Enumerable.Range(0, 1001).Select(i => new JsonObject { ["trade_id"] = "native-" + i }).ToArray() };
        var store = new Store(); var feed = new ArenaFeed(source, new Transport(), store, _ => { });
        await Assert.ThrowsAsync<InvalidDataException>(() => feed.ReconcileAsync(default));
        Assert.Empty((await store.LoadAsync(default)).Trades);
    }
    [Fact]
    public async Task AtomicFileStatePersistsAndCorruptionIsNotReset()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arena-feed-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ArenaFileStore(directory); var state = new ArenaFeedState();
            state.Trades.Add("t", new("t", "{}", default, default)); await store.SaveAsync(state, default);
            Assert.Single((await new ArenaFileStore(directory).LoadAsync(default)).Trades);
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(directory, "outbox.json")));
            await File.WriteAllTextAsync(Path.Combine(directory, "outbox.json"), "corrupt");
            await Assert.ThrowsAsync<JsonException>(() => store.LoadAsync(default));
        }
        finally { Directory.Delete(directory, true); }
    }
}
