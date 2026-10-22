using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TradingBot.Arena;

public sealed record ArenaFeedTrade(string TradeId, string Payload, DateTimeOffset FirstAttempt,
    DateTimeOffset NextAttempt, int Attempts = 0, bool Received = false, bool Rejected = false,
    string? DeliveredHash = null);
public sealed class ArenaFeedState
{
    public Dictionary<string, ArenaFeedTrade> Trades { get; set; } = new(StringComparer.Ordinal);
}
public interface IArenaFeedStore
{
    Task<ArenaFeedState> LoadAsync(CancellationToken ct);
    Task SaveAsync(ArenaFeedState state, CancellationToken ct);
}
public interface IArenaFeedSource
{
    // Implementations use native executions/account-log economics and the importer's reconstruction engine.
    Task<IReadOnlyList<JsonObject>> ClosedAsync(CancellationToken ct);
    // A NEW, complete native observation, filtered to proven bot-owned positions. Never a queued old snapshot.
    Task<JsonObject> SnapshotAsync(CancellationToken ct);
}
public interface IArenaFeedTransport
{
    Task<ArenaReceipt> TradesAsync(IReadOnlyList<JsonObject> trades, CancellationToken ct);
    Task<ArenaReceipt> PositionsAsync(JsonObject positions, CancellationToken ct);
}

