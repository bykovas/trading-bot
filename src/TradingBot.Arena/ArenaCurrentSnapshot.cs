using System.Text.Json.Nodes;

namespace TradingBot.Arena;

public sealed record ArenaHeldPosition(string Symbol, string Side, decimal Size, decimal Entry,
    DateTimeOffset OpenedAt, decimal Leverage);

public static class ArenaCurrentSnapshot
{
    public static JsonObject Create(JsonObject native, IReadOnlyList<ArenaHeldPosition> botHeld, DateTimeOffset observedAt, DateTimeOffset now)
    {
        if (observedAt > now || now - observedAt > TimeSpan.FromSeconds(60)) throw new InvalidDataException("Native position observation is stale or from the future.");
        var scope = native["openPositions"] as JsonArray ?? throw new InvalidDataException("Missing native position scope is not flat.");
        if (botHeld.Select(p => p.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).Count() != botHeld.Count)
            throw new InvalidDataException("Ambiguous bot position scope.");
        JsonArray rows = [];
        foreach (var p in botHeld)
        {
            var matches = scope.OfType<JsonObject>().Where(n => n["symbol"]?.ToString().Equals(p.Symbol, StringComparison.OrdinalIgnoreCase) == true).ToArray();
            if (matches.Length != 1 || matches[0]["side"]?.ToString() != p.Side || p.Side is not ("long" or "short")
                || KrakenEvidence.Number(matches[0], "size") != p.Size || KrakenEvidence.Number(matches[0], "price") != p.Entry
                || p.Size <= 0 || p.Entry <= 0 || p.Leverage <= 0 || p.OpenedAt > now)
                throw new InvalidDataException("Bot/native position mismatch; never replace it with a false flat or modeled snapshot.");
            rows.Add(new JsonObject
            {
                ["venue"] = "KRAKEN-FUTURES",
                ["instrument"] = p.Symbol,
                ["side"] = p.Side,
                ["opened_at"] = p.OpenedAt.ToUniversalTime().ToString("O"),
                ["entry_price"] = ReconstructedPosition.Digits(p.Entry),
                ["size"] = ReconstructedPosition.Digits(p.Size),
                ["size_unit"] = "base",
                ["leverage"] = ReconstructedPosition.Digits(p.Leverage)
            });
        }
        return new() { ["account"] = "live", ["positions"] = rows };
    }
}
