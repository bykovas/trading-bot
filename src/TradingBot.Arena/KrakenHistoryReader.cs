using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TradingBot.Arena;

public sealed record HistoryCapture(string AccountUid, DateTimeOffset CapturedAt, IReadOnlyList<JsonObject> Executions,
    IReadOnlyList<JsonObject> Ledger, JsonObject OpenPositions, DateTimeOffset OpenPositionsAt, bool Complete = true, IReadOnlyList<JsonObject>? PositionUpdates = null);

/// <summary>GET only. Explicit GET-only import credentials mode; no trading broker, order method or best-effort history truncation.</summary>
public sealed class KrakenHistoryReader
{
    private readonly HttpClient _http;
    private readonly string _key, _secret;
    private readonly Uri _origin;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private long _nonce;
    private readonly bool _existingKeyReadOnly;
    private static readonly HashSet<string> Paths = ["/api/auth/v1/api-keys/v3/check", "/api/history/v3/executions", "/api/history/v3/account-log", "/api/history/v3/positions", "/derivatives/api/v3/openpositions"];
    public KrakenHistoryReader(HttpClient http, string key, string secret, Uri? origin = null, TimeProvider? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, bool existingKeyReadOnly = false)
    {
        _existingKeyReadOnly = existingKeyReadOnly;
        _http = http; _key = key; _secret = secret; _origin = origin ?? new Uri("https://futures.kraken.com");
        if (_origin != new Uri("https://futures.kraken.com") && !_origin.IsLoopback) throw new ArgumentException("Private credentials may go only to Kraken or a loopback fixture.");
        _clock = clock ?? TimeProvider.System;
        _delay = delay ?? ((duration, ct) => Task.Delay(duration, _clock, ct));
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret)) throw new ArgumentException("Kraken credentials are required in the environment.");
    }
    private string Redact(string value) => value.Replace(_key, "[key]", StringComparison.Ordinal).Replace(_secret, "[secret]", StringComparison.Ordinal);
    private async Task<(JsonObject Body, string? Next)> GetAsync(string path, Dictionary<string, string> query, CancellationToken ct)
    {
        if (!Paths.Contains(path)) throw new InvalidOperationException("Endpoint is not on the read-only allowlist.");
        string data = string.Join("&", query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        for (int attempt = 0; ; attempt++)
        {
            long nonce = _nonce = Math.Max(_clock.GetUtcNow().ToUnixTimeMilliseconds(), _nonce + 1);
            string n = _existingKeyReadOnly ? "" : nonce.ToString(CultureInfo.InvariantCulture);
            string signingPath = path.StartsWith("/derivatives/", StringComparison.Ordinal) ? path["/derivatives".Length..] : path;
            // Same v3 signature as KrakenFuturesBroker: HMAC-SHA512(SHA256(encoded query + nonce + signing path)).
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(data + n + signingPath));
            using var hmac = new HMACSHA512(Convert.FromBase64String(_secret));
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_origin, path + (data.Length == 0 ? "" : "?" + data)));
            request.Headers.Add("APIKey", _key);
            // Kraken Derivatives REST makes Nonce optional. Omit it for a shared working key to avoid advancing the bot's nonce.
            if (!_existingKeyReadOnly) request.Headers.Add("Nonce", n);
            request.Headers.Add("Authent", Convert.ToBase64String(hmac.ComputeHash(digest)));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                bool transient = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                if (transient && attempt < 4)
                {
                    TimeSpan wait = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - _clock.GetUtcNow()) ?? TimeSpan.FromSeconds(12 * (attempt + 1));
                    await _delay(wait < TimeSpan.FromSeconds(12) ? TimeSpan.FromSeconds(12) : wait, ct); continue;
                }
                string body = await ReadBodyAsync(response, 8192, true, ct);
                throw new InvalidOperationException($"Kraken GET {path}: HTTP {(int)response.StatusCode}: {Redact(body)[..Math.Min(500, Redact(body).Length)]}");
            }
            if (response.Content.Headers.ContentLength > 32 * 1024 * 1024) throw new InvalidDataException("History page exceeds 32 MiB; capture is incomplete.");
            string payload = await ReadBodyAsync(response, 32 * 1024 * 1024, false, ct);
            var bodyNode = JsonNode.Parse(payload) as JsonObject ?? throw new InvalidDataException("Expected a Kraken history object.");
            if (bodyNode["error"] is not null || bodyNode["result"] is JsonValue result && result.GetValue<string>() != "success")
                throw new InvalidDataException("Kraken rejected history request: " + Redact(bodyNode["error"]?.ToString() ?? "unsuccessful result"));
            string? next = response.Headers.TryGetValues("Next-Continuation-Token", out var values) ? values.Single() : bodyNode["continuationToken"]?.GetValue<string>();
            return (bodyNode, string.IsNullOrEmpty(next) ? null : next);
        }
    }
    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, int maximum, bool prefix, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > maximum)
            {
                if (!prefix) throw new InvalidDataException("History page exceeds its byte bound; capture is incomplete.");
                buffer.Write(chunk, 0, maximum - (int)buffer.Length); break;
            }
            buffer.Write(chunk, 0, count);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
    public async Task<(string AccountUid, JsonObject Body, DateTimeOffset ReceivedAt)> CurrentOpenPositionsAsync(CancellationToken ct)
    {
        string account = await CheckIdentityAsync(ct);
        var (body, _) = await GetAsync("/derivatives/api/v3/openpositions", [], ct);
        if (body["openPositions"] is not JsonArray) throw new InvalidDataException("Missing current open positions.");
        return (account, body, _clock.GetUtcNow());
    }
    private async Task<string> CheckIdentityAsync(CancellationToken ct)
    {
        var (identity, _) = await GetAsync("/api/auth/v1/api-keys/v3/check", [], ct);
        string? permission = identity["permissions"]?["general"]?.GetValue<string>();
        if (_existingKeyReadOnly ? permission is not ("READ_ONLY" or "FULL_ACCESS") : permission != "READ_ONLY" || identity["permissions"]?["transfer"]?.GetValue<string>() != "NO_ACCESS")
            throw new InvalidOperationException("Default capture requires general=READ_ONLY, transfer=NO_ACCESS. Existing-key GET-only mode must be selected explicitly and never grants order/transfer access.");
        string account = identity["accountUid"]?.GetValue<string>() ?? throw new InvalidDataException("Missing account identity.");
        return account;
    }
    public async Task<HistoryCapture> CaptureAsync(CancellationToken ct)
    {
        string account = await CheckIdentityAsync(ct);
        DateTimeOffset cutoff = _clock.GetUtcNow();
        long bytes = 0;
        JsonObject Keep(JsonObject row)
        {
            bytes += Encoding.UTF8.GetByteCount(row.ToJsonString());
            if (bytes > 64 * 1024 * 1024) throw new InvalidDataException("Capture exceeds the 64 MiB safety bound; no partial import is permitted.");
            return row.DeepClone().AsObject();
        }
        List<JsonObject> executions = []; HashSet<string> ids = new(StringComparer.Ordinal), tokens = new(StringComparer.Ordinal);
        string? next = null;
        for (int page = 0; ; page++)
        {
            if (page >= 1000) throw new InvalidDataException("History safety bound reached; capture is incomplete, never import it.");
            await _delay(TimeSpan.FromSeconds(12), ct);
            Dictionary<string, string> query = new() { ["count"] = "1000", ["sort"] = "asc", ["before"] = cutoff.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) };
            if (next is not null) query["continuation_token"] = next;
            var (body, token) = await GetAsync("/api/history/v3/executions", query, ct);
            CheckAccount(body, account);
            var elements = body["elements"] as JsonArray ?? throw new InvalidDataException("Missing execution history, not an empty history.");
            foreach (JsonNode? e in elements)
            {
                var row = e as JsonObject ?? throw new InvalidDataException("Malformed execution history row.");
                string id = row["uid"]?.GetValue<string>() ?? throw new InvalidDataException("Execution event has no UID.");
                if (!ids.Add(id)) throw new InvalidDataException("Repeated execution event; pagination is not proven complete.");
                executions.Add(Keep(row));
            }
            if (token is null) break;
            if (!tokens.Add(token) || elements.Count == 0) throw new InvalidDataException("Non-progressing execution pagination.");
            next = token;
        }
        List<JsonObject> positions = []; ids.Clear(); tokens.Clear(); next = null;
        for (int page = 0; ; page++)
        {
            if (page >= 1000) throw new InvalidDataException("Position history safety bound reached; capture is incomplete.");
            await _delay(TimeSpan.FromSeconds(12), ct);
            Dictionary<string, string> query = new() { ["count"] = "1000", ["sort"] = "asc", ["before"] = cutoff.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) };
            if (next is not null) query["continuation_token"] = next;
            var (body, token) = await GetAsync("/api/history/v3/positions", query, ct);
            CheckAccount(body, account);
            var elements = body["elements"] as JsonArray ?? throw new InvalidDataException("Missing position history.");
            foreach (var element in elements)
            {
                var row = element as JsonObject ?? throw new InvalidDataException("Malformed position history.");
                // Position events share uid (account/scope), unlike executions. The full event is the deduplication identity.
                if (row["timestamp"] is null || row["event"]?["PositionUpdate"] is not JsonObject) throw new InvalidDataException("Malformed position update.");
                string id = ArenaContract.Digest(row);
                if (!ids.Add(id)) throw new InvalidDataException("Repeated position event.");
                positions.Add(Keep(row));
            }
            if (token is null) break;
            if (!tokens.Add(token) || elements.Count == 0) throw new InvalidDataException("Non-progressing position pagination.");
            next = token;
        }
        // No artificial window start: positions closed in the import window may have opened earlier.
        // ID pagination is inclusive. 'before' is milliseconds, NOT an account-log ID.
        List<JsonObject> ledger = []; long? upperId = null; ids.Clear();
        for (int page = 0; ; page++)
        {
            if (page >= 2000) throw new InvalidDataException("Ledger safety bound reached; capture is incomplete, never import it.");
            await _delay(TimeSpan.FromSeconds(12), ct);
            Dictionary<string, string> query = new() { ["count"] = "500", ["sort"] = "desc", ["before"] = cutoff.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) };
            if (upperId is { } upper) query["to"] = upper.ToString(CultureInfo.InvariantCulture);
            var (body, _) = await GetAsync("/api/history/v3/account-log", query, ct);
            CheckAccount(body, account);
            var rows = body["logs"] as JsonArray ?? throw new InvalidDataException("Missing account log, not an empty ledger.");
            long minimum = long.MaxValue;
            foreach (JsonNode? e in rows)
            {
                var row = e as JsonObject ?? throw new InvalidDataException("Malformed ledger row.");
                long id = long.Parse(row["id"]?.ToString() ?? "", CultureInfo.InvariantCulture);
                if (id < 1 || upperId is { } limit && id > limit || !ids.Add(id.ToString(CultureInfo.InvariantCulture))) throw new InvalidDataException("Ledger pagination repeated or exceeded its ID bound.");
                minimum = Math.Min(minimum, id); ledger.Add(Keep(row));
            }
            if (rows.Count == 0 || minimum == 1) break;
            upperId = minimum - 1; // Ask until an empty page; never infer completeness from a short server-capped page.
        }
        await _delay(TimeSpan.FromSeconds(12), ct);
        var (open, _) = await GetAsync("/derivatives/api/v3/openpositions", [], ct);
        if (open["openPositions"] is not JsonArray) throw new InvalidDataException("Missing current open positions.");
        return new(account, cutoff, executions, ledger, open, _clock.GetUtcNow(), PositionUpdates: positions);
    }
    private static void CheckAccount(JsonObject body, string expected)
    {
        if (body["accountUid"]?.GetValue<string>() != expected) throw new InvalidDataException("History account changed between pages.");
    }
}