/// <summary>Independent reporting only. Nothing in this component can place an order or change a portfolio.</summary>
public sealed class ArenaFeed(IArenaFeedSource source, IArenaFeedTransport transport, IArenaFeedStore store,
    Action<string> log, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private const int MaximumTrades = 10000;
    private const int MaximumPending = 1000;
    public async Task ReconcileAsync(CancellationToken ct)
    {
        var rows = await source.ClosedAsync(ct);
        // Validate all identities/bodies before touching the durable outbox. A broken result isn't an empty history.
        if (rows.Any(r => r["trade_id"]?.ToString() is not { Length: > 0 })
            || rows.Select(r => r["trade_id"]!.ToString()).Distinct(StringComparer.Ordinal).Count() != rows.Count)
            throw new InvalidDataException("Arena source returned missing or duplicate native trade identities.");
        foreach (var row in rows) _ = ArenaContract.Batches([row], 1);
        await _gate.WaitAsync(ct);
        try
        {
            var state = await store.LoadAsync(ct);
            var now = _time.GetUtcNow();
            foreach (var row in rows)
            {
                string id = row["trade_id"]!.ToString(), payload = row.ToJsonString(), hash = ArenaContract.Digest(row);
                if (state.Trades.TryGetValue(id, out var old))
                {
                    // First attempt is persisted BEFORE transmission. Crash/retry can only shorten, never extend, the 24h window.
                    if (old.Rejected || old.FirstAttempt != default && now >= old.FirstAttempt.AddHours(24)) continue;
                    if (ArenaContract.Digest(JsonNode.Parse(old.Payload)!) == hash) continue;
                    state.Trades[id] = old with { Payload = payload, NextAttempt = now };
                }
                else state.Trades.Add(id, new(id, payload, default, now));
            }
            if (state.Trades.Count > MaximumTrades || state.Trades.Values.Count(Pending) > MaximumPending)
                throw new InvalidDataException("Arena durable outbox bound reached; source checkpoint retained, no economic event dropped. Reporting needs recovery.");
            await store.SaveAsync(state, ct);
        }
        finally { _gate.Release(); }
    }
    private static bool Pending(ArenaFeedTrade t) => !t.Rejected
        && (!t.Received || t.DeliveredHash != ArenaContract.Digest(JsonNode.Parse(t.Payload)!));

    public async Task DispatchAsync(CancellationToken ct)
    {
        ArenaFeedTrade? trade;
        await _gate.WaitAsync(ct);
        try
        {
            var state = await store.LoadAsync(ct);
            var now = _time.GetUtcNow();
            trade = state.Trades.Values.Where(Pending).Where(t => t.NextAttempt <= now)
                .OrderBy(t => t.NextAttempt).ThenBy(t => t.TradeId, StringComparer.Ordinal).FirstOrDefault();
            if (trade is null) return;
            if (trade.Received && trade.FirstAttempt != default && now >= trade.FirstAttempt.AddHours(24))
            {
                state.Trades[trade.TradeId] = trade with { Rejected = true };
                await store.SaveAsync(state, ct); return;
            }
            trade = trade with { FirstAttempt = trade.FirstAttempt == default ? now : trade.FirstAttempt, Attempts = trade.Attempts + 1 };
            state.Trades[trade.TradeId] = trade;
            await store.SaveAsync(state, ct); // No network write unless this durable intent succeeds.
        }
        finally { _gate.Release(); }
        ArenaReceipt? receipt = null;
        Exception? failure = null;
        try { receipt = await transport.TradesAsync([JsonNode.Parse(trade.Payload)!.AsObject()], ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { failure = ex; }
        await _gate.WaitAsync(ct);
        try
        {
            var state = await store.LoadAsync(ct);
            var current = state.Trades[trade.TradeId]; // Reconciliation may have queued newer settled costs while HTTP was in flight.
            if (failure is null)
            {
                string delivered = ArenaContract.Digest(JsonNode.Parse(trade.Payload)!);
                // A 200 on the first ever local attempt is an imported/external receipt of unknown age. Do not guess its correction deadline.
                bool external = !trade.Received && trade.Attempts == 1 && receipt!.Accepted == 0;
                state.Trades[trade.TradeId] = current with
                {
                    Received = true,
                    DeliveredHash = delivered,
                    FirstAttempt = external ? _time.GetUtcNow().AddHours(-24) : current.FirstAttempt,
                    NextAttempt = _time.GetUtcNow()
                };
                log($"Arena /v1/trades HTTP {receipt!.Status}: submitted={receipt.Submitted} accepted={receipt.Accepted} updated={receipt.Updated}");
            }
            else
            {
                bool terminal = failure is ArenaHttpException http && (int)http.Status is >= 400 and < 500 && http.Status != HttpStatusCode.TooManyRequests;
                var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(trade.Attempts, 8))));
                if (failure is ArenaHttpException { RetryAfter: { } after } && after > delay) delay = after;
                state.Trades[trade.TradeId] = current with { Rejected = terminal, NextAttempt = _time.GetUtcNow() + delay };
                log($"Arena reporting trade failure; terminal={terminal}: {failure}");
            }
            await store.SaveAsync(state, ct);
        }
        finally { _gate.Release(); }
    }
    public async Task SnapshotAsync(CancellationToken ct)
    {
        var current = await source.SnapshotAsync(ct);
        var receipt = await transport.PositionsAsync(current, ct);
        log($"Arena /v1/positions HTTP {receipt.Status}: submitted={receipt.Submitted} accepted={receipt.Accepted} updated={receipt.Updated}");
    }
}

/// <summary>Atomic replace, private permissions, bounded JSON; corrupt/missing state is never silently reset.</summary>
public sealed class ArenaFileStore : IArenaFeedStore
{
    private readonly string _path;
    public ArenaFileStore(string directory)
    {
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _path = Path.Combine(Path.GetFullPath(directory), "outbox.json");
    }
    public async Task<ArenaFeedState> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return new();
        if (new FileInfo(_path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Arena outbox exceeds 16MiB; recover without dropping receipts.");
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<ArenaFeedState>(stream, cancellationToken: ct) ?? throw new InvalidDataException("Invalid Arena outbox.");
    }
    public async Task SaveAsync(ArenaFeedState state, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(state);
        if (body.Length > 16 * 1024 * 1024) throw new InvalidDataException("Arena outbox exceeds 16MiB; previous state retained.");
        string temporary = _path + "." + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options)) { await stream.WriteAsync(body, ct); stream.Flush(true); }
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
