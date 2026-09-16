using Npgsql;

namespace TradingBot.FuturesWorker;

internal sealed record FuturesCapacitySkipCounts(int Slots, int Margin)
{
    public int Total => Slots + Margin;
}

internal sealed record FuturesCapacitySummaryRow(
    string BotInstanceId,
    int Slots,
    int Margin);

internal interface IFuturesCapacitySummaryStore
{
    Task AddAsync(
        string botInstanceId,
        DateTimeOffset windowStartUtc,
        FuturesCapacitySkipCounts counts,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<FuturesCapacitySummaryRow>> LoadAsync(
        DateTimeOffset windowStartUtc,
        CancellationToken cancellationToken);

    Task<bool> TryClaimDeliveryAsync(
        DateTimeOffset windowStartUtc,
        string reporterInstanceId,
        CancellationToken cancellationToken);

    Task MarkDeliveredAsync(DateTimeOffset windowStartUtc, CancellationToken cancellationToken);

    Task ReleaseDeliveryClaimAsync(DateTimeOffset windowStartUtc, CancellationToken cancellationToken);
}

internal sealed class NullFuturesCapacitySummaryStore : IFuturesCapacitySummaryStore
{
    public Task AddAsync(string botInstanceId, DateTimeOffset windowStartUtc, FuturesCapacitySkipCounts counts, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<FuturesCapacitySummaryRow>> LoadAsync(DateTimeOffset windowStartUtc, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FuturesCapacitySummaryRow>>([]);

    public Task<bool> TryClaimDeliveryAsync(DateTimeOffset windowStartUtc, string reporterInstanceId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task MarkDeliveredAsync(DateTimeOffset windowStartUtc, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ReleaseDeliveryClaimAsync(DateTimeOffset windowStartUtc, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class PostgresFuturesCapacitySummaryStore : IFuturesCapacitySummaryStore
{
    private readonly string _connectionString;

    public PostgresFuturesCapacitySummaryStore(string connectionString)
    {
        _connectionString = connectionString;
        EnsureSchema();
    }

    public async Task AddAsync(
        string botInstanceId,
        DateTimeOffset windowStartUtc,
        FuturesCapacitySkipCounts counts,
        CancellationToken cancellationToken)
    {
        if (counts.Total <= 0)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into futures_capacity_summary_buckets (
                window_start_utc,
                bot_instance_id,
                slots_skipped,
                margin_skipped,
                updated_at)
            values (
                @window_start_utc,
                @bot_instance_id,
                @slots_skipped,
                @margin_skipped,
                now())
            on conflict (window_start_utc, bot_instance_id) do update set
                slots_skipped = futures_capacity_summary_buckets.slots_skipped + excluded.slots_skipped,
                margin_skipped = futures_capacity_summary_buckets.margin_skipped + excluded.margin_skipped,
                updated_at = now();
            """,
            connection);
        command.Parameters.AddWithValue("window_start_utc", windowStartUtc);
        command.Parameters.AddWithValue("bot_instance_id", botInstanceId);
        command.Parameters.AddWithValue("slots_skipped", counts.Slots);
        command.Parameters.AddWithValue("margin_skipped", counts.Margin);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FuturesCapacitySummaryRow>> LoadAsync(
        DateTimeOffset windowStartUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select bot_instance_id, slots_skipped, margin_skipped
            from futures_capacity_summary_buckets
            where window_start_utc = @window_start_utc
            order by bot_instance_id;
            """,
            connection);
        command.Parameters.AddWithValue("window_start_utc", windowStartUtc);

        var rows = new List<FuturesCapacitySummaryRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new FuturesCapacitySummaryRow(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt32(2)));
        }

        return rows;
    }

    public async Task<bool> TryClaimDeliveryAsync(
        DateTimeOffset windowStartUtc,
        string reporterInstanceId,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into futures_capacity_summary_deliveries (
                window_start_utc,
                reporter_instance_id,
                claimed_at)
            values (@window_start_utc, @reporter_instance_id, now())
            on conflict (window_start_utc) do update set
                reporter_instance_id = excluded.reporter_instance_id,
                claimed_at = excluded.claimed_at
            where futures_capacity_summary_deliveries.reported_at is null
              and futures_capacity_summary_deliveries.claimed_at < now() - interval '10 minutes'
            returning window_start_utc;
            """,
            connection);
        command.Parameters.AddWithValue("window_start_utc", windowStartUtc);
        command.Parameters.AddWithValue("reporter_instance_id", reporterInstanceId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task MarkDeliveredAsync(DateTimeOffset windowStartUtc, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            update futures_capacity_summary_deliveries
            set reported_at = now()
            where window_start_utc = @window_start_utc
              and reported_at is null;
            """,
            connection);
        command.Parameters.AddWithValue("window_start_utc", windowStartUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReleaseDeliveryClaimAsync(DateTimeOffset windowStartUtc, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            delete from futures_capacity_summary_deliveries
            where window_start_utc = @window_start_utc
              and reported_at is null;
            """,
            connection);
        command.Parameters.AddWithValue("window_start_utc", windowStartUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private void EnsureSchema()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(
            """
            create table if not exists futures_capacity_summary_buckets (
                window_start_utc timestamptz not null,
                bot_instance_id text not null,
                slots_skipped integer not null default 0 check (slots_skipped >= 0),
                margin_skipped integer not null default 0 check (margin_skipped >= 0),
                updated_at timestamptz not null default now(),
                primary key (window_start_utc, bot_instance_id)
            );

            create table if not exists futures_capacity_summary_deliveries (
                window_start_utc timestamptz primary key,
                reporter_instance_id text not null,
                claimed_at timestamptz not null,
                reported_at timestamptz null
            );
            """,
            connection);
        command.ExecuteNonQuery();
    }
}

internal sealed class FuturesCapacitySummaryReporter(
    IFuturesCapacitySummaryStore store,
    ITelegramNotifier telegram,
    string reporterInstanceId)
{
    private static readonly (string InstanceId, string Label)[] Instances =
    [
        ("futures-live", "BYKO"),
        ("futures-lukas-live", "LUKO"),
        ("futures-pukis-live", "PUKO")
    ];

    public Task RecordAsync(
        string botInstanceId,
        DateTimeOffset utc,
        FuturesCapacitySkipCounts counts,
        CancellationToken cancellationToken) =>
        store.AddAsync(botInstanceId, WindowStart(utc), counts, cancellationToken);

    public async Task ReportCompletedWindowAsync(DateTimeOffset utc, CancellationToken cancellationToken)
    {
        var completedWindow = WindowStart(utc).AddHours(-2);
        var rows = await store.LoadAsync(completedWindow, cancellationToken);
        if (rows.Sum(row => row.Slots + row.Margin) <= 0)
        {
            return;
        }

        if (!await store.TryClaimDeliveryAsync(completedWindow, reporterInstanceId, cancellationToken))
        {
            return;
        }

        try
        {
            var byInstance = rows.ToDictionary(row => row.BotInstanceId, StringComparer.OrdinalIgnoreCase);
            var lines = Instances.Select(instance =>
            {
                byInstance.TryGetValue(instance.InstanceId, out var row);
                var slots = row?.Slots ?? 0;
                var margin = row?.Margin ?? 0;
                return slots + margin == 0
                    ? $"{instance.Label} — 0"
                    : $"{instance.Label} — {slots + margin} (slotai: {slots}, marža: {margin})";
            });
            var message = "📊 <b>Per paskutines 2 val. dėl pajėgumo praleisti įėjimai</b>\n"
                + string.Join("\n", lines);

            if (await telegram.TrySendAsync(message, cancellationToken))
            {
                await store.MarkDeliveredAsync(completedWindow, cancellationToken);
            }
            else
            {
                await store.ReleaseDeliveryClaimAsync(completedWindow, cancellationToken);
            }
        }
        catch
        {
            await store.ReleaseDeliveryClaimAsync(completedWindow, cancellationToken);
            throw;
        }
    }

    internal static DateTimeOffset WindowStart(DateTimeOffset utc)
    {
        var roundedHour = utc.UtcDateTime.Hour - utc.UtcDateTime.Hour % 2;
        return new DateTimeOffset(
            utc.UtcDateTime.Year,
            utc.UtcDateTime.Month,
            utc.UtcDateTime.Day,
            roundedHour,
            0,
            0,
            TimeSpan.Zero);
    }
}
