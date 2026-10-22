using System.Text.Json.Nodes;
using TradingBot.Arena;
using Xunit;
namespace TradingBot.Arena.Tests;
public sealed class KrakenEvidenceTests
{
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-11T00:00:00Z");
    private static HistoryCapture Capture(string asset = "usd")
    {
        var execution = JsonNode.Parse("{\"event\":{\"execution\":{\"execution\":{\"uid\":\"fill-a\",\"timestamp\":1791676800000,\"quantity\":\"2\",\"price\":\"100\",\"order\":{\"uid\":\"order-a\",\"tradeable\":\"PF_XBTUSD\",\"direction\":\"Buy\"}}}}}")!.AsObject();
        var update = JsonNode.Parse("{\"event\":{\"PositionUpdate\":{\"executionUid\":\"fill-a\",\"tradeable\":\"PF_XBTUSD\",\"updateReason\":\"trade\",\"fillTime\":1791673200000,\"timestamp\":1791676800000,\"executionSize\":\"2\",\"executionPrice\":\"100\",\"oldPosition\":\"0\",\"newPosition\":\"2\",\"fee\":\"999\",\"realizedFunding\":\"999\"}}}")!.AsObject();
        JsonObject ledger = new() { ["execution"] = "fill-a", ["contract"] = "pf_xbtusd", ["asset"] = asset, ["fee"] = ".12", ["realized_funding"] = "-.03" };
        return new("account", Time.AddDays(1), [execution], [ledger], new(), Time.AddDays(1), PositionUpdates: [update]);
    }
    [Fact]
    public void Costs_come_from_booked_ledger_not_indicative_position_event_or_bot()
    {
        var normalized = KrakenEvidence.Normalize(Capture());
        var fill = Assert.Single(normalized.Fills); Assert.Equal(.12m, fill.Fees); Assert.Equal(-.03m, fill.RealizedFunding);
        Assert.Equal("order-a", fill.OrderId); Assert.Equal(0, fill.OldPosition); Assert.Equal(2, fill.NewPosition);
    }
    [Fact]
    public void Missing_or_conflicting_evidence_and_foreign_currency_fail_instead_of_zero_costs()
    {
        var capture = Capture(); Assert.Throws<InvalidDataException>(() => KrakenEvidence.Normalize(capture with { Ledger = [] }));
        Assert.Throws<InvalidDataException>(() => KrakenEvidence.Normalize(capture with { PositionUpdates = [] }));
        Assert.Throws<InvalidDataException>(() => KrakenEvidence.Normalize(Capture("btc")));
    }
    [Theory]
    [InlineData("0.00000000000000000000000000001")]
    [InlineData("12345678901234567890123456789.123456789")]
    public void Never_silently_rounds_raw_venue_decimals(string value) => Assert.Throws<InvalidDataException>(() => KrakenEvidence.ExactDecimal(value));
    [Theory]
    [InlineData("1.23000", "1.23")]
    [InlineData("-2.1e-5", "-0.000021")]
    public void Accepts_exact_decimal_representations(string value, string expected) => Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), KrakenEvidence.ExactDecimal(value));
    [Fact]
    public void Position_ledger_row_is_not_added_to_the_cash_cost_and_event_timestamp_may_lag()
    {
        var capture = Capture();
        var positionRow = capture.Ledger[0].DeepClone().AsObject(); positionRow["asset"] = "pf_xbtusd"; positionRow["fee"] = null;
        var update = capture.PositionUpdates![0]["event"]!["PositionUpdate"]!.AsObject(); update["timestamp"] = 1791676800001;
        var result = KrakenEvidence.Normalize(capture with { Ledger = [capture.Ledger[0], positionRow] });
        Assert.Equal(.12m, Assert.Single(result.Fills).Fees);
    }
    [Fact]
    public void Signed_funding_comes_from_its_booked_cash_row_inside_the_holding_period()
    {
        var capture = Capture();
        JsonObject funding = new() { ["id"] = 100, ["asset"] = "usd", ["contract"] = "pf_xbtusd", ["date"] = "2026-10-11T01:00:00Z", ["realized_funding"] = "-.0002" };
        var result = KrakenEvidence.Normalize(capture with { Ledger = [capture.Ledger[0], funding] });
        Assert.Equal(-.0002m, Assert.Single(result.Funding).Amount);
    }
}
