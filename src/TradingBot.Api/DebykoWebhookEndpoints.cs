using System.Text.Json;
using TradingBot.Core.Common;

namespace TradingBot.Api;

internal static class DebykoWebhookEndpoints
{
    private const int MaxBodyBytes = 64 * 1024;
    private static readonly TimeSpan SignatureTolerance = TimeSpan.FromMinutes(5);

    public static void MapDebykoWebhookEndpoints(
        this WebApplication app,
        IDebykoSignalInbox inbox)
    {
        app.MapPost(
            "/api/webhooks/debyko/{channelId:guid}",
            async (Guid channelId, HttpRequest request, CancellationToken cancellationToken) =>
            {
                if (!inbox.IsAvailable)
                {
                    return Results.Problem(
                        "The signal inbox database is not configured.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                var channel = await inbox.FindChannelAsync(channelId, cancellationToken);
                if (channel is null)
                {
                    return Results.NotFound();
                }

                if (!DebykoWebhookSignature.TryDecodeSecret(channel.SigningSecret, out var signingKey))
                {
                    return Results.Problem(
                        "The webhook channel signing key is invalid.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                var webhookId = request.Headers["webhook-id"].ToString();
                var webhookTimestamp = request.Headers["webhook-timestamp"].ToString();
                var webhookSignature = request.Headers["webhook-signature"].ToString();
                if (string.IsNullOrWhiteSpace(webhookId)
                    || string.IsNullOrWhiteSpace(webhookTimestamp)
                    || string.IsNullOrWhiteSpace(webhookSignature))
                {
                    return Results.BadRequest(new { error = "missing_webhook_signature_headers" });
                }

                if (!DebykoWebhookSignature.IsTimestampFresh(
                        webhookTimestamp,
                        DateTimeOffset.UtcNow,
                        SignatureTolerance))
                {
                    return Results.BadRequest(new { error = "stale_webhook_timestamp" });
                }

                byte[] rawBody;
                try
                {
                    rawBody = await ReadBodyAsync(request, cancellationToken);
                }
                catch (InvalidDataException)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                if (!DebykoWebhookSignature.Verify(
                        webhookId,
                        webhookTimestamp,
                        rawBody,
                        signingKey,
                        webhookSignature))
                {
                    return Results.Unauthorized();
                }

                DebykoWebhookEnvelope envelope;
                try
                {
                    envelope = DebykoWebhookEnvelope.Parse(rawBody);
                }
                catch (JsonException ex)
                {
                    return Results.BadRequest(new { error = "invalid_webhook_payload", detail = ex.Message });
                }

                if (!string.Equals(webhookId, envelope.EventId, StringComparison.Ordinal))
                {
                    return Results.BadRequest(new { error = "webhook_id_mismatch" });
                }

                if (string.Equals(envelope.EventType, "destination.verify", StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(envelope.Challenge))
                    {
                        return Results.BadRequest(new { error = "missing_verification_challenge" });
                    }

                    return Results.Ok(new
                    {
                        verify = DebykoWebhookSignature.VerificationProof(envelope.Challenge, signingKey)
                    });
                }

                if (string.Equals(envelope.EventType, "watch.became_true", StringComparison.Ordinal)
                    && !string.Equals(envelope.EventId, envelope.FiringId, StringComparison.Ordinal))
                {
                    return Results.BadRequest(new { error = "firing_id_mismatch" });
                }

                var inserted = await inbox.StoreAsync(channel, envelope, cancellationToken);
                return Results.Ok(new
                {
                    accepted = true,
                    duplicate = !inserted,
                    eventId = envelope.EventId,
                    botInstanceId = channel.BotInstanceId
                });
            });
    }

    private static async Task<byte[]> ReadBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBodyBytes)
        {
            throw new InvalidDataException("DEBYKO webhook body exceeds 64 KiB.");
        }

        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return stream.ToArray();
            }

            if (stream.Length + read > MaxBodyBytes)
            {
                throw new InvalidDataException("DEBYKO webhook body exceeds 64 KiB.");
            }

            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}
