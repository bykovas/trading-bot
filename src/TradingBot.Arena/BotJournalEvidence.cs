using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TradingBot.Arena;

public sealed record BotOrderEvidence(string OrderId, string PairOrSymbol, string Proof, JournalAnnotation Annotation);
public static partial class BotJournalEvidence
{
    [GeneratedRegex(@"live Kraken Futures (?:FOK |post-only )?(?:order )?accepted id=(?<id>[0-9a-fA-F-]{36})(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex AcceptedOrder();
    [GeneratedRegex(@"DEBYKO (?<rule>[A-Z0-9_]+) firing (?<id>[a-zA-Z0-9_-]+)(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex Signal();
    [GeneratedRegex(@"futures-(?:trailing-arm|tpsl-arm|trailing-rollback): (?:restored stop_loss )?symbol=(?<symbol>[A-Z0-9_]+) .*?orderId=(?<id>[0-9a-fA-F-]{36})(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ProtectiveOrder();
    public static IReadOnlyList<BotOrderEvidence> Read(JsonObject journal, string protectedOrderLog)
    {
        var verifiedFirings = (journal["signals"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(s => s["firing_id"] is not null).Select(s => s["firing_id"]!.ToString()).ToHashSet(StringComparer.Ordinal);
        var result = new List<BotOrderEvidence>();
        foreach (var action in (journal["actions"] as JsonArray ?? []).OfType<JsonObject>())
        {
            string reason = action["reason"]?.ToString() ?? "";
            var accepted = AcceptedOrder().Match(reason);
            if (!accepted.Success) continue; // EXCHANGE_CLOSE(_BACKFILLED) does not prove who placed the order.
            string id = accepted.Groups["id"].Value;
            var recorded = (action["fills"] as JsonArray ?? []).OfType<JsonObject>().Where(f => f["order_id"]?.ToString() == id
                && f["source"]?.ToString() is "EXCHANGE_ORDER_RESULT" or "EXCHANGE_ORDER_ACCEPTED_MODELED").Any();
            if (!recorded && action["exchange_order_id"]?.ToString() != id) continue;
            var signal = Signal().Match(reason); string? firing = signal.Success && verifiedFirings.Contains(signal.Groups["id"].Value) ? signal.Groups["id"].Value : null;
            string? exit = action["exit_reason_code"]?.ToString() switch
            {
                "SELL_STOP_LOSS" or "BUY_STOP_LOSS" => "sl",
                "SELL_TAKE_PROFIT" or "BUY_TAKE_PROFIT" => "tp",
                _ => reason.Contains("signal reversal close", StringComparison.Ordinal) ? "signal" : null
            };
            result.Add(new(id, action["pair"]?.ToString() ?? throw new InvalidDataException("Order proof has no pair."), "accepted_order_journal",
                new(id, firing, signal.Success ? signal.Groups["rule"].Value : null, exit)));
        }
        foreach (string line in protectedOrderLog.Split('\n'))
        {
            var match = ProtectiveOrder().Match(line);
            if (!match.Success || line.Contains("FAILED", StringComparison.Ordinal)) continue;
            string id = match.Groups["id"].Value;
            result.Add(new(id, match.Groups["symbol"].Value, "bot_placed_protection_log", new(id, ExitReason: line.Contains("take_profit", StringComparison.Ordinal) ? "tp" : "sl")));
        }
        return result.GroupBy(e => e.OrderId, StringComparer.Ordinal).Select(group =>
        {
            var first = group.First();
            string? One(Func<BotOrderEvidence, string?> value)
            {
                var values = group.Select(value).Where(v => v is not null).Distinct(StringComparer.Ordinal).ToArray();
                if (values.Length > 1) throw new InvalidDataException("Conflicting journal evidence for one order.");
                return values.SingleOrDefault();
            }
            return first with { Annotation = new(first.OrderId, One(e => e.Annotation.FiringId), One(e => e.Annotation.Rule), One(e => e.Annotation.ExitReason)) };
        }).ToArray();
    }
}
