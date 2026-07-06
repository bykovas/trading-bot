using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace TradingBot.SpotWorker;

internal interface IDryRunPortfolioStore
{
    string StateDescription { get; }
    string EventsDescription { get; }
    PortfolioState? Load();
    void Save(PortfolioState state);
    void AppendCycle(DryRunCycleRecord record);

    // Persist the per-cycle light market snapshot (one row per universe pair) in a
    // single batch. Callers wrap this so a failure never blocks the trading cycle.
    void AppendMarketSnapshots(IReadOnlyList<MarketSnapshotRecord> snapshots);

    // Recent persisted market snapshots (utc >= sinceUtc), oldest first. Used to
    // hydrate the price-action history after a restart so the anti-lag guard is not
    // blind for several cycles. Callers wrap this: a failure only skips hydration.
    IReadOnlyList<MarketSnapshotRecord> LoadRecentMarketSnapshots(DateTimeOffset sinceUtc);
}

internal sealed class FileDryRunPortfolioStore(DryRunOptions options) : IDryRunPortfolioStore
{
    private readonly JsonSerializerOptions _stateJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly JsonSerializerOptions _eventJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private string StatePath => Path.Combine(options.OutputDirectory, options.StateFile);
    private string EventsPath => Path.Combine(options.OutputDirectory, options.EventsFile);
    private string MarketSnapshotsPath => Path.Combine(options.OutputDirectory, options.MarketSnapshotsFile);

    public string StateDescription => StatePath;
    public string EventsDescription => EventsPath;

