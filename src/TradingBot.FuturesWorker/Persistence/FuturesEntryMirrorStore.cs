using Npgsql;

namespace TradingBot.FuturesWorker;

internal sealed record FuturesEntryMirrorCommand(
    long Id,
    string SourceBotInstanceId,
    string SourceCycleId,
    string TargetBotInstanceId,
    string Pair,
    string KrakenSymbol,
    string SourceSide,
    string TargetSide,
    decimal TargetNotionalUsd,
    decimal Leverage,
    decimal SourceFillPrice,
    int? QuantityDecimals,
    int? PriceDecimals,
    DateTimeOffset CreatedAtUtc,
    int AttemptCount = 0);

internal interface IFuturesEntryMirrorStore
{
    Task PublishAsync(FuturesEntryMirrorCommand command, CancellationToken cancellationToken);

    Task<FuturesEntryMirrorCommand?> ClaimNextAsync(
        string sourceBotInstanceId,
        string targetBotInstanceId,
        TimeSpan staleClaimAfter,
        CancellationToken cancellationToken);

    Task MarkCompletedAsync(long id, string detail, CancellationToken cancellationToken);

    Task MarkForRetryAsync(long id, string error, CancellationToken cancellationToken);

    Task MarkFailedAsync(long id, string error, CancellationToken cancellationToken);
}

