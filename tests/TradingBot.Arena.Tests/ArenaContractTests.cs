using System.Text;
using System.Text.Json.Nodes;
using TradingBot.Arena;
using Xunit;

namespace TradingBot.Arena.Tests;

public sealed class ArenaContractTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-22T12:00:00Z");
    private static ArenaContract Contract() => new(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/arena-openapi.json")));
    private static JsonObject Profile() => new() { ["handle"] = "omenas", ["bio"] = "DEBYKO's own bot", ["declared_capital"] = "1000", ["capital_ccy"] = "USD", ["effective_from"] = "2026-10-11T00:00:00Z" };
    private static JsonObject Trade(string id = "kraken-PF_XBTUSD-fill-1") => new()
    {
        ["trade_id"] = id, ["account"] = "live", ["venue"] = "KRAKEN-FUTURES", ["instrument"] = "PF_XBTUSD", ["side"] = "long",
        ["opened_at"] = "2026-10-10T23:00:00Z", ["closed_at"] = "2026-10-11T01:00:00Z", ["entry_price"] = "100", ["exit_price"] = "110",
        ["leverage"] = "10", ["size"] = "1", ["size_unit"] = "base", ["fees"] = ".1", ["funding"] = "-.01", ["label"] = "BYKO",
        ["fills"] = new JsonArray(new JsonObject { ["fill_id"] = "fill-1", ["time"] = "2026-10-10T23:00:00Z", ["price"] = "100", ["size"] = "1", ["side"] = "buy" })
    };
    private static JsonObject Positions() => new() { ["account"] = "live", ["positions"] = new JsonArray() };
    private static readonly ArenaRegistryListing[] Registry = [new("PF_XBTUSD", null, null, 1)];
    [Fact]
    public void Whole_plan_matches_recorded_real_OpenAPI_and_native_registry()
    {
        Assert.Empty(Contract().Validate(Profile(), [Trade()], Positions(), Registry, Now));
    }
    [Fact]
    public void Invalid_tail_of_import_is_detected_before_any_network_write()
    {
        var bad = Trade("tail"); bad["size"] = "-1";
        var errors = Contract().Validate(Profile(), [Trade(), bad], Positions(), Registry, Now);
        Assert.Contains(errors, e => e.StartsWith("trades[1]", StringComparison.Ordinal));
    }
    [Fact]
    public void Rejects_duplicate_ID_unresolved_listing_invalid_time_and_costs()
    {
        var duplicate = Contract().Validate(Profile(), [Trade(), Trade()], Positions(), Registry, Now);
        Assert.Contains(duplicate, e => e.Contains("duplicate trade_id"));
        Assert.NotEmpty(Contract().Validate(Profile(), [Trade()], Positions(), [], Now));
        var trade = Trade(); trade["closed_at"] = "2026-10-11T01:00:00"; trade["fees"] = "-1";
        var errors = Contract().Validate(Profile(), [trade], Positions(), Registry, Now);
        Assert.Contains(errors, e => e.Contains("invalid_time")); Assert.Contains(errors, e => e.Contains("rebates"));
    }
    [Fact]
    public void Capital_declaration_cannot_leave_imported_closes_on_old_return_basis()
    {
        var profile = Profile(); profile["effective_from"] = "2026-10-22T00:00:00Z";
        Assert.Contains(Contract().Validate(profile, [Trade()], Positions(), Registry, Now), e => e.Contains("different percentage basis"));
    }
    [Fact]
    public void Owner_required_journal_leverage_and_complete_booked_costs_cannot_be_removed()
    {
        var trade = Trade(); trade.Remove("leverage"); trade.Remove("fees");
        var errors = Contract().Validate(Profile(), [trade], Positions(), Registry, Now);
        Assert.Contains(errors, e => e.Contains("historical journal leverage"));
        Assert.Contains(errors, e => e.Contains("fees: required"));
    }
    [Fact]
    public void No_external_schema_refs_are_resolved()
    {
        Assert.Throws<InvalidDataException>(() => new ArenaContract("{\"components\":{\"schemas\":{\"Unsafe\":{\"$ref\":\"https://127.0.0.1:1/not-called\"}}}}"));
    }
    [Fact]
    public void Batch_limits_hold_without_trimming_trades_or_fills()
    {
        var trades = Enumerable.Range(0, 201).Select(i => Trade(i.ToString())).ToArray();
        var batches = ArenaContract.Batches(trades);
        Assert.Equal(201, batches.Sum(b => JsonNode.Parse(b)!.AsArray().Count));
        Assert.All(batches, b => { Assert.True(b.Length <= 65536); Assert.InRange(JsonNode.Parse(b)!.AsArray().Count, 1, 100); });
        var huge = Trade(); huge["note"] = new string('x', 66000);
        Assert.Throws<InvalidDataException>(() => ArenaContract.Batches([huge]));
    }
    [Fact]
    public void Approval_digest_is_order_independent_for_object_keys_but_detects_any_payload_change()
    {
        var a = JsonNode.Parse("{\"x\":1,\"y\":[1,2]}")!; var b = JsonNode.Parse("{\"y\":[1,2],\"x\":1}")!;
        Assert.Equal(ArenaContract.Digest(a), ArenaContract.Digest(b)); b["x"] = 2;
        Assert.NotEqual(ArenaContract.Digest(a), ArenaContract.Digest(b));
    }
}