    public PortfolioState? Load()
    {
        Directory.CreateDirectory(options.OutputDirectory);

        if (!File.Exists(StatePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(StatePath);
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<PortfolioState>(json, _stateJsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Console.WriteLine($"portfolio-load: failed to read {StatePath} ({ex.Message})");
            return null;
        }
    }

    public void Save(PortfolioState state)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, _stateJsonOptions));
    }

    public void AppendCycle(DryRunCycleRecord record)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        var line = JsonSerializer.Serialize(record, _eventJsonOptions);
        File.AppendAllText(EventsPath, line + Environment.NewLine);
    }

    public void AppendMarketSnapshots(IReadOnlyList<MarketSnapshotRecord> snapshots)
    {
        if (snapshots.Count == 0)
        {
            return;
        }

        Directory.CreateDirectory(options.OutputDirectory);
        var lines = snapshots.Select(snapshot => JsonSerializer.Serialize(snapshot, _eventJsonOptions));
        File.AppendAllLines(MarketSnapshotsPath, lines);
    }

    public IReadOnlyList<MarketSnapshotRecord> LoadRecentMarketSnapshots(DateTimeOffset sinceUtc)
    {
        if (!File.Exists(MarketSnapshotsPath))
        {
            return [];
        }

        var results = new List<MarketSnapshotRecord>();
        foreach (var line in File.ReadLines(MarketSnapshotsPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            MarketSnapshotRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<MarketSnapshotRecord>(line, _eventJsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (record is not null && record.Utc >= sinceUtc)
            {
                results.Add(record);
            }
        }

        return results.OrderBy(record => record.Utc).ToList();
    }
}

internal sealed class PostgresDryRunPortfolioStore(string connectionString, string botInstanceId = "default") : IDryRunPortfolioStore
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private bool _schemaReady;
    private int StateId => StableStateId(botInstanceId);

    public string StateDescription => $"postgres:portfolio_state[{botInstanceId}]";
    public string EventsDescription => $"postgres:dry_run_cycles[{botInstanceId}]";

    public PortfolioState? Load()
    {
        EnsureSchema();

        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(
            "select state_json::text from portfolio_state where id = @id and bot_instance_id = @bot_instance_id",
            connection);
        command.Parameters.AddWithValue("id", StateId);
        command.Parameters.AddWithValue("bot_instance_id", botInstanceId);
        var value = command.ExecuteScalar() as string;
        return string.IsNullOrWhiteSpace(value)
            ? null
            : JsonSerializer.Deserialize<PortfolioState>(value, _jsonOptions);
    }

    public void Save(PortfolioState state)
    {
        EnsureSchema();

        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(
            """
            insert into portfolio_state (id, bot_instance_id, updated_at, state_json)
            values (@id, @bot_instance_id, @updated_at, @state_json)
            on conflict (id) do update set
                bot_instance_id = excluded.bot_instance_id,
                updated_at = excluded.updated_at,
                state_json = excluded.state_json
            """,
            connection);
        command.Parameters.AddWithValue("id", StateId);
        command.Parameters.AddWithValue("bot_instance_id", botInstanceId);
        command.Parameters.AddWithValue("updated_at", state.UpdatedAt.UtcDateTime);
        command.Parameters.Add("state_json", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(state, _jsonOptions);
        command.ExecuteNonQuery();
    }

    public void AppendCycle(DryRunCycleRecord record)
    {
        EnsureSchema();

        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(
            """
            insert into dry_run_cycles (
                cycle_id,
                bot_instance_id,
                utc,
                worker_version,
                worker_commit,
                worker_build_utc,
                worker_image_tag,
                strategy_version,
                change_set,
                record_json)
            values (
                @cycle_id,
                @bot_instance_id,
                @utc,
                @worker_version,
                @worker_commit,
                @worker_build_utc,
                @worker_image_tag,
                @strategy_version,
                @change_set,
                @record_json)
            on conflict (cycle_id) do update set
                utc = excluded.utc,
                bot_instance_id = excluded.bot_instance_id,
                worker_version = excluded.worker_version,
                worker_commit = excluded.worker_commit,
                worker_build_utc = excluded.worker_build_utc,
                worker_image_tag = excluded.worker_image_tag,
                strategy_version = excluded.strategy_version,
                change_set = excluded.change_set,
                record_json = excluded.record_json
            """,
            connection);
        command.Parameters.AddWithValue("cycle_id", record.CycleId);
        command.Parameters.AddWithValue("bot_instance_id", botInstanceId);
        command.Parameters.AddWithValue("utc", record.Utc.UtcDateTime);
        command.Parameters.AddWithValue("worker_version", record.Worker.Version);
        command.Parameters.AddWithValue("worker_commit", record.Worker.Commit);
        command.Parameters.AddWithValue("worker_build_utc", record.Worker.BuildUtc);
        command.Parameters.AddWithValue("worker_image_tag", record.Worker.ImageTag);
        command.Parameters.AddWithValue("strategy_version", record.Worker.StrategyVersion);
        command.Parameters.AddWithValue("change_set", record.Worker.ChangeSet);
        command.Parameters.Add("record_json", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(record, _jsonOptions);
        command.ExecuteNonQuery();
    }

    public void AppendMarketSnapshots(IReadOnlyList<MarketSnapshotRecord> snapshots)
    {
        if (snapshots.Count == 0)
        {
            return;
        }

        EnsureSchema();

        // One batch COPY per cycle. Lean by design: no retention, no extra indexes.
        using var connection = OpenConnection();
        using var writer = connection.BeginBinaryImport(
            "copy market_snapshots (cycle_id, bot_instance_id, utc, pair, bid, ask, last, volume24h, change_percent) from stdin (format binary)");
        foreach (var snapshot in snapshots)
        {
            writer.StartRow();
            writer.Write(snapshot.CycleId, NpgsqlDbType.Text);
            writer.Write(botInstanceId, NpgsqlDbType.Text);
            writer.Write(snapshot.Utc.UtcDateTime, NpgsqlDbType.TimestampTz);
            writer.Write(snapshot.Pair, NpgsqlDbType.Text);
            writer.Write(snapshot.Bid, NpgsqlDbType.Numeric);
            writer.Write(snapshot.Ask, NpgsqlDbType.Numeric);
            writer.Write(snapshot.Last, NpgsqlDbType.Numeric);
            writer.Write(snapshot.Volume24h, NpgsqlDbType.Numeric);
            writer.Write(snapshot.ChangePercent, NpgsqlDbType.Numeric);
        }

        writer.Complete();
    }

    public IReadOnlyList<MarketSnapshotRecord> LoadRecentMarketSnapshots(DateTimeOffset sinceUtc)
    {
        EnsureSchema();

        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(
            """
            select cycle_id, bot_instance_id, utc, pair, bid, ask, last, volume24h, change_percent
            from market_snapshots
            where utc >= @since
              and bot_instance_id = @bot_instance_id
            order by utc
            """,
            connection);
        command.Parameters.AddWithValue("since", sinceUtc.UtcDateTime);
        command.Parameters.AddWithValue("bot_instance_id", botInstanceId);

        var results = new List<MarketSnapshotRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new MarketSnapshotRecord(
                reader.GetString(0),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc)),
                reader.GetString(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5),
                reader.GetDecimal(6),
                reader.GetDecimal(7),
                reader.GetDecimal(8),
                reader.GetString(1)));
        }

        return results;
    }

    private void EnsureSchema()
    {
        if (_schemaReady)
        {
            return;
        }

        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(
            """
            create table if not exists portfolio_state (
                id integer primary key,
                bot_instance_id text not null default 'default',
                updated_at timestamptz not null,
                state_json jsonb not null
            );

            alter table portfolio_state
                add column if not exists bot_instance_id text not null default 'default';

            do $$
            begin
                if exists (
                    select 1
                    from pg_constraint
                    where conrelid = 'portfolio_state'::regclass
                      and conname = 'portfolio_state_id_check'
                ) then
                    alter table portfolio_state drop constraint portfolio_state_id_check;
                end if;
            end $$;

            create table if not exists dry_run_cycles (
                cycle_id text primary key,
                bot_instance_id text not null default 'default',
                utc timestamptz not null,
                record_json jsonb not null
            );

            alter table dry_run_cycles
                add column if not exists bot_instance_id text not null default 'default',
                add column if not exists worker_version text,
                add column if not exists worker_commit text,
                add column if not exists worker_build_utc text,
                add column if not exists worker_image_tag text,
                add column if not exists strategy_version text,
                add column if not exists change_set text;

            create index if not exists ix_dry_run_cycles_utc on dry_run_cycles (utc desc);
            create index if not exists ix_dry_run_cycles_bot_instance_utc on dry_run_cycles (bot_instance_id, utc desc);
            create index if not exists ix_dry_run_cycles_worker_commit on dry_run_cycles (worker_commit, utc desc);
            create index if not exists ix_dry_run_cycles_strategy_version on dry_run_cycles (strategy_version, utc desc);
            create index if not exists ix_dry_run_cycles_change_set on dry_run_cycles (change_set, utc desc);

            create table if not exists market_snapshots (
                cycle_id text not null,
                bot_instance_id text not null default 'default',
                utc timestamptz not null,
                pair text not null,
                bid numeric not null,
                ask numeric not null,
                last numeric not null,
                volume24h numeric not null,
                change_percent numeric not null
            );

            alter table market_snapshots
                add column if not exists bot_instance_id text not null default 'default';

            create index if not exists ix_market_snapshots_cycle_id on market_snapshots (cycle_id);
            create index if not exists ix_market_snapshots_bot_instance_utc on market_snapshots (bot_instance_id, utc desc);

            drop view if exists dry_run_cycle_entry_diagnostics;
            drop view if exists dry_run_decisions;
            drop view if exists dry_run_cycle_summary;
            drop view if exists portfolio_positions;
            drop view if exists portfolio_summary;

            create or replace view portfolio_summary as
            select
                id,
                bot_instance_id,
                updated_at,
                (state_json ->> 'cashEur')::numeric as cash_eur,
                (state_json ->> 'positionsValueEur')::numeric as positions_value_eur,
                (state_json ->> 'totalValueEur')::numeric as total_value_eur,
                jsonb_array_length(coalesce(state_json -> 'positions', '[]'::jsonb)) as open_positions,
                state_json -> 'dailyRisk' ->> 'dateUtc' as daily_risk_date_utc,
                (state_json -> 'dailyRisk' ->> 'realizedPnlEur')::numeric as daily_realized_pnl_eur
            from portfolio_state;

            create or replace view portfolio_positions as
            select
                state.id as portfolio_state_id,
                state.bot_instance_id,
                state.updated_at as portfolio_updated_at,
                position ->> 'pair' as pair,
                position ->> 'side' as side,
                (position ->> 'quantity')::numeric as quantity,
                (position ->> 'entryPrice')::numeric as entry_price,
                (position ->> 'entryNotionalEur')::numeric as entry_notional_eur,
                (position ->> 'lastPrice')::numeric as last_price,
                (position ->> 'marketValueEur')::numeric as market_value_eur,
                (position ->> 'unrealizedPnlEur')::numeric as unrealized_pnl_eur,
                (position ->> 'unrealizedPnlPercent')::numeric as unrealized_pnl_percent,
                (position ->> 'openedAtUtc')::timestamptz as opened_at_utc,
                (position ->> 'lastActionAtUtc')::timestamptz as last_action_at_utc
            from portfolio_state state
            cross join lateral jsonb_array_elements(coalesce(state.state_json -> 'positions', '[]'::jsonb)) as position;

            create or replace view dry_run_cycle_summary as
            select
                cycle.cycle_id,
                cycle.bot_instance_id,
                cycle.utc,
                cycle.record_json ->> 'marketDataMode' as market_data_mode,
                cycle.record_json ->> 'aiProvider' as ai_provider,
                jsonb_array_length(coalesce(cycle.record_json -> 'activePairs', '[]'::jsonb)) as active_pairs_count,
                jsonb_array_length(coalesce(cycle.record_json -> 'decisions', '[]'::jsonb)) as decisions_count,
                (cycle.record_json -> 'portfolioBefore' ->> 'cashEur')::numeric as cash_before_eur,
                (cycle.record_json -> 'portfolioAfter' ->> 'cashEur')::numeric as cash_after_eur,
                (cycle.record_json -> 'portfolioBefore' ->> 'totalValueEur')::numeric as portfolio_value_before_eur,
                (cycle.record_json -> 'portfolioAfter' ->> 'totalValueEur')::numeric as portfolio_value_after_eur,
                (
                    select count(*)
                    from jsonb_array_elements(coalesce(cycle.record_json -> 'decisions', '[]'::jsonb)) as decision
                    where decision -> 'dryRunAction' ->> 'action' = 'WOULD_BUY'
                ) as would_buy_count,
                (
                    select count(*)
                    from jsonb_array_elements(coalesce(cycle.record_json -> 'decisions', '[]'::jsonb)) as decision
                    where decision -> 'dryRunAction' ->> 'action' = 'WOULD_SELL'
                ) as would_sell_count,
                (
                    select count(*)
                    from jsonb_array_elements(coalesce(cycle.record_json -> 'decisions', '[]'::jsonb)) as decision
                    where coalesce(decision ->> 'broker', '') like 'VALIDATED_OK%'
                ) as validated_order_count
            from dry_run_cycles cycle;

            create or replace view dry_run_decisions as
            select
                cycle.cycle_id,
                cycle.bot_instance_id,
                cycle.utc,
                decision ->> 'pair' as pair,
                decision -> 'dryRunAction' ->> 'action' as action,
                decision ->> 'desiredPosition' as desired_position,
                (decision ->> 'price')::numeric as price,
                (decision ->> 'score')::numeric as score,
                (decision ->> 'riskApproved')::boolean as risk_approved,
                decision ->> 'broker' as broker,
                (decision -> 'dryRunAction' ->> 'targetNotionalEur')::numeric as target_notional_eur,
                (decision -> 'dryRunAction' ->> 'quantity')::numeric as quantity,
                (decision -> 'dryRunAction' ->> 'fillPrice')::numeric as fill_price,
                (decision -> 'dryRunAction' ->> 'feeEur')::numeric as fee_eur,
                (decision -> 'dryRunAction' ->> 'cashBeforeEur')::numeric as cash_before_eur,
                (decision -> 'dryRunAction' ->> 'cashAfterEur')::numeric as cash_after_eur,
                (decision -> 'dryRunAction' ->> 'portfolioValueBeforeEur')::numeric as portfolio_value_before_eur,
                (decision -> 'dryRunAction' ->> 'portfolioValueAfterEur')::numeric as portfolio_value_after_eur,
                decision -> 'dryRunAction' ->> 'reason' as reason,
                decision -> 'dryRunAction' ->> 'holdReasonCode' as hold_reason_code,
                decision -> 'dryRunAction' ->> 'exitReasonCode' as exit_reason_code,
                decision ->> 'entryRejectionReason' as entry_rejection_reason,
                (decision ->> 'spreadPercent')::numeric as spread_percent,
                decision ->> 'priceActionDirection' as price_action_direction,
                (decision ->> 'priceActionTrendPercent')::numeric as price_action_trend_percent,
                (decision ->> 'exploratory')::boolean as exploratory,
                -- Appended after initial rollout: CREATE OR REPLACE VIEW only allows
                -- new columns at the END, so these diagnostic-only fields stay last.
                (decision ->> 'hasBullishStructure')::boolean as has_bullish_structure,
                (decision ->> 'emaFullyConfirmed')::boolean as ema_fully_confirmed,
                (decision ->> 'bullishEmaGapPercent')::numeric as bullish_ema_gap_percent,
                (decision ->> 'emaGapVelocityPercent')::numeric as ema_gap_velocity_percent,
                (decision ->> 'earlyEntryEligible')::boolean as early_entry_eligible,
                decision ->> 'earlyEntryReason' as early_entry_reason,
                (decision ->> 'earlyEntryDiagnosticScore')::numeric as early_entry_diagnostic_score,
                (decision ->> 'earlyEntrySuggestedNotionalEur')::numeric as early_entry_suggested_notional_eur
            from dry_run_cycles cycle
            cross join lateral jsonb_array_elements(coalesce(cycle.record_json -> 'decisions', '[]'::jsonb)) as decision;

            create or replace view dry_run_cycle_entry_diagnostics as
            select
                cycle.cycle_id,
                cycle.bot_instance_id,
                cycle.utc,
                (cycle.record_json -> 'entryDiagnostics' ->> 'snapshotPairsAvailable')::int as snapshot_pairs_available,
                (cycle.record_json -> 'entryDiagnostics' ->> 'activePairsEvaluated')::int as active_pairs_evaluated,
                (cycle.record_json -> 'entryDiagnostics' ->> 'entryPairsEvaluated')::int as entry_pairs_evaluated,
                (cycle.record_json -> 'entryDiagnostics' ->> 'scoreAtLeast075')::int as score_at_least_075,
                (cycle.record_json -> 'entryDiagnostics' ->> 'scoreAtLeast080')::int as score_at_least_080,
                (cycle.record_json -> 'entryDiagnostics' ->> 'scoreAtLeast085')::int as score_at_least_085,
                (cycle.record_json -> 'entryDiagnostics' ->> 'scoreAtLeast090')::int as score_at_least_090,
                (cycle.record_json -> 'entryDiagnostics' ->> 'hardFilterPassCount')::int as hard_filter_pass_count,
                (cycle.record_json -> 'entryDiagnostics' ->> 'eligibleEntryCandidates')::int as eligible_entry_candidates,
                cycle.record_json -> 'entryDiagnostics' ->> 'chosenPair' as chosen_pair,
                cycle.record_json -> 'entryDiagnostics' ->> 'noTradeReason' as no_trade_reason,
                cycle.record_json -> 'entryDiagnostics' -> 'rejectionCounts' as rejection_counts,
                cycle.record_json -> 'entryDiagnostics' -> 'topCandidates' as top_candidates,
                cycle.record_json -> 'entryDiagnostics' -> 'excludedPairs' as excluded_pairs,
                -- Appended after initial rollout: CREATE OR REPLACE VIEW only allows
                -- new columns at the END, so this stays last.
                (cycle.record_json -> 'entryDiagnostics' ->> 'priceActionReadyCount')::int as price_action_ready_count
            from dry_run_cycles cycle;

            -- Clean slate for the market-prefixed instance-id scheme (spot-live,
            -- spot-virtual, futures-live, futures-virtual): rows written under the
            -- legacy ids are dropped rather than migrated. No-ops once applied.
            delete from portfolio_state  where bot_instance_id in ('live', 'virtual', 'default');
            delete from dry_run_cycles   where bot_instance_id in ('live', 'virtual', 'default');
            delete from market_snapshots where bot_instance_id in ('live', 'virtual', 'default');
            """,
            connection);
        command.ExecuteNonQuery();
        _schemaReady = true;
    }

    private NpgsqlConnection OpenConnection()
    {
        var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static int StableStateId(string instanceId)
    {
        if (instanceId.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        unchecked
        {
            var hash = 23;
            foreach (var ch in instanceId.ToLowerInvariant())
            {
                hash = hash * 31 + ch;
            }

            return Math.Abs(hash == int.MinValue ? 2 : hash) + 2;
        }
    }
}
