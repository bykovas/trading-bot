using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;

namespace TradingBot.Arena;

/// <summary>Venue executions/position updates prove state; the USD account log alone supplies booked costs.</summary>
public static class KrakenEvidence
{
    public static (IReadOnlyList<VenueFill> Fills, IReadOnlyList<VenueFunding> Funding) Normalize(HistoryCapture capture, DateTimeOffset? closedSince = null)
    {
        if (!capture.Complete || capture.PositionUpdates is null) throw new InvalidDataException("Complete execution, position and account-log evidence is required.");
        var updates = capture.PositionUpdates.Select(r => r["event"]?["PositionUpdate"] as JsonObject ?? throw new InvalidDataException("Missing native position update.")).ToArray();
        var trades = updates.Where(u => u["updateReason"]?.ToString() == "trade").ToLookup(u => Required(u, "executionUid"), StringComparer.Ordinal);
        var costs = capture.Ledger.Where(l => l["execution"] is not null).ToLookup(l => l["execution"]!.ToString(), StringComparer.Ordinal);
        var fills = new List<VenueFill>(); long sequence = 0;
        foreach (var row in capture.Executions)
        {
            var execution = row["event"]?["execution"]?["execution"] as JsonObject ?? throw new InvalidDataException("Missing native execution.");
            var order = execution["order"] as JsonObject ?? throw new InvalidDataException("Missing native order.");
            string symbol = Required(order, "tradeable").ToUpperInvariant();
            if (!Linear(symbol)) continue; // Unsupported products are never converted to a made-up base size.
            string id = Required(execution, "uid");
            var matching = trades[id].Where(u => Required(u, "tradeable").Equals(symbol, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matching.Length != 1) throw new InvalidDataException($"Execution {id}: expected one native position update, got {matching.Length}.");
            var update = matching[0]; DateTimeOffset time = Milliseconds(execution, "timestamp");
            // fillTime can be the original opening time; event timestamp can lag. Join by exact executionUid, never time proximity.
            if (Milliseconds(update, "timestamp") < time || Milliseconds(update, "timestamp") > capture.CapturedAt
                || Number(update, "executionSize") != Number(execution, "quantity") || Number(update, "executionPrice") != Number(execution, "price"))
                throw new InvalidDataException("Execution and native position update disagree.");
            fills.Add(new(id, Required(order, "uid"), symbol, symbol, time, Required(order, "direction").ToLowerInvariant(),
                Number(execution, "quantity"), Number(execution, "price"), Number(update, "oldPosition"), Number(update, "newPosition"), 0, 0, sequence++));
        }
        // Reuse the same cycle engine to find complete relevant holding periods, including earlier opens.
        // Costs of a cycle closed before the import window cannot affect any imported trade.
        var periods = PositionReconstruction.Build(fills, [], [], closedSince ?? DateTimeOffset.UnixEpoch, capture.CapturedAt);
        var relevant = periods.Closed.Concat(periods.Open).ToArray();
        var needed = relevant.SelectMany(p => p.Fills).Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        for (int i = 0; i < fills.Count; i++)
        {
            var fill = fills[i];
            if (!needed.Contains(fill.Id)) continue;
            var ledger = costs[fill.Id].Where(l => l["contract"]?.ToString().Equals(fill.Symbol, StringComparison.OrdinalIgnoreCase) == true
                && l["asset"]?.ToString().Equals("usd", StringComparison.OrdinalIgnoreCase) == true).ToArray();
            if (ledger.Length == 0 || ledger.Length > 1 && (ledger.Any(l => l["id"] is null)
                || ledger.Select(l => l["id"]!.ToString()).Distinct(StringComparer.Ordinal).Count() != ledger.Length))
                throw new InvalidDataException($"Execution {fill.Id}: booked USD costs are missing or ambiguous; import is blocked.");
            // Separate immutable USD account-log IDs are distinct bookings (including a later fee correction), not duplicate position-ledger rows.
            foreach (var cost in ledger) RequireUsd(cost);
            fills[i] = fill with { Fees = ledger.Sum(cost => Number(cost, "fee") + OptionalNumber(cost, "liquidation_fee") + OptionalNumber(cost, "conversion_fee")),
                RealizedFunding = ledger.Sum(cost => OptionalNumber(cost, "realized_funding")) };
        }
        var funding = new List<VenueFunding>();
        foreach (var row in capture.Ledger.Where(l => l["execution"] is null && l["contract"] is not null))
        {
            string symbol = row["contract"]!.ToString().ToUpperInvariant();
            if (!Linear(symbol) || OptionalNumber(row, "realized_funding") == 0) continue;
            RequireUsd(row);
            if (!DateTimeOffset.TryParse(Required(row, "date"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) throw new InvalidDataException("Invalid ledger time.");
            if (!relevant.Any(p => p.Symbol == symbol && p.OpenedAt <= time && (p.ClosedAt is null || time <= p.ClosedAt))) continue;
            // Booked amount/date, not an unrounded indicative accrual or the bot's funding estimate.
            funding.Add(new("ledger-" + Required(row, "id"), symbol, symbol, time, Number(row, "realized_funding")));
        }
        return (fills, funding);
    }
    private static bool Linear(string symbol) => symbol.StartsWith("PF_", StringComparison.Ordinal) && symbol.EndsWith("USD", StringComparison.Ordinal);
    private static void RequireUsd(JsonObject row)
    {
        if (!Required(row, "asset").Equals("usd", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Non-USD booked cost cannot be relabelled as USD.");
    }
    public static string Required(JsonObject row, string name) => row[name]?.ToString() is { Length: > 0 } value ? value : throw new InvalidDataException("Missing venue field: " + name);
    public static decimal Number(JsonObject row, string name) => ExactDecimal(Required(row, name));
    private static decimal OptionalNumber(JsonObject row, string name) => row[name] is null ? 0 : Number(row, name);
    public static decimal ExactDecimal(string text)
    {
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) throw new InvalidDataException("Venue decimal is outside the exact supported range.");
        // Decimal.TryParse can silently round long inputs. Compare normalized arbitrary-precision coefficients first.
        static (BigInteger Coefficient, int Scale) Parts(string s)
        {
            s = s.ToLowerInvariant(); string[] exponent = s.Split('e');
            if (exponent.Length > 2) throw new InvalidDataException("Invalid decimal exponent.");
            int e = exponent.Length == 2 ? int.Parse(exponent[1], CultureInfo.InvariantCulture) : 0;
            string m = exponent[0]; int dot = m.IndexOf('.'); int scale = (dot < 0 ? 0 : m.Length - dot - 1) - e;
            var coefficient = BigInteger.Parse(m.Replace(".", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
            if (scale < -1000 || scale > 1000) throw new InvalidDataException("Decimal exponent is outside the safety bound.");
            while (coefficient != 0 && coefficient % 10 == 0) { coefficient /= 10; scale--; }
            return coefficient == 0 ? (BigInteger.Zero, 0) : (coefficient, scale);
        }
        if (Parts(text) != Parts(value.ToString("G29", CultureInfo.InvariantCulture))) throw new InvalidDataException("Venue decimal would lose precision.");
        return value;
    }
    private static DateTimeOffset Milliseconds(JsonObject row, string name) => DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(Required(row, name), CultureInfo.InvariantCulture));
}
