using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class DebykoEvaluationMetadataTests
{
    private static readonly Guid Id = Guid.Parse("3214ba4c-613a-4b0a-aeb4-715ec84cbb88");
    private static readonly DateTimeOffset End = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Version = new('A', 64);

    [Theory]
    [InlineData("BYKO_BREAKOUT_LONG", "bd6a4033-ce4f-4acb-97b7-a384c5368f2e", "3eee341e9979ee76240a89d6e1a030bef39b328513d671454d4b9099badb6ad0")]
    [InlineData("BYKO_BREAKOUT_SHORT", "2bb871a8-6c38-4c32-aa88-2fd5b7873527", "ef82714c71096f626749578ea5bf90962183c4f0969286e7088521eb9c9ec473")]
    public void Reviewed_breakout_pin_accepts_live_slice_but_rejects_version_drift(string rule, string id, string version)
    {
        var payload = Payload();
        payload["data"]!["watch"]!["id"] = id;
        payload["data"]!["watch"]!["version"] = version;
        var metadata = Assert.IsType<DebykoEvaluationMetadata>(DebykoEvaluationMetadata.Read(payload.ToJsonString()));
        var config = new BykoV2Options();
        config.DebykoRuleIds[rule] = Guid.Parse(id);
        config.DebykoRuleVersions[rule] = version;
        Assert.Null(DebykoEvaluationMetadata.RejectionReason(metadata, config, rule));
        config.DebykoRuleVersions[rule] = Version;
        var reason = DebykoEvaluationMetadata.RejectionReason(metadata, config, rule);
        Assert.Contains($"expected={Version} actual={version}", reason);
        config.DebykoRuleVersions.Clear();
        Assert.Contains("version pin missing", DebykoEvaluationMetadata.RejectionReason(metadata, config, rule));
        config.DebykoRuleIds[rule] = Guid.Empty;
        Assert.Contains("rule ID mismatch", DebykoEvaluationMetadata.RejectionReason(metadata, config, rule));
        Assert.Contains("missing or invalid", DebykoEvaluationMetadata.RejectionReason(null, config, rule));
        Assert.Contains("mode mismatch", DebykoEvaluationMetadata.RejectionReason(metadata with { Mode = "settled" }, config, rule));
    }

    [Theory]
    [InlineData("live")]
    [InlineData("settled")]
    public void Reads_valid_metadata_and_preserves_mode(string mode)
    {
        var payload = Payload();
        payload["data"]!["evaluation"]!["mode"] = mode;
        var metadata = Assert.IsType<DebykoEvaluationMetadata>(DebykoEvaluationMetadata.Read(payload.ToJsonString()));
        Assert.Equal(Id, metadata.RuleId);
        Assert.Equal(Version, metadata.Version);
        Assert.Equal(End, metadata.BarEnd);
        Assert.Equal(End.AddSeconds(15), metadata.DataAsOf);
        Assert.Equal(0, metadata.Attempt);
        Assert.Equal(mode, metadata.Mode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("LIVE")]
    [InlineData("replay")]
    [InlineData("not_live")]
    [InlineData(null)]
    public void Rejects_invalid_mode(string? mode)
    {
        var payload = Payload();
        payload["data"]!["evaluation"]!["mode"] = mode;
        Assert.Null(DebykoEvaluationMetadata.Read(payload.ToJsonString()));
    }

    [Theory]
    [InlineData("watch", "id")]
    [InlineData("watch", "version")]
    [InlineData("evaluation", "bar_end")]
    [InlineData("evaluation", "data_as_of")]
    [InlineData("evaluation", "attempt")]
    [InlineData("evaluation", "mode")]
    public void Rejects_missing_fields(string section, string field)
    {
        var payload = Payload();
        payload["data"]![section]!.AsObject().Remove(field);
        Assert.Null(DebykoEvaluationMetadata.Read(payload.ToJsonString()));
    }

    [Theory]
    [InlineData("watch", "id", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("watch", "id", "\"bad-guid\"")]
    [InlineData("watch", "version", "null")]
    [InlineData("watch", "version", "123")]
    [InlineData("watch", "version", "\"abcd\"")]
    [InlineData("watch", "version", "\"gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg\"")]
    [InlineData("evaluation", "bar_end", "\"not-a-time\"")]
    [InlineData("evaluation", "data_as_of", "null")]
    [InlineData("evaluation", "data_as_of", "\"2026-10-10T12:00:14.999Z\"")]
    [InlineData("evaluation", "attempt", "-1")]
    [InlineData("evaluation", "attempt", "1.5")]
    [InlineData("evaluation", "attempt", "\"1\"")]
    [InlineData("evaluation", "attempt", "2147483648")]
    [InlineData("evaluation", "mode", "123")]
    public void Rejects_invalid_values_and_types(string section, string field, string json)
    {
        var payload = Payload();
        payload["data"]![section]![field] = JsonNode.Parse(json);
        Assert.Null(DebykoEvaluationMetadata.Read(payload.ToJsonString()));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":{\"watch\":{},\"evaluation\":{}}}")]
    public void Rejects_malformed_or_incomplete_payload(string payload) => Assert.Null(DebykoEvaluationMetadata.Read(payload));

    [Fact]
    public void Accepts_equivalent_offset_timestamps_and_later_attempt()
    {
        var payload = Payload();
        payload["data"]!["evaluation"]!["bar_end"] = "2026-10-10T15:00:00+03:00";
        payload["data"]!["evaluation"]!["data_as_of"] = "2026-10-10T15:00:25+03:00";
        payload["data"]!["evaluation"]!["attempt"] = 2;
        var metadata = Assert.IsType<DebykoEvaluationMetadata>(DebykoEvaluationMetadata.Read(payload.ToJsonString()));
        Assert.Equal(End, metadata.BarEnd);
        Assert.Equal(End.AddSeconds(25), metadata.DataAsOf);
        Assert.Equal(2, metadata.Attempt);
    }

    private static JsonObject Payload() => JsonSerializer.SerializeToNode(new
    {
        data = new
        {
            watch = new { id = Id, version = Version },
            evaluation = new { bar_end = End, data_as_of = End.AddSeconds(15), attempt = 0, mode = "live" }
        }
    })!.AsObject();
}
