using System.Security.Cryptography;
using System.Text;
using TradingBot.Core.Common;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class DebykoWebhookTests
{
    [Fact]
    public void Standard_webhooks_reference_vector_verifies()
    {
        Assert.True(DebykoWebhookSignature.TryDecodeSecret(
            "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw",
            out var key));
        var payload = Encoding.UTF8.GetBytes("{\"test\": 2432232314}");

        var verified = DebykoWebhookSignature.Verify(
            "msg_p5jXN8AQM9LWM0D4loKWxJek",
            "1614265330",
            payload,
            key,
            "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=");

        Assert.True(verified);
    }

    [Fact]
    public void Signature_is_over_the_unchanged_body_bytes()
    {
        Assert.True(DebykoWebhookSignature.TryDecodeSecret(
            "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw",
            out var key));
        var payload = Encoding.UTF8.GetBytes("{\"test\": 2432232314}");
        payload[^1] ^= 0xff;

        Assert.False(DebykoWebhookSignature.Verify(
            "msg_p5jXN8AQM9LWM0D4loKWxJek",
            "1614265330",
            payload,
            key,
            "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE="));
    }

    [Theory]
    [InlineData(-301, false)]
    [InlineData(-299, true)]
    [InlineData(299, true)]
    [InlineData(301, false)]
    public void Timestamp_tolerance_is_plus_minus_five_minutes(int offsetSeconds, bool expected)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        Assert.Equal(
            expected,
            DebykoWebhookSignature.IsTimestampFresh(
                now.AddSeconds(offsetSeconds).ToUnixTimeSeconds().ToString(),
                now,
                TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Verification_proof_uses_the_debyko_challenge_contract()
    {
        Assert.True(DebykoWebhookSignature.TryDecodeSecret(
            "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw",
            out var key));
        var expected = Convert.ToBase64String(
            HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("verify.challenge-value")));

        Assert.Equal(expected, DebykoWebhookSignature.VerificationProof("challenge-value", key));
    }

    [Fact]
    public void Became_true_payload_preserves_routing_and_expiry_fields()
    {
        var body = Encoding.UTF8.GetBytes(
            """
            {
              "type": "watch.became_true",
              "timestamp": "2026-10-10T10:00:01Z",
              "data": {
                "schema": "debyko.agent.event/1",
                "event_id": "fire-1",
                "firing_id": "fire-1",
                "destination_seq": 42,
                "tag": "BYKO_FLUSH_LONG",
                "instrument": "BTC-PERP",
                "fired_at": "2026-10-10T10:00:00Z",
                "expires_at": "2026-10-10T10:01:00Z",
                "answer": { "venues": [] }
              }
            }
            """);

        var parsed = DebykoWebhookEnvelope.Parse(body);

        Assert.Equal("watch.became_true", parsed.EventType);
        Assert.Equal("fire-1", parsed.EventId);
        Assert.Equal("fire-1", parsed.FiringId);
        Assert.Equal(42, parsed.DestinationSequence);
        Assert.Equal("BYKO_FLUSH_LONG", parsed.Tag);
        Assert.Equal("BTC-PERP", parsed.Instrument);
        Assert.Equal(DateTimeOffset.Parse("2026-10-10T10:00:00Z"), parsed.FiredAtUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-10-10T10:01:00Z"), parsed.ExpiresAtUtc);
        Assert.Contains("\"answer\"", parsed.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Verification_payload_exposes_the_challenge()
    {
        var body = Encoding.UTF8.GetBytes(
            """
            {
              "type": "destination.verify",
              "data": {
                "event_id": "verify-1",
                "challenge": "abc123",
                "expires_at": "2026-10-10T10:01:00Z"
              }
            }
            """);

        var parsed = DebykoWebhookEnvelope.Parse(body);

        Assert.Equal("destination.verify", parsed.EventType);
        Assert.Equal("abc123", parsed.Challenge);
    }
}
