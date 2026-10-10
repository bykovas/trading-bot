using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace TradingBot.Core.Common;

public sealed record DebykoWebhookChannel(
    Guid ChannelId,
    string BotInstanceId,
    string SigningSecret);

public sealed record DebykoWebhookEvent(
    long Id,
    Guid ChannelId,
    string BotInstanceId,
    string EventId,
    string? FiringId,
    string EventType,
    long? DestinationSequence,
    string? Tag,
    string? Instrument,
    DateTimeOffset? FiredAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string PayloadJson,
    DateTimeOffset ReceivedAtUtc,
    int AttemptCount = 0);

public sealed record DebykoWebhookEnvelope(
    string EventType,
    string EventId,
    string? FiringId,
    long? DestinationSequence,
    string? Tag,
    string? Instrument,
    DateTimeOffset? FiredAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string? Challenge,
    string PayloadJson)
{
    public static DebykoWebhookEnvelope Parse(ReadOnlySpan<byte> rawBody)
    {
        using var document = JsonDocument.Parse(rawBody.ToArray());
        var root = document.RootElement;
        var eventType = RequiredString(root, "type");
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("DEBYKO webhook data must be an object.");
        }

        return new DebykoWebhookEnvelope(
            eventType,
            RequiredString(data, "event_id"),
            OptionalString(data, "firing_id"),
            OptionalInt64(data, "destination_seq"),
            OptionalString(data, "tag"),
            OptionalString(data, "instrument"),
            OptionalInstant(data, "fired_at"),
            OptionalInstant(data, "expires_at"),
            OptionalString(data, "challenge"),
            Encoding.UTF8.GetString(rawBody));
    }

    private static string RequiredString(JsonElement element, string name) =>
        OptionalString(element, name) is { Length: > 0 } value
            ? value
            : throw new JsonException($"DEBYKO webhook field '{name}' is required.");

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? OptionalInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static DateTimeOffset? OptionalInstant(JsonElement element, string name)
    {
        var value = OptionalString(element, name);
        if (value is null)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : throw new JsonException($"DEBYKO webhook field '{name}' is not an RFC 3339 instant.");
    }
}

public static class DebykoWebhookSignature
{
    private const string SecretPrefix = "whsec_";

    public static bool TryDecodeSecret(string? value, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith(SecretPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            key = Convert.FromBase64String(value[SecretPrefix.Length..]);
            return key.Length is >= 24 and <= 64;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool IsTimestampFresh(
        string? webhookTimestamp,
        DateTimeOffset now,
        TimeSpan tolerance)
    {
        return long.TryParse(webhookTimestamp, out var unixSeconds)
            && (now - DateTimeOffset.FromUnixTimeSeconds(unixSeconds)).Duration() <= tolerance;
    }

    public static bool Verify(
        string webhookId,
        string webhookTimestamp,
        ReadOnlySpan<byte> rawBody,
        ReadOnlySpan<byte> key,
        string signatureHeader)
    {
        var prefix = Encoding.UTF8.GetBytes($"{webhookId}.{webhookTimestamp}.");
        var signedPayload = new byte[prefix.Length + rawBody.Length];
        prefix.CopyTo(signedPayload, 0);
        rawBody.CopyTo(signedPayload.AsSpan(prefix.Length));
        var expected = HMACSHA256.HashData(key, signedPayload);

        var matched = false;
        foreach (var token in signatureHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!token.StartsWith("v1,", StringComparison.Ordinal))
            {
                continue;
            }

            byte[] candidate;
            try
            {
                candidate = Convert.FromBase64String(token[3..]);
            }
            catch (FormatException)
            {
                continue;
            }

            if (candidate.Length == expected.Length)
            {
                matched |= CryptographicOperations.FixedTimeEquals(expected, candidate);
            }
        }

        return matched;
    }

    public static string VerificationProof(string challenge, ReadOnlySpan<byte> key) =>
        Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("verify." + challenge)));
}

public interface IDebykoSignalInbox
{
    bool IsAvailable { get; }

    Task<DebykoWebhookChannel?> FindChannelAsync(Guid channelId, CancellationToken cancellationToken);

    Task<bool> StoreAsync(
        DebykoWebhookChannel channel,
        DebykoWebhookEnvelope envelope,
        CancellationToken cancellationToken);

    Task<DebykoWebhookEvent?> ClaimNextAsync(
        string botInstanceId,
        TimeSpan staleClaimAfter,
        CancellationToken cancellationToken);

    Task MarkCompletedAsync(long eventId, string detail, CancellationToken cancellationToken);

    Task MarkIgnoredAsync(long eventId, string detail, CancellationToken cancellationToken);

