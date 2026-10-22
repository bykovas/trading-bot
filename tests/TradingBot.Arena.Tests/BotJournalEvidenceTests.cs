using System.Text.Json.Nodes;
using TradingBot.Arena;
using Xunit;

namespace TradingBot.Arena.Tests;
public sealed class BotJournalEvidenceTests
{
    private const string Id = "11111111-1111-1111-1111-111111111111";
    private static JsonObject Journal(string reason, string source = "EXCHANGE_ORDER_ACCEPTED_MODELED", bool knownFiring = true) => new()
    {
        ["actions"] = new JsonArray(new JsonObject { ["pair"] = "XBT/USD", ["reason"] = reason, ["fills"] = new JsonArray(new JsonObject { ["order_id"] = Id, ["source"] = source }) }),
        ["signals"] = knownFiring ? new JsonArray(new JsonObject { ["firing_id"] = "signal1" }) : new JsonArray()
    };
    [Fact]
    public void Accepted_bot_order_proves_ownership_but_modeled_prices_are_not_read()
    {
        var proof = Assert.Single(BotJournalEvidence.Read(Journal($"live Kraken Futures FOK accepted id={Id} status=placed; DEBYKO BYKO_BREAKOUT_LONG firing signal1"), ""));
        Assert.Equal(Id, proof.OrderId); Assert.Equal("signal1", proof.Annotation.FiringId);
    }
    [Fact]
    public void Exchange_backfilled_or_manual_close_is_not_order_ownership_evidence()
    {
        Assert.Empty(BotJournalEvidence.Read(Journal($"EXCHANGE_CLOSE_BACKFILLED: fill {Id}", "EXCHANGE_FILLS"), ""));
        Assert.Empty(BotJournalEvidence.Read(Journal($"EXCHANGE_CLOSE: position gone from exchange, order {Id}", "EXCHANGE_FILLS"), ""));
    }
    [Fact]
    public void Exact_protection_placement_is_ownership_proof_but_failed_attempt_is_not()
    {
        string line = $"2026-10-20T00:00:00Z futures-trailing-arm: symbol=PF_XBTUSD side=LONG orderId={Id} distancePct=1";
        Assert.Equal(Id, Assert.Single(BotJournalEvidence.Read(new(), line)).OrderId);
        Assert.Empty(BotJournalEvidence.Read(new(), line + " FAILED"));
    }
    [Fact]
    public void Unconfirmed_firing_is_not_invented_as_a_verified_signal()
    {
        var proof = Assert.Single(BotJournalEvidence.Read(Journal($"live Kraken Futures FOK accepted id={Id} status=placed; DEBYKO BYKO_BREAKOUT_LONG firing signal1", knownFiring: false), ""));
        Assert.Null(proof.Annotation.FiringId);
    }
    [Fact]
    public void Expired_log_can_be_corroborated_by_exact_trailing_journal_and_native_bot_client_ID()
    {
        var journal = Journal("EXCHANGE_TRAILING_STOP: closed by exchange", "EXCHANGE_FILLS");
        var action = journal["actions"]![0]!.AsObject(); action["exchange_order_id"] = Id; action["exit_reason_code"] = "EXCHANGE_TRAILING_STOP";
        var execution = new JsonObject { ["event"] = new JsonObject { ["execution"] = new JsonObject { ["execution"] = new JsonObject
            { ["order"] = new JsonObject { ["uid"] = Id, ["clientId"] = "tb-0123456789abcdef0123456789abcdef" } } } } };
        Assert.Single(BotJournalEvidence.Read(journal, "", [execution]));
        action["exit_reason_code"] = "EXCHANGE_CLOSE";
        Assert.Empty(BotJournalEvidence.Read(journal, "", [execution]));
    }
    [Fact]
    public void Historical_journal_leverage_is_used_not_a_current_config_guess()
    {
        var journal = Journal($"live Kraken Futures FOK accepted id={Id} status=placed");
        journal["actions"]![0]!["leverage"] = 10;
        Assert.Equal(10, Assert.Single(BotJournalEvidence.Read(journal, "")).Annotation.Leverage);
    }
}
