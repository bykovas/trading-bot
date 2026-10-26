using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TradingBot.Arena;

public sealed record ArenaPaperFill(string Id, string Symbol, string Side, bool Closing, DateTimeOffset Time,
    decimal Size, decimal Price, decimal Entry, decimal Fee, decimal Leverage, decimal? Margin,
    string Source, string? OrderId = null, string? ExitReason = null);
public sealed record ArenaPaperHistory(IReadOnlyList<JsonObject> Closed, IReadOnlyList<JsonObject> Open)
{
    public JsonObject Snapshot() => new()
    {
        ["account"] = "demo", ["positions"] = new JsonArray(Open.Select(p => p.DeepClone()).ToArray())
    };
}

/// <summary>Only full modeled opens/closes are supported. Missing or ambiguous paper evidence stops reporting, not trading.</summary>
public static class ArenaPaperReconstruction
{
    public static ArenaPaperHistory Build(string instance, IReadOnlyList<ArenaPaperFill> fills, DateTimeOffset now)
    {
        var active = new Dictionary<string, ArenaPaperFill>(StringComparer.Ordinal);
        var closed = new List<JsonObject>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset previous = DateTimeOffset.MinValue;
        foreach (var f in fills)
        {
            if (f.Source != "MODELED_PORTFOLIO" || !string.IsNullOrEmpty(f.OrderId))
                throw new InvalidDataException("Paper reporting requires modeled fills without exchange order identities.");
            if (!ids.Add(f.Id) || string.IsNullOrWhiteSpace(f.Id) || f.Side is not ("long" or "short")
                || f.Size <= 0 || f.Price <= 0 || f.Entry <= 0 || f.Fee < 0 || f.Leverage <= 0
                || f.Time > now || f.Time < previous || f.Time.Year < 1970
                || !f.Symbol.StartsWith("PF_", StringComparison.Ordinal) || !f.Symbol.EndsWith("USD", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid paper execution evidence.");
            previous = f.Time;
            if (!f.Closing)
            {
                if (f.Price != f.Entry || f.Margin is not > 0 || !active.TryAdd(f.Symbol, f))
                    throw new InvalidDataException("Ambiguous paper opening, margin or scale-in; do not approximate.");
                continue;
            }
            if (!active.Remove(f.Symbol, out var open) || open.Side != f.Side || open.Size != f.Size
                || open.Entry != f.Entry || open.Leverage != f.Leverage)
                throw new InvalidDataException("Paper close does not match a complete modeled opening.");
            var row = Position(open);
            row["trade_id"] = "paper-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instance + ":" + open.Id))).ToLowerInvariant();
            row["closed_at"] = f.Time.ToUniversalTime().ToString("O");
            row["exit_price"] = ReconstructedPosition.Digits(f.Price);
            row["fees"] = ReconstructedPosition.Digits(open.Fee + f.Fee);
            // The virtual portfolio does not book funding. Do not present this as an exchange cost.
            row["funding"] = "0";
            if (Reason(f.ExitReason) is { } reason) row["exit_reason"] = reason;
            row["fills"] = new JsonArray(Leg(open), Leg(f));
            closed.Add(row);
        }
        return new(closed, active.Values.OrderBy(p => p.Symbol, StringComparer.Ordinal).Select(Position).ToArray());
    }

    public static void CheckFresh(DateTimeOffset updated, DateTimeOffset now)
    {
        if (updated > now || now - updated > TimeSpan.FromSeconds(180))
            throw new InvalidDataException("Paper portfolio is stale or from the future; do not publish a fresh heartbeat.");
    }

    public static void CheckHoldings(ArenaPaperHistory history, IReadOnlyList<ArenaHeldPosition> held)
    {
        if (history.Open.Count != held.Count || held.Select(p => p.Symbol).Distinct(StringComparer.Ordinal).Count() != held.Count)
            throw new InvalidDataException("Paper journal and saved portfolio disagree.");
        foreach (var row in history.Open)
        {
            var p = held.SingleOrDefault(p => p.Symbol == row["instrument"]!.ToString());
            if (p is null || p.Side != row["side"]!.ToString() || ReconstructedPosition.Digits(p.Size) != row["size"]!.ToString()
                || ReconstructedPosition.Digits(p.Entry) != row["entry_price"]!.ToString()
                || ReconstructedPosition.Digits(p.Leverage) != row["leverage"]!.ToString()
                || p.OpenedAt != DateTimeOffset.Parse(row["opened_at"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture))
                throw new InvalidDataException("Paper journal and saved portfolio disagree.");
        }
    }

    private static JsonObject Position(ArenaPaperFill p) => new()
    {
        ["account"] = "demo", ["venue"] = "KRAKEN-FUTURES", ["instrument"] = p.Symbol, ["side"] = p.Side,
        ["opened_at"] = p.Time.ToUniversalTime().ToString("O"), ["entry_price"] = ReconstructedPosition.Digits(p.Price),
        ["size"] = ReconstructedPosition.Digits(p.Size), ["size_unit"] = "base", ["leverage"] = ReconstructedPosition.Digits(p.Leverage),
        ["margin"] = ReconstructedPosition.Digits(p.Margin!.Value), ["label"] = "BYKO paper",
        ["note"] = "Virtual execution, not a real exchange trade. Modeled fills and fees; funding is not modeled."
    };
    private static JsonObject Leg(ArenaPaperFill p) => new()
    {
        ["time"] = p.Time.ToUniversalTime().ToString("O"), ["side"] = (p.Side == "long") != p.Closing ? "buy" : "sell",
        ["price"] = ReconstructedPosition.Digits(p.Price), ["size"] = ReconstructedPosition.Digits(p.Size)
    };
    private static string? Reason(string? reason) => reason switch
    {
        "SELL_STOP_LOSS" or "SIMULATED_TRAILING_STOP" => "sl",
        "SELL_TAKE_PROFIT" => "tp",
        "SELL_SIGNAL_REVERSAL" => "signal",
        _ => null
    };
}
