using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace TradingBot.Arena;

public sealed record ArenaProfile(string? Handle, string Bio, decimal? DeclaredCapital, string? Currency);
public enum ArenaCapitalSync { Disabled, Unchanged, Updated }

public static class ArenaCapitalConfiguration
{
    public const string Variable = "ARENA_DECLARED_CAPITAL_USD";

    public static decimal? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
            CultureInfo.InvariantCulture, out decimal capital) || capital <= 0)
            throw new InvalidDataException($"{Variable} must be a positive invariant USD decimal, or unset to disable capital reporting.");
        return capital;
    }
}

public sealed partial class ArenaSubmission
{
    public async Task<ArenaProfile> ProfileAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_origin, "/v1/profile"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        byte[] bytes = new byte[16385]; int length = 0, read;
        while (length < bytes.Length && (read = await stream.ReadAsync(bytes.AsMemory(length), ct)) > 0) length += read;
        if (response.StatusCode != HttpStatusCode.OK)
            throw new ArenaHttpException(response.StatusCode, $"Arena GET /v1/profile: HTTP {(int)response.StatusCode}",
                response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - _clock.GetUtcNow()));
        if (length == bytes.Length) throw new InvalidDataException("Arena profile exceeds its safety bound.");
        JsonObject json = JsonNode.Parse(Encoding.UTF8.GetString(bytes, 0, length))?.AsObject()
            ?? throw new InvalidDataException("Missing Arena profile.");
        if (!json.TryGetPropertyValue("declared_capital", out JsonNode? declared)
            || !json.TryGetPropertyValue("handle", out JsonNode? handle) || json["bio"] is not JsonValue bio || !bio.TryGetValue<string>(out string? biography))
            throw new InvalidDataException("Incomplete Arena profile; no capital write.");
        decimal? capital = null;
        if (declared is not null)
        {
            if (!decimal.TryParse(declared.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal amount) || amount <= 0)
                throw new InvalidDataException("Invalid Arena capital; no capital write.");
            capital = amount;
        }
        string? currency = json["capital_ccy"]?.GetValue<string>();
        if (capital is not null && string.IsNullOrWhiteSpace(currency)) throw new InvalidDataException("Arena capital currency is missing; no capital write.");
        return new(handle?.GetValue<string>(), biography, capital, currency);
    }

    /// <summary>Startup/configuration action only. Unset never writes or clears capital; no balance or trade sizing input exists.</summary>
    public async Task<ArenaCapitalSync> SynchronizeDeclaredCapitalAsync(decimal? configuredUsd, CancellationToken ct)
    {
        if (configuredUsd is null) return ArenaCapitalSync.Disabled;
        if (configuredUsd <= 0) throw new ArgumentOutOfRangeException(nameof(configuredUsd));
        ArenaProfile current = await ProfileAsync(ct);
        if (current.DeclaredCapital == configuredUsd && current.Currency == "USD") return ArenaCapitalSync.Unchanged;
        if (string.IsNullOrWhiteSpace(current.Handle)) throw new InvalidDataException("Set an Arena profile before configuring declared capital; no handle is invented.");
        JsonObject body = new()
        {
            ["handle"] = current.Handle, ["bio"] = current.Bio,
            ["declared_capital"] = configuredUsd.Value.ToString(CultureInfo.InvariantCulture), ["capital_ccy"] = "USD"
        };
        // No blind retry with a new effective timestamp after an ambiguous PUT. A restart reads the profile again.
        await SendAsync(HttpMethod.Put, "/v1/profile", Body(body), 1, ct, retryTransient: false);
        return ArenaCapitalSync.Updated;
    }
}
