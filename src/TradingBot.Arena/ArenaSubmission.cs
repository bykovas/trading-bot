using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TradingBot.Arena;

public sealed record ArenaReceipt(string Path, int Status, int Accepted, int Updated, int Submitted);
public sealed class ArenaHttpException(HttpStatusCode status, string message, TimeSpan? retryAfter = null) : InvalidOperationException(message)
{
    public HttpStatusCode Status { get; } = status;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
public sealed record ArenaImportPlan(JsonObject Profile, IReadOnlyList<JsonObject> Trades, JsonObject Positions,
    string SourceHash, string SchemaHash, string RegistryHash)
{
    public string Digest => ArenaContract.Digest(JsonSerializer.SerializeToNode(new { Profile, Trades, Positions, SourceHash, SchemaHash, RegistryHash })!);
}

/// <summary>Only Arena endpoints; callers provide environment credentials and a fully approved, offline-validated plan.</summary>
public sealed class ArenaSubmission : IArenaFeedTransport
{
    private readonly HttpClient _http;
    private readonly string _key;
    private readonly Uri _origin;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    public ArenaSubmission(HttpClient http, string key, Uri? origin = null, TimeProvider? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http; _key = key; _origin = origin ?? new Uri("https://arena.debyko.com"); _clock = clock ?? TimeProvider.System;
        if (_origin != new Uri("https://arena.debyko.com") && !_origin.IsLoopback) throw new ArgumentException("Arena key may target only Arena or loopback fixtures.");
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("DEBYKO_ARENA_KEY is required in the protected environment.");
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, _clock, ct));
    }
    public async Task<IReadOnlyList<ArenaReceipt>> ImportAsync(ArenaImportPlan plan, string approvedDigest, ArenaContract contract,
        IReadOnlyList<ArenaRegistryListing> registry, Func<JsonObject, CancellationToken, Task<DateTimeOffset>> verifyCurrentSnapshot,
        Func<ArenaReceipt, CancellationToken, Task> checkpoint, CancellationToken ct, bool profileAlreadyRecorded = false)
    {
        if (approvedDigest != plan.Digest) throw new InvalidOperationException("The owner-approved payload digest does not match; no writes.");
        if (contract.SchemaHash != plan.SchemaHash || plan.RegistryHash != RegistryHash(registry)) throw new InvalidDataException("Recorded contracts changed; revalidate and approve before writes.");
        var errors = contract.Validate(plan.Profile, plan.Trades, plan.Positions, registry, _clock.GetUtcNow());
        if (errors.Count != 0) throw new InvalidDataException("Whole-import preflight failed before any write: " + string.Join("; ", errors));
        var batches = ArenaContract.Batches(plan.Trades, maximumTrades: 1); // Size-check every batch before profile or any trade is written.
        byte[] profile = Body(plan.Profile), positions = Body(plan.Positions);
        var snapshotAt = await verifyCurrentSnapshot(plan.Positions, ct);
        if (snapshotAt > _clock.GetUtcNow() || _clock.GetUtcNow() - snapshotAt > TimeSpan.FromSeconds(60)) throw new InvalidDataException("Open-position evidence is stale; refresh before any import write.");
        var receipts = new List<ArenaReceipt>();
        async Task Send(HttpMethod method, string path, byte[] body, int count)
        {
            var receipt = await SendAsync(method, path, body, count, ct, retryTransient: path != "/v1/positions");
            receipts.Add(receipt); await checkpoint(receipt, ct); // If durable checkpoint fails, stop before the next write.
        }
        // A successful, digest-bound profile receipt is resumable: capital guards correctly reject a new backdated declaration after trades exist.
        if (!profileAlreadyRecorded) await Send(HttpMethod.Put, "/v1/profile", profile, 1);
        foreach (var batch in batches) await Send(HttpMethod.Post, "/v1/trades", batch, JsonNode.Parse(batch)!.AsArray().Count);
        // Retries may take time; never send a formerly fresh open snapshot as though it were current.
        snapshotAt = await verifyCurrentSnapshot(plan.Positions, ct);
        if (snapshotAt > _clock.GetUtcNow() || _clock.GetUtcNow() - snapshotAt > TimeSpan.FromSeconds(60)) throw new InvalidDataException("Snapshot became stale after trades; refresh and resume the positions step.");
        await Send(HttpMethod.Post, "/v1/positions", positions, plan.Positions["positions"]!.AsArray().Count);
        return receipts;
    }
    private static byte[] Body(JsonNode node)
    {
        byte[] value = JsonSerializer.SerializeToUtf8Bytes(node);
        if (value.Length > 65536) throw new InvalidDataException("Arena payload exceeds 64 KiB; never trim fills or positions.");
        return value;
    }
    public static string RegistryHash(IReadOnlyList<ArenaRegistryListing> registry) => ArenaContract.Digest(JsonSerializer.SerializeToNode(registry.OrderBy(r => r.Symbol, StringComparer.Ordinal).ThenBy(r => r.ListedAt).ToArray())!);
    public async Task<ArenaReceipt> TradesAsync(IReadOnlyList<JsonObject> trades, CancellationToken ct)
    {
        var batches = ArenaContract.Batches(trades);
        if (batches.Count != 1) throw new InvalidDataException("Reporting dispatch requires one bounded batch.");
        return await SendAsync(HttpMethod.Post, "/v1/trades", batches[0], trades.Count, ct);
    }
    public Task<ArenaReceipt> PositionsAsync(JsonObject positions, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/v1/positions", Body(positions), positions["positions"]?.AsArray().Count
            ?? throw new InvalidDataException("A complete positions array is required, including while flat."), ct, retryTransient: false);
    private async Task<ArenaReceipt> SendAsync(HttpMethod method, string path, byte[] body, int count, CancellationToken ct, bool retryTransient = true)
    {
        for (int attempt = 0; attempt <= 4; attempt++)
        {
            using var request = new HttpRequestMessage(method, new Uri(_origin, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
            request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (retryTransient && (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 4)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - _clock.GetUtcNow()) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                await _delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1), ct); continue;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            byte[] prefix = new byte[16385]; int length = 0, read;
            while (length < prefix.Length && (read = await stream.ReadAsync(prefix.AsMemory(length), ct)) > 0) length += read;
            string text = System.Text.Encoding.UTF8.GetString(prefix, 0, length).Replace(_key, "[key]", StringComparison.Ordinal);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Accepted))
                throw new ArenaHttpException(response.StatusCode, $"Arena {method} {path}: HTTP {(int)response.StatusCode}: {text[..Math.Min(500, text.Length)]}",
                    response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - _clock.GetUtcNow()));
            if (length == prefix.Length) throw new InvalidDataException("Arena receipt exceeds its safety bound; do not continue blindly.");
            var json = JsonNode.Parse(text)?.AsObject() ?? throw new InvalidDataException("Missing Arena receipt.");
            int accepted = json["accepted"]?.GetValue<int>() ?? throw new InvalidDataException("Missing accepted count.");
            int updated = json["updated"]?.GetValue<int>() ?? throw new InvalidDataException("Missing updated count.");
            if (accepted < 0 || updated < 0 || (long)accepted + updated > count || response.StatusCode == HttpStatusCode.OK && accepted + updated != 0)
                throw new InvalidDataException("Arena receipt counts do not reconcile to the submitted batch.");
            return new(path, (int)response.StatusCode, accepted, updated, count);
        }
        throw new InvalidOperationException("Arena retry bound reached.");
    }
}

public static class ArenaSnapshotCheck
{
    public static void Matches(JsonObject native, JsonObject expected)
    {
        var opens = native["openPositions"] as JsonArray ?? throw new InvalidDataException("Missing native open positions, not an empty snapshot.");
        var expectedPositions = expected["positions"] as JsonArray ?? throw new InvalidDataException("Missing approved positions.");
        if (opens.Count != expectedPositions.Count) throw new InvalidDataException("Native open scope changed since approval; review bot/manual ownership again before replacing the snapshot.");
        foreach (var position in expectedPositions.OfType<JsonObject>())
        {
            var matches = opens.OfType<JsonObject>().Where(p => p["symbol"]?.ToString().Equals(position["instrument"]?.ToString(), StringComparison.OrdinalIgnoreCase) == true).ToArray();
            if (matches.Length != 1 || matches[0]["side"]?.ToString() != position["side"]?.ToString()
                || KrakenEvidence.Number(matches[0], "size") != KrakenEvidence.Number(position, "size")
                || KrakenEvidence.Number(matches[0], "price") != KrakenEvidence.Number(position, "entry_price"))
                throw new InvalidDataException("Native bot position changed after the preview; rebuild the source evidence, not a stale snapshot.");
        }
    }
}
