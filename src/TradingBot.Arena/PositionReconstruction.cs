using System.Globalization;
using System.Text.Json.Nodes;

namespace TradingBot.Arena;

// These are venue facts. The raw adapter must prove old/new signed position state and currency before constructing them.
public sealed record VenueFill(string Id, string OrderId, string PositionKey, string Symbol, DateTimeOffset Time,
    string Side, decimal Quantity, decimal Price, decimal OldPosition, decimal NewPosition, decimal Fees, decimal RealizedFunding,
    long Sequence, decimal? Leverage = null);
public sealed record VenueFunding(string Id, string PositionKey, string Symbol, DateTimeOffset Time, decimal Amount);
public sealed record JournalAnnotation(string OrderId, string? FiringId = null, string? Rule = null, string? ExitReason = null);
public sealed record PositionLeg(string Id, string OrderId, DateTimeOffset Time, string Side, decimal Quantity, decimal Price);
public sealed record ReconstructedPosition(string TradeId, string PositionKey, string Symbol, string Side, DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt, decimal Size, decimal Entry, decimal? Exit, decimal Fees, decimal Funding, decimal? Leverage,
    IReadOnlyList<PositionLeg> Fills, string? FiringId, string? Rule, string? ExitReason)
{
    public JsonObject Payload()
    {
        JsonObject row = new() { ["account"] = "live", ["venue"] = "KRAKEN-FUTURES", ["instrument"] = Symbol, ["side"] = Side,
            ["opened_at"] = OpenedAt.ToUniversalTime().ToString("O"), ["entry_price"] = Digits(Entry), ["size"] = Digits(Size), ["size_unit"] = "base", ["label"] = "BYKO" };
        if (Leverage is { } leverage) row["leverage"] = Digits(leverage);
        if (ClosedAt is { } closed)
        {
            row["trade_id"] = TradeId; row["closed_at"] = closed.ToUniversalTime().ToString("O"); row["exit_price"] = Digits(Exit!.Value);
            row["fees"] = Digits(Fees); row["funding"] = Digits(Funding);
            if (FiringId is not null) row["debyko_firing_id"] = FiringId;
            if (Rule is not null) row["rule"] = Rule;
            if (ExitReason is not null) row["exit_reason"] = ExitReason;
            row["fills"] = new JsonArray(Fills.Select(f => (JsonNode)new JsonObject { ["fill_id"] = f.Id, ["time"] = f.Time.ToUniversalTime().ToString("O"),
                ["side"] = f.Side, ["price"] = Digits(f.Price), ["size"] = Digits(f.Quantity) }).ToArray());
        }
        return row;
    }
    public static string Digits(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);
}
public sealed record ReconstructedHistory(IReadOnlyList<ReconstructedPosition> Closed, IReadOnlyList<ReconstructedPosition> Open);

public sealed record ExcludedPosition(ReconstructedPosition Position, string Reason);
public sealed record BotSelection(ReconstructedHistory Included, IReadOnlyList<ExcludedPosition> Excluded);