    Task MarkForRetryAsync(long eventId, string error, CancellationToken cancellationToken);
}

public sealed class NullDebykoSignalInbox : IDebykoSignalInbox
{
    public bool IsAvailable => false;

    public Task<DebykoWebhookChannel?> FindChannelAsync(Guid channelId, CancellationToken cancellationToken) =>
        Task.FromResult<DebykoWebhookChannel?>(null);

    public Task<bool> StoreAsync(
        DebykoWebhookChannel channel,
        DebykoWebhookEnvelope envelope,
        CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<DebykoWebhookEvent?> ClaimNextAsync(
        string botInstanceId,
        TimeSpan staleClaimAfter,
        CancellationToken cancellationToken) =>
        Task.FromResult<DebykoWebhookEvent?>(null);

    public Task MarkCompletedAsync(long eventId, string detail, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task MarkIgnoredAsync(long eventId, string detail, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task MarkForRetryAsync(long eventId, string error, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public sealed class PostgresDebykoSignalInbox : IDebykoSignalInbox
{
    private readonly string _connectionString;
    private readonly object _schemaLock = new();
    private bool _schemaReady;

    public PostgresDebykoSignalInbox(string connectionString)
    {
        _connectionString = connectionString;
        EnsureSchema();
    }

    public bool IsAvailable => true;

    public async Task<DebykoWebhookChannel?> FindChannelAsync(
        Guid channelId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select channel_id, bot_instance_id, signing_secret
            from debyko_webhook_channels
            where channel_id = @channel_id
              and is_active
            """,
            connection);
        command.Parameters.AddWithValue("channel_id", channelId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DebykoWebhookChannel(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2));
    }

    public async Task<bool> StoreAsync(
        DebykoWebhookChannel channel,
        DebykoWebhookEnvelope envelope,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var insertEvent = new NpgsqlCommand(
            """
            insert into debyko_webhook_events (
                channel_id,
                bot_instance_id,
                event_id,
                firing_id,
                event_type,
                destination_seq,
                tag,
                instrument,
                fired_at,
                expires_at,
                payload
            ) values (
                @channel_id,
                @bot_instance_id,
                @event_id,
                @firing_id,
                @event_type,
                @destination_seq,
                @tag,
                @instrument,
                @fired_at,
                @expires_at,
                @payload
            )
            on conflict (channel_id, event_id) do nothing
            returning id
            """,
            connection,
            transaction);
        insertEvent.Parameters.AddWithValue("channel_id", channel.ChannelId);
        insertEvent.Parameters.AddWithValue("bot_instance_id", channel.BotInstanceId);
        insertEvent.Parameters.AddWithValue("event_id", envelope.EventId);
        insertEvent.Parameters.AddWithValue("firing_id", (object?)envelope.FiringId ?? DBNull.Value);
        insertEvent.Parameters.AddWithValue("event_type", envelope.EventType);
        insertEvent.Parameters.AddWithValue("destination_seq", (object?)envelope.DestinationSequence ?? DBNull.Value);
        insertEvent.Parameters.AddWithValue("tag", (object?)envelope.Tag ?? DBNull.Value);
        insertEvent.Parameters.AddWithValue("instrument", (object?)envelope.Instrument ?? DBNull.Value);
        AddInstant(insertEvent, "fired_at", envelope.FiredAtUtc);
        AddInstant(insertEvent, "expires_at", envelope.ExpiresAtUtc);
        insertEvent.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = envelope.PayloadJson;

        var insertedId = await insertEvent.ExecuteScalarAsync(cancellationToken);
        if (insertedId is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        await using var insertDelivery = new NpgsqlCommand(
            """
            insert into debyko_signal_deliveries (event_id, bot_instance_id, status)
            values (@event_id, @bot_instance_id, 'PENDING')
            on conflict (event_id) do nothing
            """,
            connection,
            transaction);
        insertDelivery.Parameters.AddWithValue("event_id", (long)insertedId);
        insertDelivery.Parameters.AddWithValue("bot_instance_id", channel.BotInstanceId);
        await insertDelivery.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<DebykoWebhookEvent?> ClaimNextAsync(
        string botInstanceId,
        TimeSpan staleClaimAfter,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            with candidate as (
                select delivery.event_id
                from debyko_signal_deliveries delivery
                join debyko_webhook_events event on event.id = delivery.event_id
                where delivery.bot_instance_id = @bot_instance_id
                  and (
                      delivery.status = 'PENDING'
                      or (delivery.status = 'PROCESSING' and delivery.claimed_at < now() - @stale_claim_after)
                  )
                order by event.received_at, event.id
                for update of delivery skip locked
                limit 1
            )
            update debyko_signal_deliveries delivery
            set status = 'PROCESSING',
                claimed_at = now(),
                attempt_count = delivery.attempt_count + 1
            from candidate, debyko_webhook_events event
            where delivery.event_id = candidate.event_id
              and event.id = candidate.event_id
            returning event.id,
                      event.channel_id,
                      event.bot_instance_id,
                      event.event_id,
                      event.firing_id,
                      event.event_type,
                      event.destination_seq,
                      event.tag,
                      event.instrument,
                      event.fired_at,
                      event.expires_at,
                      event.payload::text,
                      event.received_at,
                      delivery.attempt_count
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("bot_instance_id", BotInstanceId.Normalize(botInstanceId));
        command.Parameters.AddWithValue("stale_claim_after", staleClaimAfter);

        DebykoWebhookEvent? result = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                result = new DebykoWebhookEvent(
                    reader.GetInt64(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    ReadInstant(reader, 9),
                    ReadInstant(reader, 10),
                    reader.GetString(11),
                    ReadInstant(reader, 12)!.Value,
                    reader.GetInt32(13));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public Task MarkCompletedAsync(long eventId, string detail, CancellationToken cancellationToken) =>
        SetTerminalStatusAsync(eventId, "COMPLETED", detail, cancellationToken);

    public Task MarkIgnoredAsync(long eventId, string detail, CancellationToken cancellationToken) =>
        SetTerminalStatusAsync(eventId, "IGNORED", detail, cancellationToken);

    public async Task MarkForRetryAsync(long eventId, string error, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            update debyko_signal_deliveries
            set status = 'PENDING',
                claimed_at = null,
                detail = @detail
            where event_id = @event_id
            """,
            connection);
        command.Parameters.AddWithValue("event_id", eventId);
        command.Parameters.AddWithValue("detail", error);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SetTerminalStatusAsync(
        long eventId,
        string status,
        string detail,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            update debyko_signal_deliveries
            set status = @status,
                completed_at = now(),
                detail = @detail
            where event_id = @event_id
            """,
            connection);
        command.Parameters.AddWithValue("event_id", eventId);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("detail", detail);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private void EnsureSchema()
    {
        if (_schemaReady)
        {
            return;
        }

        lock (_schemaLock)
        {
            if (_schemaReady)
            {
                return;
            }

            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();
            using var schemaLock = new NpgsqlCommand(
                "select pg_advisory_lock(hashtext('tradingbot_debyko_signal_schema'))",
                connection);
            schemaLock.ExecuteNonQuery();
            try
            {
                using var command = new NpgsqlCommand(
                    """
                create table if not exists debyko_webhook_channels (
                    channel_id uuid primary key,
                    bot_instance_id text not null unique,
                    signing_secret text not null,
                    is_active boolean not null default true,
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now(),
                    check (btrim(bot_instance_id) <> ''),
                    check (signing_secret like 'whsec_%')
                );

                create table if not exists debyko_webhook_events (
                    id bigserial primary key,
                    channel_id uuid not null references debyko_webhook_channels(channel_id),
                    bot_instance_id text not null,
                    event_id text not null,
                    firing_id text,
                    event_type text not null,
                    destination_seq bigint,
                    tag text,
                    instrument text,
                    fired_at timestamptz,
                    expires_at timestamptz,
                    payload jsonb not null,
                    received_at timestamptz not null default now(),
                    unique (channel_id, event_id)
                );

                create index if not exists ix_debyko_webhook_events_instance_received
                    on debyko_webhook_events (bot_instance_id, received_at, id);

                create table if not exists debyko_signal_deliveries (
                    event_id bigint primary key references debyko_webhook_events(id) on delete cascade,
                    bot_instance_id text not null,
                    status text not null default 'PENDING',
                    attempt_count integer not null default 0,
                    claimed_at timestamptz,
                    completed_at timestamptz,
                    detail text
                );

                create index if not exists ix_debyko_signal_deliveries_instance_status
                    on debyko_signal_deliveries (bot_instance_id, status, event_id);
                """,
                    connection);
                command.ExecuteNonQuery();
                _schemaReady = true;
            }
            finally
            {
                using var schemaUnlock = new NpgsqlCommand(
                    "select pg_advisory_unlock(hashtext('tradingbot_debyko_signal_schema'))",
                    connection);
                schemaUnlock.ExecuteNonQuery();
            }
        }
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        EnsureSchema();
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static void AddInstant(NpgsqlCommand command, string name, DateTimeOffset? value)
    {
        command.Parameters.Add(name, NpgsqlDbType.TimestampTz).Value =
            value?.UtcDateTime ?? (object)DBNull.Value;
    }

    private static DateTimeOffset? ReadInstant(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
    }
}
