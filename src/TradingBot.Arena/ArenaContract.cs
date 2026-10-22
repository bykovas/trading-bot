using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace TradingBot.Arena;

public sealed record ArenaRegistryListing(string Symbol, DateTimeOffset? ListedAt, DateTimeOffset? DelistedAt, decimal? Multiplier);

/// <summary>Offline preflight of the complete plan. Registry evidence and the actual OpenAPI are inputs, never network reads in a validator.</summary>
public sealed class ArenaContract
{
    private readonly JsonObject _definitions;
    public string SchemaHash { get; }
    public ArenaContract(string openApi)
    {
        var document = JsonNode.Parse(openApi)?.AsObject() ?? throw new InvalidDataException("Missing OpenAPI document.");
        _definitions = document["components"]?["schemas"]?.DeepClone().AsObject() ?? throw new InvalidDataException("Missing Arena schemas.");
        void LocalRefs(JsonNode? node)
        {
            if (node is JsonObject obj)
                foreach (var property in obj)
                {
                    if (property.Key == "$ref" && property.Value?.GetValue<string>() is { } reference && !reference.StartsWith("#/components/schemas/", StringComparison.Ordinal))
                        throw new InvalidDataException("Only local recorded schema references are permitted; validation never fetches a URL.");
                    LocalRefs(property.Value);
                }
            else if (node is JsonArray array) foreach (var value in array) LocalRefs(value);
        }
        LocalRefs(_definitions); SchemaHash = Digest(_definitions);
    }
    public IReadOnlyList<string> Validate(JsonObject profile, IReadOnlyList<JsonObject> trades, JsonObject positions,
        IReadOnlyList<ArenaRegistryListing> registry, DateTimeOffset now)
    {
        List<string> errors = [];
        void Schema(string name, JsonNode value, string path)
        {
            JsonObject schema = new() { ["$schema"] = "https://json-schema.org/draft/2020-12/schema", ["$defs"] = _definitions.DeepClone(), ["$ref"] = "#/$defs/" + name };
            var compiled = JsonSchema.FromText(schema.ToJsonString().Replace("#/components/schemas/", "#/$defs/", StringComparison.Ordinal));
            if (!compiled.Evaluate(JsonSerializer.SerializeToElement(value), new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true }).IsValid)
                errors.Add(path + ": does not validate against the recorded " + name);
        }
        Schema("ArenaProfileRequest", profile, "profile"); Schema("ArenaPositionsRequest", positions, "positions");
        if (profile["handle"]?.ToString() != "omenas" || profile["capital_ccy"]?.ToString() != "USD") errors.Add("profile: BYKO uses owner-approved omenas/USD");
        if (Decimal(profile, "declared_capital", errors, "profile", true) is not > 0) errors.Add("profile: capital must be confirmed before import");
        DateTimeOffset effective = Instant(profile, "effective_from", errors, "profile", now, allowFuture: true);
        if (trades.Count > 0 && trades.Select(t => DateTimeOffset.TryParse(t["closed_at"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var x) ? x : DateTimeOffset.MinValue).Min() < effective)
            errors.Add("profile: effective_from would leave imported closes on a different percentage basis");
        HashSet<string> ids = new(StringComparer.Ordinal);
        for (int i = 0; i < trades.Count; i++)
        {
            var row = trades[i]; string path = $"trades[{i}]"; Schema("ArenaTradeRequest", row, path);
            if (!ids.Add(row["trade_id"]?.ToString() ?? "")) errors.Add(path + ": duplicate trade_id");
            Row(row, path, false);
        }
        if (positions["account"]?.ToString() != "live") errors.Add("positions: account must be live");
        HashSet<string> sides = new(StringComparer.Ordinal);
        if (positions["positions"] is JsonArray array)
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is not JsonObject row) continue;
                if (!sides.Add(row["instrument"] + "/" + row["side"])) errors.Add($"positions[{i}]: duplicate instrument/side");
                Row(row, $"positions[{i}]", true);
            }
        return errors;

        void Row(JsonObject row, string path, bool open)
        {
            foreach (var property in row)
                if (property.Value is JsonValue value && value.TryGetValue<string>(out var text) && text.Contains('\0')) errors.Add(path + "." + property.Key + ": NUL is invalid");
            if (row["venue"]?.ToString() != "KRAKEN-FUTURES" || row["size_unit"]?.ToString() != "base") errors.Add(path + ": expected KRAKEN-FUTURES and base size");
            if (!open && (row["account"]?.ToString() != "live" || row["label"]?.ToString() != "BYKO")) errors.Add(path + ": expected live/BYKO");
            DateTimeOffset opened = Instant(row, "opened_at", errors, path, now);
            DateTimeOffset closed = open ? now : Instant(row, "closed_at", errors, path, now);
            if (closed < opened) errors.Add(path + ": invalid holding interval");
            string native = row["instrument"]?.ToString() ?? "";
            var listings = registry.Where(r => r.Symbol == native && (r.ListedAt is null || r.ListedAt <= opened) && (r.DelistedAt is null || opened < r.DelistedAt)).ToArray();
            if (listings.Length != 1 || listings[0].Multiplier is not 1) errors.Add(path + ": unknown/ambiguous/non-base registry listing");
            decimal? entry = Decimal(row, "entry_price", errors, path, true), size = Decimal(row, "size", errors, path, true);
            decimal? exit = open ? entry : Decimal(row, "exit_price", errors, path, true);
            foreach (var name in new[] { "leverage", "margin" }) if (row[name] is not null && Decimal(row, name, errors, path) is not > 0) errors.Add(path + "." + name + ": must be positive");
            if (entry <= 0 || exit <= 0 || size <= 0) errors.Add(path + ": prices and size must be positive");
            decimal? fee = Decimal(row, "fees", errors, path), funding = Decimal(row, "funding", errors, path);
            if (fee < 0) errors.Add(path + ".fees: rebates cannot be disguised as a positive cost");
            if (row["exit_reason"]?.ToString() is { } reason && !new[] { "tp", "sl", "signal", "manual", "liquidation" }.Contains(reason)) errors.Add(path + ".exit_reason: unsupported");
            try
            {
                if (entry > 0 && size > 0 && exit > 0)
                {
                    decimal notional = checked(entry.Value * size.Value), leverage = Decimal(row, "leverage", errors, path) ?? 1;
                    decimal denominator = Decimal(row, "margin", errors, path) ?? (leverage > 0 ? notional / leverage : 0);
                    if (denominator != 0) _ = checked(((exit.Value - entry.Value) * size.Value - (fee ?? 0) + (funding ?? 0)) / denominator * 100);
                }
            }
            catch (Exception ex) when (ex is OverflowException or DivideByZeroException) { errors.Add(path + ": numeric_range"); }
            if (row["fills"] is JsonArray fills)
                for (int i = 0; i < fills.Count; i++)
                {
                    if (fills[i] is not JsonObject f) continue;
                    string fp = path + $".fills[{i}]"; DateTimeOffset t = Instant(f, "time", errors, fp, now);
                    if (t < opened || t > closed) errors.Add(fp + ": fill outside holding interval");
                    if (Decimal(f, "price", errors, fp, true) is not > 0 || Decimal(f, "size", errors, fp, true) is not > 0) errors.Add(fp + ": invalid price/size");
                    if (f["fill_id"]?.ToString().Contains('\0') == true) errors.Add(fp + ": NUL is invalid");
                }
        }
    }
    private static decimal? Decimal(JsonObject row, string name, List<string> errors, string path, bool required = false)
    {
        if (row[name] is null) { if (required) errors.Add(path + "." + name + ": required"); return null; }
        if (decimal.TryParse(row[name]!.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return value;
        errors.Add(path + "." + name + ": invalid decimal"); return null;
    }
    private static DateTimeOffset Instant(JsonObject row, string name, List<string> errors, string path, DateTimeOffset now, bool allowFuture = false)
    {
        string text = row[name]?.ToString() ?? "";
        bool zone = text.EndsWith('Z') || System.Text.RegularExpressions.Regex.IsMatch(text, @"[+-]\d{2}:\d{2}$");
        if (zone && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) && value.Year >= 1970 && (allowFuture || value <= now)) return value;
        errors.Add(path + "." + name + ": invalid_time"); return DateTimeOffset.MinValue;
    }
    public static string Digest(JsonNode node)
    {
        JsonNode? Sort(JsonNode? n) => n switch
        {
            JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Sort(p.Value)))),
            JsonArray array => new JsonArray(array.Select(Sort).ToArray()), _ => n?.DeepClone()
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Sort(node)!.ToJsonString()))).ToLowerInvariant();
    }
    public static IReadOnlyList<byte[]> Batches(IReadOnlyList<JsonObject> trades)
    {
        List<byte[]> result = []; var batch = new JsonArray();
        foreach (var trade in trades)
        {
            batch.Add(trade.DeepClone()); byte[] body = JsonSerializer.SerializeToUtf8Bytes(batch);
            if (batch.Count > 100 || body.Length > 65536)
            {
                batch.RemoveAt(batch.Count - 1);
                if (batch.Count == 0) throw new InvalidDataException("A single trade exceeds 64 KiB; fills/window cannot be shortened.");
                result.Add(JsonSerializer.SerializeToUtf8Bytes(batch)); batch = new JsonArray(trade.DeepClone());
                if (JsonSerializer.SerializeToUtf8Bytes(batch).Length > 65536) throw new InvalidDataException("A single trade exceeds 64 KiB; fills/window cannot be shortened.");
            }
        }
        if (batch.Count > 0) result.Add(JsonSerializer.SerializeToUtf8Bytes(batch));
        return result;
    }
}