public static class PositionReconstruction
{
    public static BotSelection BotOwnedOnly(ReconstructedHistory history, IReadOnlySet<string> provenBotOrderIds)
    {
        var excluded = new List<ExcludedPosition>();
        bool Include(ReconstructedPosition position)
        {
            bool any = position.Fills.Any(f => provenBotOrderIds.Contains(f.OrderId));
            bool all = position.Fills.Count > 0 && position.Fills.All(f => provenBotOrderIds.Contains(f.OrderId));
            if (!all) excluded.Add(new(position, any ? "mixed_or_unproven_order_ownership" : "manual_or_unproven_order_ownership"));
            return all;
        }
        return new(new(history.Closed.Where(Include).ToArray(), history.Open.Where(Include).ToArray()), excluded);
    }
    private sealed class Cycle(VenueFill first, string side)
    {
        public readonly VenueFill First = first;
        public readonly string Side = side;
        public decimal Balance, OpenedQuantity, EntryCost, ExitedQuantity, ExitCost, RemainingCost, Fees, Funding;
        public decimal? Leverage = first.Leverage;
        public readonly List<PositionLeg> Legs = [];
        public DateTimeOffset? ClosedAt;
        public ReconstructedPosition Finish(Dictionary<string, JournalAnnotation> annotations)
        {
            if (Fees < 0) throw new InvalidDataException("Net fee rebates cannot be mislabelled as nonnegative fees; the contract needs an explicit decision.");
            var first = annotations.GetValueOrDefault(First.OrderId); var last = annotations.GetValueOrDefault(Legs[^1].OrderId);
            return new("kraken-" + First.Symbol + "-" + First.Id, First.PositionKey, First.Symbol, Side, First.Time, ClosedAt,
                ClosedAt is null ? Math.Abs(Balance) : OpenedQuantity,
                ClosedAt is null ? RemainingCost / Math.Abs(Balance) : EntryCost / OpenedQuantity,
                ClosedAt is null ? null : ExitCost / ExitedQuantity, Fees, Funding, Leverage, Legs.ToArray(), first?.FiringId, first?.Rule,
                ClosedAt is null ? null : last?.ExitReason);
        }
    }
    public static ReconstructedHistory Build(IReadOnlyList<VenueFill> fills, IReadOnlyList<VenueFunding> funding,
        IReadOnlyList<JournalAnnotation> journal, DateTimeOffset closedSince, DateTimeOffset cutoff)
    {
        var annotations = journal.GroupBy(j => j.OrderId, StringComparer.Ordinal).ToDictionary(g => g.Key,
            g => g.Distinct().Count() == 1 ? g.First() : throw new InvalidDataException("Ambiguous journal annotation for a venue order."), StringComparer.Ordinal);
        var active = new Dictionary<(string Symbol, string Position), Cycle>(); List<Cycle> completed = [];
        HashSet<string> seen = new(StringComparer.Ordinal); DateTimeOffset previous = DateTimeOffset.MinValue;
        foreach (var fill in fills.OrderBy(f => f.Time).ThenBy(f => f.Sequence))
        {
            if (!seen.Add(fill.Id) || string.IsNullOrWhiteSpace(fill.Id) || string.IsNullOrWhiteSpace(fill.PositionKey) || string.IsNullOrWhiteSpace(fill.OrderId))
                throw new InvalidDataException("A fill identity, position scope or order is missing/duplicated.");
            if (!fill.Symbol.StartsWith("PF_", StringComparison.Ordinal) || !fill.Symbol.EndsWith("USD", StringComparison.Ordinal))
                throw new InvalidDataException("Only registry-confirmed linear PF_ USD base-size contracts are supported; inverse contracts are not approximated.");
            if (fill.Time > cutoff || fill.Time.Year < 1970 || fill.Time < previous || fill.Quantity <= 0 || fill.Price <= 0 || fill.Side is not ("buy" or "sell") || fill.Leverage <= 0)
                throw new InvalidDataException("Invalid venue fill.");
            previous = fill.Time;
            decimal delta = fill.Quantity * (fill.Side == "buy" ? 1 : -1);
            if (checked(fill.NewPosition - fill.OldPosition) != delta) throw new InvalidDataException("Fill does not reconcile to venue position change.");
            var key = (fill.Symbol, fill.PositionKey);
            active.TryGetValue(key, out Cycle? state);
            if ((state?.Balance ?? 0) != fill.OldPosition) throw new InvalidDataException("Missing opening/history or ambiguous hedge scope; extend the source history, never start a position in the middle.");
            if (state is null)
            {
                state = new Cycle(fill, delta > 0 ? "long" : "short"); active.Add(key, state);
            }
            // One execution may close an old side and open the opposite side. Preserve its native ID on both allocated legs.
            bool reducing = state.Balance != 0 && Math.Sign(delta) != Math.Sign(state.Balance);
            decimal closeQuantity = reducing ? Math.Min(Math.Abs(state.Balance), fill.Quantity) : 0;
            decimal openQuantity = fill.Quantity - closeQuantity;
            decimal closeFee = openQuantity == 0 ? fill.Fees : fill.Fees * closeQuantity / fill.Quantity;
            if (closeQuantity > 0)
            {
                decimal oldSize = Math.Abs(state.Balance);
                state.RemainingCost -= state.RemainingCost / oldSize * closeQuantity;
                state.ExitedQuantity += closeQuantity; state.ExitCost += closeQuantity * fill.Price; state.Fees += closeFee;
                state.Funding += fill.RealizedFunding;
                state.Balance += closeQuantity * Math.Sign(delta);
                state.Legs.Add(new(fill.Id, fill.OrderId, fill.Time, fill.Side, closeQuantity, fill.Price));
                if (state.Balance == 0)
                {
                    if (state.ExitedQuantity != state.OpenedQuantity) throw new InvalidDataException("Position volume failed open-to-flat reconciliation.");
                    state.ClosedAt = fill.Time; completed.Add(state); active.Remove(key);
                    state = null;
                }
            }
            if (openQuantity > 0)
            {
                if (state is null) { state = new Cycle(fill, delta > 0 ? "long" : "short"); active.Add(key, state); }
                state.OpenedQuantity += openQuantity; state.EntryCost += openQuantity * fill.Price; state.RemainingCost += openQuantity * fill.Price;
                state.Fees += fill.Fees - closeFee;
                // Realized funding on a reversal belongs to the old side, not the newly opened side.
                if (closeQuantity == 0) state.Funding += fill.RealizedFunding;
                state.Balance += openQuantity * Math.Sign(delta);
                state.Legs.Add(new(fill.Id, fill.OrderId, fill.Time, fill.Side, openQuantity, fill.Price));
                if (fill.Leverage != state.Leverage) state.Leverage = null; // Never substitute today's settings for historical leverage.
            }
            if ((state?.Balance ?? 0) != fill.NewPosition) throw new InvalidDataException("Reconstructed position differs from venue state.");
        }
        var all = completed.Concat(active.Values).ToArray(); seen.Clear();
        foreach (var item in funding)
        {
            if (!seen.Add(item.Id) || item.Time > cutoff) throw new InvalidDataException("Invalid/duplicated funding evidence.");
            if (item.Amount == 0) continue;
            Cycle[] matches = all.Where(c => c.First.Symbol == item.Symbol && c.First.PositionKey == item.PositionKey && c.First.Time <= item.Time
                && (c.ClosedAt is null || item.Time <= c.ClosedAt)).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Funding cannot be assigned unambiguously to one complete holding period.");
            matches[0].Funding += item.Amount;
        }
        var closed = completed.Where(c => c.ClosedAt >= closedSince).Select(c => c.Finish(annotations)).OrderBy(c => c.ClosedAt).ThenBy(c => c.TradeId, StringComparer.Ordinal).ToArray();
        if (closed.Select(c => c.TradeId).Distinct(StringComparer.Ordinal).Count() != closed.Length) throw new InvalidDataException("Trade ID collision.");
        return new(closed, active.Values.Select(c => c.Finish(annotations)).OrderBy(c => c.Symbol, StringComparer.Ordinal).ThenBy(c => c.Side, StringComparer.Ordinal).ToArray());
    }
}
