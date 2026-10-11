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