internal sealed class NullFuturesEntryMirrorStore : IFuturesEntryMirrorStore
{
    public Task PublishAsync(FuturesEntryMirrorCommand command, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<FuturesEntryMirrorCommand?> ClaimNextAsync(
        string sourceBotInstanceId,
        string targetBotInstanceId,
        TimeSpan staleClaimAfter,
        CancellationToken cancellationToken) =>
        Task.FromResult<FuturesEntryMirrorCommand?>(null);

    public Task MarkCompletedAsync(long id, string detail, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task MarkForRetryAsync(long id, string error, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task MarkFailedAsync(long id, string error, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal sealed class PostgresFuturesEntryMirrorStore
    : IFuturesEntryMirrorStore
{
    private readonly string _connectionString;
    private bool _schemaReady;

    public PostgresFuturesEntryMirrorStore(string connectionString)
    {
        _connectionString = connectionString;
        EnsureSchema();
    }

    public async Task PublishAsync(FuturesEntryMirrorCommand command, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var sql = new NpgsqlCommand(
            """
            insert into futures_entry_mirror_commands (
                source_bot_instance_id,
                source_cycle_id,
                target_bot_instance_id,
                pair,
                kraken_symbol,
                source_side,
                target_side,
                target_notional_usd,
                leverage,
                source_fill_price,
                quantity_decimals,
                price_decimals,
                status,
                created_at
            ) values (
                @source_bot_instance_id,
                @source_cycle_id,
                @target_bot_instance_id,
                @pair,
                @kraken_symbol,
                @source_side,
                @target_side,
                @target_notional_usd,
                @leverage,
                @source_fill_price,
                @quantity_decimals,
                @price_decimals,
                'PENDING',
                @created_at
            )
            on conflict (source_cycle_id, target_bot_instance_id, pair) do nothing
            """,
            connection);
        sql.Parameters.AddWithValue("source_bot_instance_id", command.SourceBotInstanceId);
        sql.Parameters.AddWithValue("source_cycle_id", command.SourceCycleId);
        sql.Parameters.AddWithValue("target_bot_instance_id", command.TargetBotInstanceId);
        sql.Parameters.AddWithValue("pair", command.Pair);
        sql.Parameters.AddWithValue("kraken_symbol", command.KrakenSymbol);
        sql.Parameters.AddWithValue("source_side", command.SourceSide);
        sql.Parameters.AddWithValue("target_side", command.TargetSide);
        sql.Parameters.AddWithValue("target_notional_usd", command.TargetNotionalUsd);
        sql.Parameters.AddWithValue("leverage", command.Leverage);
        sql.Parameters.AddWithValue("source_fill_price", command.SourceFillPrice);
        sql.Parameters.AddWithValue("quantity_decimals", (object?)command.QuantityDecimals ?? DBNull.Value);
        sql.Parameters.AddWithValue("price_decimals", (object?)command.PriceDecimals ?? DBNull.Value);
        sql.Parameters.AddWithValue("created_at", command.CreatedAtUtc.UtcDateTime);
        await sql.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<FuturesEntryMirrorCommand?> ClaimNextAsync(
        string sourceBotInstanceId,
        string targetBotInstanceId,
        TimeSpan staleClaimAfter,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var sql = new NpgsqlCommand(
            """
            with candidate as (
                select id
                from futures_entry_mirror_commands
                where source_bot_instance_id = @source_bot_instance_id
                  and target_bot_instance_id = @target_bot_instance_id
                  and (
                      status = 'PENDING'
                      or (status = 'PROCESSING' and claimed_at < now() - @stale_claim_after)
                  )
                order by created_at, id
                for update skip locked
                limit 1
            )
            update futures_entry_mirror_commands command
            set status = 'PROCESSING',
                claimed_at = now(),
                attempt_count = command.attempt_count + 1
            from candidate
            where command.id = candidate.id
            returning command.id,
                      command.source_bot_instance_id,
                      command.source_cycle_id,
                      command.target_bot_instance_id,
                      command.pair,
                      command.kraken_symbol,
                      command.source_side,
                      command.target_side,
                      command.target_notional_usd,
                      command.leverage,
                      command.source_fill_price,
                      command.quantity_decimals,
                      command.price_decimals,
                      command.created_at,
                      command.attempt_count
            """,
            connection,
            transaction);
        sql.Parameters.AddWithValue("source_bot_instance_id", sourceBotInstanceId);
        sql.Parameters.AddWithValue("target_bot_instance_id", targetBotInstanceId);
        sql.Parameters.AddWithValue("stale_claim_after", staleClaimAfter);

        FuturesEntryMirrorCommand? command = null;
        await using (var reader = await sql.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                var createdAt = DateTime.SpecifyKind(reader.GetDateTime(13), DateTimeKind.Utc);
                command = new FuturesEntryMirrorCommand(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetDecimal(8),
                    reader.GetDecimal(9),
                    reader.GetDecimal(10),
                    reader.IsDBNull(11) ? null : reader.GetInt32(11),
                    reader.IsDBNull(12) ? null : reader.GetInt32(12),
                    new DateTimeOffset(createdAt),
                    reader.GetInt32(14));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return command;
    }

    public Task MarkCompletedAsync(long id, string detail, CancellationToken cancellationToken) =>
        SetTerminalStatusAsync(id, "COMPLETED", detail, cancellationToken);

    public async Task MarkForRetryAsync(long id, string error, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var sql = new NpgsqlCommand(
            """
            update futures_entry_mirror_commands
            set status = 'PENDING',
                claimed_at = null,
                last_error = @last_error
            where id = @id
            """,
            connection);
        sql.Parameters.AddWithValue("id", id);
        sql.Parameters.AddWithValue("last_error", error);
        await sql.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task MarkFailedAsync(long id, string error, CancellationToken cancellationToken) =>
        SetTerminalStatusAsync(id, "FAILED", error, cancellationToken);

    private async Task SetTerminalStatusAsync(
        long id,
        string status,
        string detail,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var sql = new NpgsqlCommand(
            """
            update futures_entry_mirror_commands
            set status = @status,
                completed_at = now(),
                last_error = @detail
            where id = @id
            """,
            connection);
        sql.Parameters.AddWithValue("id", id);
        sql.Parameters.AddWithValue("status", status);
        sql.Parameters.AddWithValue("detail", detail);
        await sql.ExecuteNonQueryAsync(cancellationToken);
    }

    private void EnsureSchema()
    {
        if (_schemaReady)
        {
            return;
        }

        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var sql = new NpgsqlCommand(
            """
            create table if not exists futures_entry_mirror_commands (
                id bigserial primary key,
                source_bot_instance_id text not null,
                source_cycle_id text not null,
                target_bot_instance_id text not null,
                pair text not null,
                kraken_symbol text not null,
                source_side text not null,
                target_side text not null,
                target_notional_usd numeric not null,
                leverage numeric not null,
                source_fill_price numeric not null,
                quantity_decimals integer,
                price_decimals integer,
                status text not null default 'PENDING',
                attempt_count integer not null default 0,
                last_error text,
                created_at timestamptz not null default now(),
                claimed_at timestamptz,
                completed_at timestamptz,
                unique (source_cycle_id, target_bot_instance_id, pair)
            );

            create index if not exists ix_futures_entry_mirror_target_status_created
                on futures_entry_mirror_commands (target_bot_instance_id, source_bot_instance_id, status, created_at, id);
            """,
            connection);
        sql.ExecuteNonQuery();
        _schemaReady = true;
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        EnsureSchema();
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
