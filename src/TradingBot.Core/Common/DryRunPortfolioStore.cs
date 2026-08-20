using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace TradingBot.Core.Common;

public interface IDryRunPortfolioStore
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

    // Deposits, withdrawals and transfers read from the exchange ledger. Idempotent
    // on the exchange's entry id, so overlapping windows can be re-synced freely.
    // Callers wrap this: a failure must never block the trading cycle.
    void SaveCashEvents(IReadOnlyList<PortfolioCashEvent> events);
}

public sealed class FileDryRunPortfolioStore(DryRunOptions options) : IDryRunPortfolioStore
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

    // File mode backs a local dry run with no dashboard behind it, so ledger events
    // have nowhere to go. Kept as an explicit no-op rather than an interface split.
    public void SaveCashEvents(IReadOnlyList<PortfolioCashEvent> events)
    {
    }
}

public sealed class PostgresDryRunPortfolioStore(string connectionString, string botInstanceId = "default") : IDryRunPortfolioStore
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
        using var summaryCommand = new NpgsqlCommand(
            """
            select updated_at,
                   cash_eur,
                   cash_quote_value,
                   cash_quote_currency,
                   daily_risk_date_utc,
                   daily_realized_pnl_eur,
                   external_pnl_eur
            from portfolio_state_summary
            where bot_instance_id = @bot_instance_id
            limit 1
            """,
            connection);
        summaryCommand.Parameters.AddWithValue("bot_instance_id", botInstanceId);

        using var summaryReader = summaryCommand.ExecuteReader();
        if (!summaryReader.Read())
        {
            return null;
        }

        var state = new PortfolioState
        {
            UpdatedAt = new DateTimeOffset(DateTime.SpecifyKind(summaryReader.GetDateTime(0), DateTimeKind.Utc)),
            CashEur = summaryReader.GetDecimal(1),
            CashQuoteValue = summaryReader.IsDBNull(2) ? null : summaryReader.GetDecimal(2),
            CashQuoteCurrency = summaryReader.IsDBNull(3) ? null : summaryReader.GetString(3),
            DailyRisk = summaryReader.IsDBNull(4)
                ? null
                : new DailyRiskState
                {
                    DateUtc = summaryReader.GetString(4),
                    RealizedPnlEur = summaryReader.IsDBNull(5) ? 0m : summaryReader.GetDecimal(5)
                },
            ExternalPnlEur = summaryReader.GetDecimal(6)
        };
        summaryReader.Close();

        using (var command = new NpgsqlCommand(
            """
            select pair,
                   side,
                   quantity,
                   entry_price,
                   entry_notional_eur,
                   last_price,
                   market_value_eur,
                   unrealized_pnl_eur,
                   unrealized_pnl_percent,
                   opened_at_utc,
                   last_action_at_utc,
                   peak_pnl_percent,
                   entry_score,
                   exit_mode,
                   entry_atr,
                   stop_loss_price,
                   take_profit_price,
                   round_trip_cost_estimate_pct,
                   expected_funding_pct,
                   atr_pct,
                   stop_distance_pct,
                   take_profit_distance_pct,
                   exchange_stop_loss_price,
                   exchange_take_profit_price,
                   exchange_protection_multiplier_percent,
                   trailing_stop_state,
                   trailing_stop_percent,
                   trailing_stop_order_id,
                   trailing_activated_at_utc,
                   low_score_cycles,
                   leverage,
                   initial_margin_eur,
                   mark_price,
                   liquidation_price,
                   liquidation_distance_percent,
                   funding_paid_eur,
                   tp_order_state,
                   sl_order_state,
                   origin,
                   entry_channel,
                   flipped_entry
            from portfolio_position_state
            where bot_instance_id = @bot_instance_id
            order by position_index
            """,
            connection))
        {
            command.Parameters.AddWithValue("bot_instance_id", botInstanceId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                state.Positions.Add(new PortfolioPosition
                {
                    Pair = reader.GetString(0),
                    Side = reader.GetString(1),
                    Quantity = reader.GetDecimal(2),
                    EntryPrice = reader.GetDecimal(3),
                    EntryNotionalEur = reader.GetDecimal(4),
                    LastPrice = reader.GetDecimal(5),
                    MarketValueEur = reader.GetDecimal(6),
                    UnrealizedPnlEur = reader.GetDecimal(7),
                    UnrealizedPnlPercent = reader.GetDecimal(8),
                    OpenedAtUtc = GetNullableDateTimeOffset(reader, 9),
                    LastActionAtUtc = GetNullableDateTimeOffset(reader, 10),
                    PeakPnlPercent = GetNullableDecimal(reader, 11),
                    EntryScore = GetNullableDecimal(reader, 12),
                    ExitMode = GetNullableString(reader, 13),
                    EntryAtr = GetNullableDecimal(reader, 14),
                    StopLossPrice = GetNullableDecimal(reader, 15),
                    TakeProfitPrice = GetNullableDecimal(reader, 16),
                    RoundTripCostEstimatePct = GetNullableDecimal(reader, 17),
                    ExpectedFundingPct = GetNullableDecimal(reader, 18),
                    AtrPct = GetNullableDecimal(reader, 19),
                    StopDistancePct = GetNullableDecimal(reader, 20),
                    TakeProfitDistancePct = GetNullableDecimal(reader, 21),
                    ExchangeStopLossPrice = GetNullableDecimal(reader, 22),
                    ExchangeTakeProfitPrice = GetNullableDecimal(reader, 23),
                    ExchangeProtectionMultiplierPercent = GetNullableDecimal(reader, 24),
                    TrailingStopState = GetNullableString(reader, 25),
                    TrailingStopPercent = GetNullableDecimal(reader, 26),
                    TrailingStopOrderId = GetNullableString(reader, 27),
                    TrailingActivatedAtUtc = GetNullableDateTimeOffset(reader, 28),
                    LowScoreCycles = reader.GetInt32(29),
                    Leverage = GetNullableDecimal(reader, 30),
                    InitialMarginEur = GetNullableDecimal(reader, 31),
                    MarkPrice = GetNullableDecimal(reader, 32),
                    LiquidationPrice = GetNullableDecimal(reader, 33),
                    LiquidationDistancePercent = GetNullableDecimal(reader, 34),
                    FundingPaidEur = GetNullableDecimal(reader, 35),
                    TpOrderState = GetNullableString(reader, 36),
                    SlOrderState = GetNullableString(reader, 37),
                    Origin = GetNullableString(reader, 38),
                    EntryChannel = GetNullableString(reader, 39),
                    FlippedEntry = !reader.IsDBNull(40) && reader.GetBoolean(40)
                });
            }
        }

        using (var command = new NpgsqlCommand(
            """
            select pair, last_buy_at_utc, last_sell_at_utc, last_stop_loss_at_utc
            from portfolio_action_history_state
            where bot_instance_id = @bot_instance_id
            order by pair
            """,
            connection))
        {
            command.Parameters.AddWithValue("bot_instance_id", botInstanceId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                state.ActionHistory.Add(new PairActionHistory
                {
                    Pair = reader.GetString(0),
                    LastBuyAtUtc = GetNullableDateTimeOffset(reader, 1),
                    LastSellAtUtc = GetNullableDateTimeOffset(reader, 2),
                    LastStopLossAtUtc = GetNullableDateTimeOffset(reader, 3)
                });
            }
        }

        using (var command = new NpgsqlCommand(
            """
            select pair, exchange_order_id, created_at_utc, requested_quantity, submitted_limit_price
            from pending_futures_order_state
            where bot_instance_id = @bot_instance_id
            order by order_index
            """,
            connection))
        {
            command.Parameters.AddWithValue("bot_instance_id", botInstanceId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                state.PendingFuturesOrders.Add(new PendingFuturesOrder
                {
                    Pair = reader.GetString(0),
                    ExchangeOrderId = GetNullableString(reader, 1),
                    CreatedAtUtc = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc)),
                    RequestedQuantity = GetNullableDecimal(reader, 3),
                    SubmittedLimitPrice = GetNullableDecimal(reader, 4)
                });
            }
        }

        return state;
    }

    public void Save(PortfolioState state)
    {
        EnsureSchema();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = new NpgsqlCommand(
            """
            insert into portfolio_state (id, bot_instance_id, updated_at)
            values (@id, @bot_instance_id, @updated_at)
            on conflict (id) do update set
                bot_instance_id = excluded.bot_instance_id,
                updated_at = excluded.updated_at
            """,
            connection);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("id", StateId);
        command.Parameters.AddWithValue("bot_instance_id", botInstanceId);
        command.Parameters.AddWithValue("updated_at", state.UpdatedAt.UtcDateTime);
        command.ExecuteNonQuery();

        SaveNormalizedPortfolioState(connection, transaction, state);
        transaction.Commit();
    }

    public void AppendCycle(DryRunCycleRecord record)
    {
        EnsureSchema();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
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
                change_set)
            values (
                @cycle_id,
                @bot_instance_id,
                @utc,
                @worker_version,
                @worker_commit,
                @worker_build_utc,
                @worker_image_tag,
                @strategy_version,
                @change_set)
            on conflict (cycle_id) do update set
                utc = excluded.utc,
                bot_instance_id = excluded.bot_instance_id,
                worker_version = excluded.worker_version,
                worker_commit = excluded.worker_commit,
                worker_build_utc = excluded.worker_build_utc,
                worker_image_tag = excluded.worker_image_tag,
                strategy_version = excluded.strategy_version,
                change_set = excluded.change_set
            """,
            connection);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("cycle_id", record.CycleId);
        command.Parameters.AddWithValue("bot_instance_id", botInstanceId);
        command.Parameters.AddWithValue("utc", record.Utc.UtcDateTime);
        command.Parameters.AddWithValue("worker_version", record.Worker.Version);
        command.Parameters.AddWithValue("worker_commit", record.Worker.Commit);
        command.Parameters.AddWithValue("worker_build_utc", record.Worker.BuildUtc);
        command.Parameters.AddWithValue("worker_image_tag", record.Worker.ImageTag);
        command.Parameters.AddWithValue("strategy_version", record.Worker.StrategyVersion);
        command.Parameters.AddWithValue("change_set", record.Worker.ChangeSet);
        command.ExecuteNonQuery();

        SaveNormalizedCycle(connection, transaction, record);
        transaction.Commit();
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

    public void SaveCashEvents(IReadOnlyList<PortfolioCashEvent> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        EnsureSchema();

        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(
            """
            insert into portfolio_cash_events
                (bot_instance_id, event_id, occurred_at, event_type, amount, asset, source)
            select
                @bot_instance_id,
                unnest(@event_ids),
                unnest(@occurred_at),
                unnest(@event_types),
                unnest(@amounts),
                unnest(@assets),
                unnest(@sources)
            on conflict (bot_instance_id, event_id) do nothing
            """,
            connection);
        command.Parameters.Add("bot_instance_id", NpgsqlDbType.Text).Value = botInstanceId;
        command.Parameters.Add("event_ids", NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            events.Select(item => item.EventId).ToArray();
        command.Parameters.Add("occurred_at", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz).Value =
            events.Select(item => item.OccurredAt.UtcDateTime).ToArray();
        command.Parameters.Add("event_types", NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            events.Select(item => item.EventType).ToArray();
        command.Parameters.Add("amounts", NpgsqlDbType.Array | NpgsqlDbType.Numeric).Value =
            events.Select(item => item.Amount).ToArray();
        command.Parameters.Add("assets", NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            events.Select(item => (object?)item.Asset ?? DBNull.Value).ToArray();
        command.Parameters.Add("sources", NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            events.Select(item => item.Source).ToArray();
        command.ExecuteNonQuery();
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
                updated_at timestamptz not null
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
                utc timestamptz not null
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
            create index if not exists ix_dry_run_cycles_bot_instance_utc_cycle on dry_run_cycles (bot_instance_id, utc desc, cycle_id desc);
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
            create index if not exists ix_market_snapshots_bot_pair_utc on market_snapshots (bot_instance_id, pair, utc desc, cycle_id desc);
            create index if not exists ix_market_snapshots_cycle_pair on market_snapshots (cycle_id, pair);

            create table if not exists portfolio_state_summary (
                bot_instance_id text primary key,
                state_id integer not null,
                updated_at timestamptz not null,
                cash_eur numeric not null,
                cash_quote_value numeric,
                cash_quote_currency text,
                positions_value_eur numeric not null,
                total_value_eur numeric not null,
                open_positions integer not null,
                daily_risk_date_utc text,
                daily_realized_pnl_eur numeric,
                external_pnl_eur numeric not null default 0
            );

            create table if not exists portfolio_position_state (
                bot_instance_id text not null,
                updated_at timestamptz not null,
                position_index integer not null,
                pair text not null,
                side text not null,
                quantity numeric not null,
                entry_price numeric not null,
                entry_notional_eur numeric not null,
                last_price numeric not null,
                market_value_eur numeric not null,
                unrealized_pnl_eur numeric not null,
                unrealized_pnl_percent numeric not null,
                opened_at_utc timestamptz,
                last_action_at_utc timestamptz,
                peak_pnl_percent numeric,
                entry_score numeric,
                exit_mode text,
                entry_atr numeric,
                stop_loss_price numeric,
                take_profit_price numeric,
                round_trip_cost_estimate_pct numeric,
                expected_funding_pct numeric,
                atr_pct numeric,
                stop_distance_pct numeric,
                take_profit_distance_pct numeric,
                exchange_stop_loss_price numeric,
                exchange_take_profit_price numeric,
                exchange_protection_multiplier_percent numeric,
                trailing_stop_state text,
                trailing_stop_percent numeric,
                trailing_stop_order_id text,
                trailing_activated_at_utc timestamptz,
                low_score_cycles integer not null,
                leverage numeric,
                initial_margin_eur numeric,
                mark_price numeric,
                liquidation_price numeric,
                liquidation_distance_percent numeric,
                funding_paid_eur numeric,
                tp_order_state text,
                sl_order_state text,
                origin text,
                entry_channel text,
                flipped_entry boolean not null default false,
                primary key (bot_instance_id, position_index)
            );

            create index if not exists ix_portfolio_position_state_pair on portfolio_position_state (bot_instance_id, pair);
            create index if not exists ix_portfolio_position_state_origin on portfolio_position_state (bot_instance_id, origin);

            alter table portfolio_position_state
                add column if not exists flipped_entry boolean not null default false;

            alter table portfolio_state_summary
                add column if not exists cash_quote_value numeric,
                add column if not exists cash_quote_currency text;

            create table if not exists portfolio_cash_events (
                bot_instance_id text not null,
                event_id text not null,
                occurred_at timestamptz not null,
                event_type text not null,
                amount numeric not null,
                asset text,
                source text not null,
                recorded_at timestamptz not null default now(),
                primary key (bot_instance_id, event_id)
            );

            create index if not exists ix_portfolio_cash_events_bot_occurred
                on portfolio_cash_events (bot_instance_id, occurred_at desc);

            create table if not exists portfolio_action_history_state (
                bot_instance_id text not null,
                pair text not null,
                last_buy_at_utc timestamptz,
                last_sell_at_utc timestamptz,
                last_stop_loss_at_utc timestamptz,
                primary key (bot_instance_id, pair)
            );

            create table if not exists pending_futures_order_state (
                bot_instance_id text not null,
                order_index integer not null,
                pair text not null,
                exchange_order_id text,
                created_at_utc timestamptz not null,
                requested_quantity numeric,
                submitted_limit_price numeric,
                primary key (bot_instance_id, order_index)
            );

            create table if not exists dry_run_cycle_facts (
                cycle_id text primary key,
                bot_instance_id text not null,
                bot_instance_name text not null,
                utc timestamptz not null,
                market_data_mode text not null,
                ai_provider text not null,
                worker_version text,
                worker_commit text,
                worker_build_utc text,
                worker_image_tag text,
                strategy_version text,
                change_set text,
                active_pairs_count integer not null,
                decisions_count integer not null,
                cash_before_eur numeric not null,
                cash_after_eur numeric not null,
                positions_value_before_eur numeric not null,
                positions_value_after_eur numeric not null,
                portfolio_value_before_eur numeric not null,
                portfolio_value_after_eur numeric not null,
                would_buy_count integer not null,
                would_sell_count integer not null,
                validated_order_count integer not null
            );

            create index if not exists ix_dry_run_cycle_facts_bot_utc on dry_run_cycle_facts (bot_instance_id, utc desc, cycle_id desc);
            create index if not exists ix_dry_run_cycle_facts_change_set on dry_run_cycle_facts (change_set, utc desc);
            create index if not exists ix_dry_run_cycle_facts_strategy_utc on dry_run_cycle_facts (strategy_version, utc desc, cycle_id desc);
            create index if not exists ix_dry_run_cycle_facts_bot_meta_utc on dry_run_cycle_facts (bot_instance_id, strategy_version, change_set, utc desc, cycle_id desc);

            create table if not exists dry_run_cycle_active_pairs (
                cycle_id text not null references dry_run_cycle_facts (cycle_id) on delete cascade,
                pair_index integer not null,
                pair text not null,
                primary key (cycle_id, pair_index)
            );

            create index if not exists ix_dry_run_cycle_active_pairs_pair on dry_run_cycle_active_pairs (pair, cycle_id);
            create index if not exists ix_dry_run_cycle_active_pairs_cycle_pair on dry_run_cycle_active_pairs (cycle_id, pair_index);

            create table if not exists dry_run_decision_facts (
                cycle_id text not null references dry_run_cycle_facts (cycle_id) on delete cascade,
                decision_index integer not null,
                bot_instance_id text not null,
                utc timestamptz not null,
                pair text not null,
                price numeric not null,
                fast_ema numeric,
                slow_ema numeric,
                rsi numeric,
                desired_position text not null,
                score numeric not null,
                risk_approved boolean not null,
                broker text,
                entry_rejection_reason text,
                spread_percent numeric not null,
                price_action_direction text,
                price_action_trend_percent numeric,
                exploratory boolean not null,
                has_bullish_structure boolean not null,
                ema_fully_confirmed boolean not null,
                bullish_ema_gap_percent numeric,
                ema_gap_velocity_percent numeric,
                allows_short boolean not null,
                has_bearish_structure boolean not null,
                bearish_ema_gap_percent numeric,
                short_score numeric,
                long_score_threshold numeric,
                short_score_threshold numeric,
                minimum_ema_gap_percent numeric,
                short_base_block_reason_code text,
                short_base_block_reason text,
                early_entry_eligible boolean not null,
                early_entry_reason text,
                early_entry_diagnostic_score numeric not null,
                early_entry_suggested_notional_eur numeric not null,
                primary key (cycle_id, decision_index)
            );

            create index if not exists ix_dry_run_decision_facts_bot_utc on dry_run_decision_facts (bot_instance_id, utc desc);
            create index if not exists ix_dry_run_decision_facts_pair on dry_run_decision_facts (bot_instance_id, pair, utc desc);
            create index if not exists ix_dry_run_decision_facts_action_pair on dry_run_decision_facts (pair, cycle_id);
            create index if not exists ix_dry_run_decision_facts_cycle_pair on dry_run_decision_facts (cycle_id, pair);
            create index if not exists ix_dry_run_decision_facts_bot_cycle on dry_run_decision_facts (bot_instance_id, cycle_id);

            create table if not exists dry_run_decision_risk_reasons (
                cycle_id text not null,
                decision_index integer not null,
                reason_index integer not null,
                reason text not null,
                primary key (cycle_id, decision_index, reason_index),
                foreign key (cycle_id, decision_index) references dry_run_decision_facts (cycle_id, decision_index) on delete cascade
            );

            create table if not exists dry_run_signal_contributions (
                cycle_id text not null,
                decision_index integer not null,
                contribution_index integer not null,
                name text not null,
                value numeric not null,
                reason text not null,
                primary key (cycle_id, decision_index, contribution_index),
                foreign key (cycle_id, decision_index) references dry_run_decision_facts (cycle_id, decision_index) on delete cascade
            );

            create table if not exists dry_run_actions (
                cycle_id text not null,
                decision_index integer not null,
                pair text not null,
                action text not null,
                reason text not null,
                hold_reason_code text,
                exit_reason_code text,
                desired_position text not null,
                target_notional_eur numeric not null,
                quantity numeric not null,
                entry_price numeric not null,
                last_price numeric not null,
                fill_price numeric not null,
                fee_eur numeric not null,
                gross_notional_eur numeric not null,
                net_notional_eur numeric not null,
                cash_before_eur numeric not null,
                cash_after_eur numeric not null,
                portfolio_value_before_eur numeric not null,
                portfolio_value_after_eur numeric not null,
                fill_source text,
                modeled_fill_price numeric,
                modeled_fee_eur numeric,
                round_trip_cost_estimate_pct numeric,
                expected_funding_pct numeric,
                atr_pct numeric,
                stop_distance_pct numeric,
                take_profit_distance_pct numeric,
                open_risk_eur numeric,
                queue_ahead_eur numeric,
                maker_order_filled_eur numeric,
                maker_fill_rate numeric,
                time_to_fill_ms bigint,
                repeg_count integer,
                funding_state text,
                btc_regime_state text,
                short_allowed text,
                requested_notional_eur numeric,
                filled_notional_eur numeric,
                side text,
                reduce_only boolean,
                leverage numeric,
                exit_trigger_source text,
                entry_channel text,
                exchange_order_id text,
                exchange_fill_timestamp timestamptz,
                requested_margin_eur numeric,
                requested_leverage numeric,
                actual_initial_margin_eur numeric,
                actual_effective_leverage numeric,
                target_risk_eur numeric,
                sized_notional_eur numeric,
                required_margin_eur numeric,
                effective_leverage numeric,
                projected_stop_loss_eur numeric,
                execution_cost_model text,
                stop_source text,
                notional_cap_reason text,
                range_basis text,
                close_percentile numeric,
                recent_swing_position numeric,
                primary key (cycle_id, decision_index),
                foreign key (cycle_id, decision_index) references dry_run_decision_facts (cycle_id, decision_index) on delete cascade
            );

            create index if not exists ix_dry_run_actions_action_pair on dry_run_actions (action, pair);
            create index if not exists ix_dry_run_actions_exchange_order on dry_run_actions (exchange_order_id);
            create index if not exists ix_dry_run_actions_action_cycle on dry_run_actions (action, cycle_id);

            create table if not exists dry_run_entry_freshness (
                cycle_id text not null,
                decision_index integer not null,
                entry_freshness_position_in_24h_range_pct numeric,
                entry_freshness_distance_from_recent_high_pct numeric,
                entry_freshness_last_snapshot_step_pct numeric,
                entry_freshness_short_snapshot_slope_pct numeric,
                entry_freshness_positive_steps_in_last_3 integer,
                entry_freshness_is_near_high boolean,
                entry_freshness_has_fresh_upward_tape boolean,
                entry_freshness_has_fresh_breakout boolean,
                entry_freshness_block_reason text,
                entry_freshness_recent_candle_momentum_pct numeric,
                entry_distance_from_local_high_pct numeric,
                local_high_source text,
                breakout_buffer_pct numeric,
                live_price_vs_signal_close_pct numeric,
                post_fill_entry_distance_from_local_high_pct numeric,
                post_fill_live_price_vs_signal_close_pct numeric,
                signal_price numeric,
                pre_submit_bid numeric,
                pre_submit_ask numeric,
                submitted_limit_price numeric,
                requested_quantity numeric,
                filled_quantity numeric,
                average_fill_price numeric,
                entry_deviation_from_signal_pct numeric,
                entry_deviation_from_ask_pct numeric,
                dip_bounce_min_score_applied numeric,
                primary key (cycle_id, decision_index),
                foreign key (cycle_id, decision_index) references dry_run_decision_facts (cycle_id, decision_index) on delete cascade
            );

            create table if not exists dry_run_long_range_diagnostics (
                cycle_id text not null,
                decision_index integer not null,
                long_range_entry_price numeric,
                long_range_entry_price_source text,
                long_range_absolute_low_24h numeric,
                long_range_absolute_high_24h numeric,
                long_range_robust_low_24h numeric,
                long_range_robust_high_24h numeric,
                long_range_24h_source text,
                long_range_24h_sample_count integer,
                long_range_24h_position_raw numeric,
                long_range_24h_position numeric,
                long_range_max_position_for_long numeric,
                long_range_distance_from_24h_low_pct numeric,
                long_range_rising_snapshot_count integer,
                entry_blocked_by_24h_range boolean,
                long_range_block_reason_code text,
                btc_recent_change_pct numeric,
                relative_strength_pct numeric,
                zone text,
                anti_chase_applied boolean,
                confirmations_met integer,
                confirmations_required integer,
                effective_max_drift_pct numeric,
                atr_pct numeric,
                primary key (cycle_id, decision_index),
                foreign key (cycle_id, decision_index) references dry_run_decision_facts (cycle_id, decision_index) on delete cascade
            );

            -- "create table if not exists" never adds columns to an already-created
            -- table, so new diagnostics need an explicit migration or the insert fails
            -- on a live database.
            alter table dry_run_long_range_diagnostics
                add column if not exists btc_recent_change_pct numeric,
                add column if not exists relative_strength_pct numeric;

            create table if not exists dry_run_cycle_entry_diagnostic_facts (
                cycle_id text primary key references dry_run_cycle_facts (cycle_id) on delete cascade,
                snapshot_pairs_available integer not null,
                active_pairs_evaluated integer not null,
                entry_pairs_evaluated integer not null,
                price_action_ready_count integer not null,
                score_at_least_075 integer not null,
                score_at_least_080 integer not null,
                score_at_least_085 integer not null,
                score_at_least_090 integer not null,
                hard_filter_pass_count integer not null,
                eligible_entry_candidates integer not null,
                chosen_pair text,
                no_trade_reason text,
                execution_mode text not null,
                fill_rate numeric not null,
                pairs_passed_spread integer not null,
                pairs_passed_volume integer not null,
                pairs_passed_depth integer not null,
                open_risk_eur numeric not null,
                btc_regime_state text not null,
                pairs_passed_exit_depth integer not null,
                funding_state text not null
            );

            create table if not exists dry_run_rejection_counts (
                cycle_id text not null references dry_run_cycle_facts (cycle_id) on delete cascade,
                reason text not null,
                count integer not null,
                primary key (cycle_id, reason)
            );

            create table if not exists dry_run_top_candidates (
                cycle_id text not null references dry_run_cycle_facts (cycle_id) on delete cascade,
                candidate_index integer not null,
                pair text not null,
                score numeric not null,
                desired_position text not null,
                spread_percent numeric not null,
                price numeric not null,
                bid numeric not null,
                ask numeric not null,
                has_bullish_structure boolean not null,
                ema_fully_confirmed boolean not null,
                bullish_ema_gap_percent numeric,
                ema_gap_velocity_percent numeric,
                early_entry_eligible boolean not null,
                early_entry_reason text,
                early_entry_diagnostic_score numeric not null,
                early_entry_suggested_notional_eur numeric not null,
                price_action_direction text not null,
                price_action_trend_percent numeric,
                price_action_state text not null,
                price_action_samples_available integer not null,
                price_action_samples_required integer not null,
                price_action_oldest_sample_utc timestamptz,
                price_action_newest_sample_utc timestamptz,
                hard_filters_passed boolean not null,
                quality_filters_passed boolean not null,
                rejection_reason text,
                exploratory boolean not null,
                primary key (cycle_id, candidate_index)
            );

            create table if not exists dry_run_top_candidate_missing_confirmations (
                cycle_id text not null,
                candidate_index integer not null,
                confirmation_index integer not null,
                confirmation text not null,
                primary key (cycle_id, candidate_index, confirmation_index),
                foreign key (cycle_id, candidate_index) references dry_run_top_candidates (cycle_id, candidate_index) on delete cascade
            );

            create table if not exists dry_run_excluded_pairs (
                cycle_id text not null references dry_run_cycle_facts (cycle_id) on delete cascade,
                excluded_index integer not null,
                pair text not null,
                reason text not null,
                last numeric not null,
                change_percent numeric not null,
                volume_rank integer,
                est_24h_volume_eur numeric,
                spread_percent numeric,
                advisor_rank integer,
                primary key (cycle_id, excluded_index)
            );

            create index if not exists ix_dry_run_excluded_pairs_cycle_pair on dry_run_excluded_pairs (cycle_id, pair);

            alter table dry_run_long_range_diagnostics
                add column if not exists zone text,
                add column if not exists anti_chase_applied boolean,
                add column if not exists confirmations_met integer,
                add column if not exists confirmations_required integer,
                add column if not exists effective_max_drift_pct numeric,
                add column if not exists atr_pct numeric;

            drop view if exists dry_run_cycle_records;
            drop view if exists dry_run_cycle_entry_diagnostics;
            drop view if exists dry_run_decisions;
            drop view if exists dry_run_cycle_summary;
            drop view if exists portfolio_positions;
            drop view if exists portfolio_summary;

            alter table portfolio_state drop column if exists state_json;
            alter table dry_run_cycles drop column if exists record_json;

            create or replace view portfolio_summary as
            select
                summary.state_id as id,
                summary.bot_instance_id,
                summary.updated_at,
                summary.cash_eur,
                summary.cash_quote_value,
                summary.cash_quote_currency,
                summary.positions_value_eur,
                summary.total_value_eur,
                summary.open_positions,
                summary.daily_risk_date_utc,
                summary.daily_realized_pnl_eur
            from portfolio_state_summary summary;

            create or replace view portfolio_positions as
            select
                summary.state_id as portfolio_state_id,
                position.bot_instance_id,
                position.updated_at as portfolio_updated_at,
                position.pair,
                position.side,
                position.quantity,
                position.entry_price,
                position.entry_notional_eur,
                position.last_price,
                position.market_value_eur,
                position.unrealized_pnl_eur,
                position.unrealized_pnl_percent,
                position.opened_at_utc,
                position.last_action_at_utc,
                position.leverage,
                position.initial_margin_eur,
                position.mark_price,
                position.liquidation_price,
                position.liquidation_distance_percent,
                position.funding_paid_eur,
                position.tp_order_state,
                position.sl_order_state,
                position.entry_channel
            from portfolio_position_state position
            join portfolio_state_summary summary on summary.bot_instance_id = position.bot_instance_id;

            create or replace view dry_run_cycle_summary as
            select
                cycle.cycle_id,
                cycle.bot_instance_id,
                cycle.utc,
                cycle.market_data_mode,
                cycle.ai_provider,
                cycle.active_pairs_count,
                cycle.decisions_count,
                cycle.cash_before_eur,
                cycle.cash_after_eur,
                cycle.portfolio_value_before_eur,
                cycle.portfolio_value_after_eur,
                cycle.would_buy_count,
                cycle.would_sell_count,
                cycle.validated_order_count
            from dry_run_cycle_facts cycle;

            create or replace view dry_run_decisions as
            select
                decision.cycle_id,
                decision.bot_instance_id,
                decision.utc,
                decision.pair,
                action.action,
                decision.desired_position,
                decision.price,
                decision.score,
                decision.risk_approved,
                decision.broker,
                action.target_notional_eur,
                action.quantity,
                action.fill_price,
                action.fee_eur,
                action.cash_before_eur,
                action.cash_after_eur,
                action.portfolio_value_before_eur,
                action.portfolio_value_after_eur,
                action.reason,
                action.hold_reason_code,
                action.exit_reason_code,
                decision.entry_rejection_reason,
                decision.spread_percent,
                decision.price_action_direction,
                decision.price_action_trend_percent,
                decision.exploratory,
                decision.has_bullish_structure,
                decision.ema_fully_confirmed,
                decision.bullish_ema_gap_percent,
                decision.ema_gap_velocity_percent,
                decision.early_entry_eligible,
                decision.early_entry_reason,
                decision.early_entry_diagnostic_score,
                decision.early_entry_suggested_notional_eur,
                action.side,
                action.reduce_only,
                action.leverage,
                action.exit_trigger_source,
                action.fill_source,
                action.modeled_fill_price,
                action.modeled_fee_eur,
                freshness.entry_freshness_position_in_24h_range_pct,
                freshness.entry_freshness_distance_from_recent_high_pct,
                freshness.entry_freshness_last_snapshot_step_pct,
                freshness.entry_freshness_short_snapshot_slope_pct,
                freshness.entry_freshness_positive_steps_in_last_3,
                freshness.entry_freshness_is_near_high,
                freshness.entry_freshness_has_fresh_upward_tape,
                freshness.entry_freshness_has_fresh_breakout,
                freshness.entry_freshness_block_reason,
                freshness.signal_price,
                freshness.pre_submit_bid,
                freshness.pre_submit_ask,
                freshness.submitted_limit_price,
                freshness.requested_quantity,
                freshness.filled_quantity,
                freshness.average_fill_price,
                freshness.entry_deviation_from_signal_pct,
                freshness.entry_deviation_from_ask_pct,
                action.exchange_order_id,
                action.exchange_fill_timestamp,
                action.entry_channel,
                freshness.entry_distance_from_local_high_pct,
                freshness.local_high_source,
                freshness.breakout_buffer_pct,
                freshness.live_price_vs_signal_close_pct,
                freshness.post_fill_entry_distance_from_local_high_pct,
                freshness.post_fill_live_price_vs_signal_close_pct,
                freshness.entry_freshness_recent_candle_momentum_pct,
                freshness.dip_bounce_min_score_applied,
                long_range.long_range_entry_price,
                long_range.long_range_entry_price_source,
                long_range.long_range_absolute_low_24h,
                long_range.long_range_absolute_high_24h,
                long_range.long_range_robust_low_24h,
                long_range.long_range_robust_high_24h,
                long_range.long_range_24h_source,
                long_range.long_range_24h_sample_count,
                long_range.long_range_24h_position_raw,
                long_range.long_range_24h_position,
                long_range.long_range_max_position_for_long,
                long_range.long_range_distance_from_24h_low_pct,
                long_range.long_range_rising_snapshot_count,
                long_range.entry_blocked_by_24h_range,
                long_range.long_range_block_reason_code,
                long_range.btc_recent_change_pct,
                long_range.relative_strength_pct,
                long_range.zone,
                long_range.anti_chase_applied,
                long_range.confirmations_met,
                long_range.confirmations_required,
                long_range.effective_max_drift_pct,
                long_range.atr_pct
            from dry_run_decision_facts decision
            join dry_run_actions action on action.cycle_id = decision.cycle_id and action.decision_index = decision.decision_index
            left join dry_run_entry_freshness freshness on freshness.cycle_id = decision.cycle_id and freshness.decision_index = decision.decision_index
            left join dry_run_long_range_diagnostics long_range on long_range.cycle_id = decision.cycle_id and long_range.decision_index = decision.decision_index;

            -- Clean slate for the market-prefixed instance-id scheme (spot-live,
            -- spot-virtual, futures-live, futures-virtual): rows written under the
            -- legacy ids are dropped rather than migrated. No-ops once applied.
            delete from portfolio_state  where bot_instance_id in ('live', 'virtual', 'default');
            delete from dry_run_cycles   where bot_instance_id in ('live', 'virtual', 'default');
            delete from market_snapshots where bot_instance_id in ('live', 'virtual', 'default');
            delete from portfolio_state_summary where bot_instance_id in ('live', 'virtual', 'default');
            delete from portfolio_position_state where bot_instance_id in ('live', 'virtual', 'default');
            delete from portfolio_action_history_state where bot_instance_id in ('live', 'virtual', 'default');
            delete from pending_futures_order_state where bot_instance_id in ('live', 'virtual', 'default');
            delete from dry_run_cycle_facts where bot_instance_id in ('live', 'virtual', 'default');
            """,
            connection);
        command.ExecuteNonQuery();
        _schemaReady = true;
    }

    private void SaveNormalizedPortfolioState(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PortfolioState state)
    {
        using (var command = new NpgsqlCommand(
            """
            insert into portfolio_state_summary (
                bot_instance_id,
                state_id,
                updated_at,
                cash_eur,
                cash_quote_value,
                cash_quote_currency,
                positions_value_eur,
                total_value_eur,
                open_positions,
                daily_risk_date_utc,
                daily_realized_pnl_eur,
                external_pnl_eur)
            values (
                @bot_instance_id,
                @state_id,
                @updated_at,
                @cash_eur,
                @cash_quote_value,
                @cash_quote_currency,
                @positions_value_eur,
                @total_value_eur,
                @open_positions,
                @daily_risk_date_utc,
                @daily_realized_pnl_eur,
                @external_pnl_eur)
            on conflict (bot_instance_id) do update set
                state_id = excluded.state_id,
                updated_at = excluded.updated_at,
                cash_eur = excluded.cash_eur,
                cash_quote_value = excluded.cash_quote_value,
                cash_quote_currency = excluded.cash_quote_currency,
                positions_value_eur = excluded.positions_value_eur,
                total_value_eur = excluded.total_value_eur,
                open_positions = excluded.open_positions,
                daily_risk_date_utc = excluded.daily_risk_date_utc,
                daily_realized_pnl_eur = excluded.daily_realized_pnl_eur,
                external_pnl_eur = excluded.external_pnl_eur
            """,
            connection,
            transaction))
        {
            Add(command, "bot_instance_id", NpgsqlDbType.Text, botInstanceId);
            Add(command, "state_id", NpgsqlDbType.Integer, StateId);
            Add(command, "updated_at", NpgsqlDbType.TimestampTz, state.UpdatedAt.UtcDateTime);
            Add(command, "cash_eur", NpgsqlDbType.Numeric, state.CashEur);
            Add(command, "cash_quote_value", NpgsqlDbType.Numeric, state.CashQuoteValue);
            Add(command, "cash_quote_currency", NpgsqlDbType.Text, state.CashQuoteCurrency);
            Add(command, "positions_value_eur", NpgsqlDbType.Numeric, state.PositionsValueEur);
            Add(command, "total_value_eur", NpgsqlDbType.Numeric, state.TotalValueEur);
            Add(command, "open_positions", NpgsqlDbType.Integer, state.Positions.Count);
            Add(command, "daily_risk_date_utc", NpgsqlDbType.Text, state.DailyRisk?.DateUtc);
            Add(command, "daily_realized_pnl_eur", NpgsqlDbType.Numeric, state.DailyRisk?.RealizedPnlEur);
            Add(command, "external_pnl_eur", NpgsqlDbType.Numeric, state.ExternalPnlEur);
            command.ExecuteNonQuery();
        }

        Execute(connection, transaction, "delete from portfolio_position_state where bot_instance_id = @bot_instance_id",
            ("bot_instance_id", NpgsqlDbType.Text, botInstanceId));
        Execute(connection, transaction, "delete from portfolio_action_history_state where bot_instance_id = @bot_instance_id",
            ("bot_instance_id", NpgsqlDbType.Text, botInstanceId));
        Execute(connection, transaction, "delete from pending_futures_order_state where bot_instance_id = @bot_instance_id",
            ("bot_instance_id", NpgsqlDbType.Text, botInstanceId));

        for (var i = 0; i < state.Positions.Count; i++)
        {
            var position = state.Positions[i];
            using var command = new NpgsqlCommand(
                """
                insert into portfolio_position_state (
                    bot_instance_id,
                    updated_at,
                    position_index,
                    pair,
                    side,
                    quantity,
                    entry_price,
                    entry_notional_eur,
                    last_price,
                    market_value_eur,
                    unrealized_pnl_eur,
                    unrealized_pnl_percent,
                    opened_at_utc,
                    last_action_at_utc,
                    peak_pnl_percent,
                    entry_score,
                    exit_mode,
                    entry_atr,
                    stop_loss_price,
                    take_profit_price,
                    round_trip_cost_estimate_pct,
                    expected_funding_pct,
                    atr_pct,
                    stop_distance_pct,
                    take_profit_distance_pct,
                    exchange_stop_loss_price,
                    exchange_take_profit_price,
                    exchange_protection_multiplier_percent,
                    trailing_stop_state,
                    trailing_stop_percent,
                    trailing_stop_order_id,
                    trailing_activated_at_utc,
                    low_score_cycles,
                    leverage,
                    initial_margin_eur,
                    mark_price,
                    liquidation_price,
                    liquidation_distance_percent,
                    funding_paid_eur,
                    tp_order_state,
                    sl_order_state,
                    origin,
                    entry_channel,
                    flipped_entry)
                values (
                    @bot_instance_id,
                    @updated_at,
                    @position_index,
                    @pair,
                    @side,
                    @quantity,
                    @entry_price,
                    @entry_notional_eur,
                    @last_price,
                    @market_value_eur,
                    @unrealized_pnl_eur,
                    @unrealized_pnl_percent,
                    @opened_at_utc,
                    @last_action_at_utc,
                    @peak_pnl_percent,
                    @entry_score,
                    @exit_mode,
                    @entry_atr,
                    @stop_loss_price,
                    @take_profit_price,
                    @round_trip_cost_estimate_pct,
                    @expected_funding_pct,
                    @atr_pct,
                    @stop_distance_pct,
                    @take_profit_distance_pct,
                    @exchange_stop_loss_price,
                    @exchange_take_profit_price,
                    @exchange_protection_multiplier_percent,
                    @trailing_stop_state,
                    @trailing_stop_percent,
                    @trailing_stop_order_id,
                    @trailing_activated_at_utc,
                    @low_score_cycles,
                    @leverage,
                    @initial_margin_eur,
                    @mark_price,
                    @liquidation_price,
                    @liquidation_distance_percent,
                    @funding_paid_eur,
                    @tp_order_state,
                    @sl_order_state,
                    @origin,
                    @entry_channel,
                    @flipped_entry)
                """,
                connection,
                transaction);
            Add(command, "bot_instance_id", NpgsqlDbType.Text, botInstanceId);
            Add(command, "updated_at", NpgsqlDbType.TimestampTz, state.UpdatedAt.UtcDateTime);
            Add(command, "position_index", NpgsqlDbType.Integer, i);
            Add(command, "pair", NpgsqlDbType.Text, position.Pair);
            Add(command, "side", NpgsqlDbType.Text, position.Side);
            Add(command, "quantity", NpgsqlDbType.Numeric, position.Quantity);
            Add(command, "entry_price", NpgsqlDbType.Numeric, position.EntryPrice);
            Add(command, "entry_notional_eur", NpgsqlDbType.Numeric, position.EntryNotionalEur);
            Add(command, "last_price", NpgsqlDbType.Numeric, position.LastPrice);
            Add(command, "market_value_eur", NpgsqlDbType.Numeric, position.MarketValueEur);
            Add(command, "unrealized_pnl_eur", NpgsqlDbType.Numeric, position.UnrealizedPnlEur);
            Add(command, "unrealized_pnl_percent", NpgsqlDbType.Numeric, position.UnrealizedPnlPercent);
            Add(command, "opened_at_utc", NpgsqlDbType.TimestampTz, Utc(position.OpenedAtUtc));
            Add(command, "last_action_at_utc", NpgsqlDbType.TimestampTz, Utc(position.LastActionAtUtc));
            Add(command, "peak_pnl_percent", NpgsqlDbType.Numeric, position.PeakPnlPercent);
            Add(command, "entry_score", NpgsqlDbType.Numeric, position.EntryScore);
            Add(command, "exit_mode", NpgsqlDbType.Text, position.ExitMode);
            Add(command, "entry_atr", NpgsqlDbType.Numeric, position.EntryAtr);
            Add(command, "stop_loss_price", NpgsqlDbType.Numeric, position.StopLossPrice);
            Add(command, "take_profit_price", NpgsqlDbType.Numeric, position.TakeProfitPrice);
            Add(command, "round_trip_cost_estimate_pct", NpgsqlDbType.Numeric, position.RoundTripCostEstimatePct);
            Add(command, "expected_funding_pct", NpgsqlDbType.Numeric, position.ExpectedFundingPct);
            Add(command, "atr_pct", NpgsqlDbType.Numeric, position.AtrPct);
            Add(command, "stop_distance_pct", NpgsqlDbType.Numeric, position.StopDistancePct);
            Add(command, "take_profit_distance_pct", NpgsqlDbType.Numeric, position.TakeProfitDistancePct);
            Add(command, "exchange_stop_loss_price", NpgsqlDbType.Numeric, position.ExchangeStopLossPrice);
            Add(command, "exchange_take_profit_price", NpgsqlDbType.Numeric, position.ExchangeTakeProfitPrice);
            Add(command, "exchange_protection_multiplier_percent", NpgsqlDbType.Numeric, position.ExchangeProtectionMultiplierPercent);
            Add(command, "trailing_stop_state", NpgsqlDbType.Text, position.TrailingStopState);
            Add(command, "trailing_stop_percent", NpgsqlDbType.Numeric, position.TrailingStopPercent);
            Add(command, "trailing_stop_order_id", NpgsqlDbType.Text, position.TrailingStopOrderId);
            Add(command, "trailing_activated_at_utc", NpgsqlDbType.TimestampTz, Utc(position.TrailingActivatedAtUtc));
            Add(command, "low_score_cycles", NpgsqlDbType.Integer, position.LowScoreCycles);
            Add(command, "leverage", NpgsqlDbType.Numeric, position.Leverage);
            Add(command, "initial_margin_eur", NpgsqlDbType.Numeric, position.InitialMarginEur);
            Add(command, "mark_price", NpgsqlDbType.Numeric, position.MarkPrice);
            Add(command, "liquidation_price", NpgsqlDbType.Numeric, position.LiquidationPrice);
            Add(command, "liquidation_distance_percent", NpgsqlDbType.Numeric, position.LiquidationDistancePercent);
            Add(command, "funding_paid_eur", NpgsqlDbType.Numeric, position.FundingPaidEur);
            Add(command, "tp_order_state", NpgsqlDbType.Text, position.TpOrderState);
            Add(command, "sl_order_state", NpgsqlDbType.Text, position.SlOrderState);
            Add(command, "origin", NpgsqlDbType.Text, position.Origin);
            Add(command, "entry_channel", NpgsqlDbType.Text, position.EntryChannel);
            Add(command, "flipped_entry", NpgsqlDbType.Boolean, position.FlippedEntry);
            command.ExecuteNonQuery();
        }

        foreach (var history in state.ActionHistory)
        {
            using var command = new NpgsqlCommand(
                """
                insert into portfolio_action_history_state (
                    bot_instance_id,
                    pair,
                    last_buy_at_utc,
                    last_sell_at_utc,
                    last_stop_loss_at_utc)
                values (
                    @bot_instance_id,
                    @pair,
                    @last_buy_at_utc,
                    @last_sell_at_utc,
                    @last_stop_loss_at_utc)
                """,
                connection,
                transaction);
            Add(command, "bot_instance_id", NpgsqlDbType.Text, botInstanceId);
            Add(command, "pair", NpgsqlDbType.Text, history.Pair);
            Add(command, "last_buy_at_utc", NpgsqlDbType.TimestampTz, Utc(history.LastBuyAtUtc));
            Add(command, "last_sell_at_utc", NpgsqlDbType.TimestampTz, Utc(history.LastSellAtUtc));
            Add(command, "last_stop_loss_at_utc", NpgsqlDbType.TimestampTz, Utc(history.LastStopLossAtUtc));
            command.ExecuteNonQuery();
        }

        for (var i = 0; i < state.PendingFuturesOrders.Count; i++)
        {
            var order = state.PendingFuturesOrders[i];
            using var command = new NpgsqlCommand(
                """
                insert into pending_futures_order_state (
                    bot_instance_id,
                    order_index,
                    pair,
                    exchange_order_id,
                    created_at_utc,
                    requested_quantity,
                    submitted_limit_price)
                values (
                    @bot_instance_id,
                    @order_index,
                    @pair,
                    @exchange_order_id,
                    @created_at_utc,
                    @requested_quantity,
                    @submitted_limit_price)
                """,
                connection,
                transaction);
            Add(command, "bot_instance_id", NpgsqlDbType.Text, botInstanceId);
            Add(command, "order_index", NpgsqlDbType.Integer, i);
            Add(command, "pair", NpgsqlDbType.Text, order.Pair);
            Add(command, "exchange_order_id", NpgsqlDbType.Text, order.ExchangeOrderId);
            Add(command, "created_at_utc", NpgsqlDbType.TimestampTz, order.CreatedAtUtc.UtcDateTime);
            Add(command, "requested_quantity", NpgsqlDbType.Numeric, order.RequestedQuantity);
            Add(command, "submitted_limit_price", NpgsqlDbType.Numeric, order.SubmittedLimitPrice);
            command.ExecuteNonQuery();
        }
    }

    private void SaveNormalizedCycle(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DryRunCycleRecord record)
    {
        Execute(connection, transaction, "delete from dry_run_cycle_facts where cycle_id = @cycle_id",
            ("cycle_id", NpgsqlDbType.Text, record.CycleId));

        var wouldBuyCount = record.Decisions.Count(decision => IsBuyAction(decision.DryRunAction.Action));
        var wouldSellCount = record.Decisions.Count(decision => IsSellAction(decision.DryRunAction.Action));
        var validatedOrderCount = record.Decisions.Count(decision =>
            (decision.Broker ?? string.Empty).StartsWith("VALIDATED_OK", StringComparison.OrdinalIgnoreCase));

        using (var command = new NpgsqlCommand(
            """
            insert into dry_run_cycle_facts (
                cycle_id,
                bot_instance_id,
                bot_instance_name,
                utc,
                market_data_mode,
                ai_provider,
                worker_version,
                worker_commit,
                worker_build_utc,
                worker_image_tag,
                strategy_version,
                change_set,
                active_pairs_count,
                decisions_count,
                cash_before_eur,
                cash_after_eur,
                positions_value_before_eur,
                positions_value_after_eur,
                portfolio_value_before_eur,
                portfolio_value_after_eur,
                would_buy_count,
                would_sell_count,
                validated_order_count)
            values (
                @cycle_id,
                @bot_instance_id,
                @bot_instance_name,
                @utc,
                @market_data_mode,
                @ai_provider,
                @worker_version,
                @worker_commit,
                @worker_build_utc,
                @worker_image_tag,
                @strategy_version,
                @change_set,
                @active_pairs_count,
                @decisions_count,
                @cash_before_eur,
                @cash_after_eur,
                @positions_value_before_eur,
                @positions_value_after_eur,
                @portfolio_value_before_eur,
                @portfolio_value_after_eur,
                @would_buy_count,
                @would_sell_count,
                @validated_order_count)
            """,
            connection,
            transaction))
        {
            Add(command, "cycle_id", NpgsqlDbType.Text, record.CycleId);
            Add(command, "bot_instance_id", NpgsqlDbType.Text, botInstanceId);
            Add(command, "bot_instance_name", NpgsqlDbType.Text, record.BotInstanceName);
            Add(command, "utc", NpgsqlDbType.TimestampTz, record.Utc.UtcDateTime);
            Add(command, "market_data_mode", NpgsqlDbType.Text, record.MarketDataMode);
            Add(command, "ai_provider", NpgsqlDbType.Text, record.AiProvider);
            Add(command, "worker_version", NpgsqlDbType.Text, record.Worker.Version);
            Add(command, "worker_commit", NpgsqlDbType.Text, record.Worker.Commit);
            Add(command, "worker_build_utc", NpgsqlDbType.Text, record.Worker.BuildUtc);
            Add(command, "worker_image_tag", NpgsqlDbType.Text, record.Worker.ImageTag);
            Add(command, "strategy_version", NpgsqlDbType.Text, record.Worker.StrategyVersion);
            Add(command, "change_set", NpgsqlDbType.Text, record.Worker.ChangeSet);
            Add(command, "active_pairs_count", NpgsqlDbType.Integer, record.ActivePairs.Count);
            Add(command, "decisions_count", NpgsqlDbType.Integer, record.Decisions.Count);
            Add(command, "cash_before_eur", NpgsqlDbType.Numeric, record.PortfolioBefore.CashEur);
            Add(command, "cash_after_eur", NpgsqlDbType.Numeric, record.PortfolioAfter.CashEur);
            Add(command, "positions_value_before_eur", NpgsqlDbType.Numeric, record.PortfolioBefore.PositionsValueEur);
            Add(command, "positions_value_after_eur", NpgsqlDbType.Numeric, record.PortfolioAfter.PositionsValueEur);
            Add(command, "portfolio_value_before_eur", NpgsqlDbType.Numeric, record.PortfolioBefore.TotalValueEur);
            Add(command, "portfolio_value_after_eur", NpgsqlDbType.Numeric, record.PortfolioAfter.TotalValueEur);
            Add(command, "would_buy_count", NpgsqlDbType.Integer, wouldBuyCount);
            Add(command, "would_sell_count", NpgsqlDbType.Integer, wouldSellCount);
            Add(command, "validated_order_count", NpgsqlDbType.Integer, validatedOrderCount);
            command.ExecuteNonQuery();
        }

        for (var i = 0; i < record.ActivePairs.Count; i++)
        {
            Execute(connection, transaction,
                """
                insert into dry_run_cycle_active_pairs (cycle_id, pair_index, pair)
                values (@cycle_id, @pair_index, @pair)
                """,
                ("cycle_id", NpgsqlDbType.Text, record.CycleId),
                ("pair_index", NpgsqlDbType.Integer, i),
                ("pair", NpgsqlDbType.Text, record.ActivePairs[i]));
        }

        for (var i = 0; i < record.Decisions.Count; i++)
        {
            SaveNormalizedDecision(connection, transaction, record, i, record.Decisions[i]);
        }

        if (record.EntryDiagnostics is not null)
        {
            SaveNormalizedEntryDiagnostics(connection, transaction, record.CycleId, record.EntryDiagnostics);
        }
    }

    private void SaveNormalizedDecision(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DryRunCycleRecord record,
        int decisionIndex,
        DryRunDecisionRecord decision)
    {
        using (var command = new NpgsqlCommand(
            """
            insert into dry_run_decision_facts (
                cycle_id,
                decision_index,
                bot_instance_id,
                utc,
                pair,
                price,
                fast_ema,
                slow_ema,
                rsi,
                desired_position,
                score,
                risk_approved,
                broker,
                entry_rejection_reason,
                spread_percent,
                price_action_direction,
                price_action_trend_percent,
                exploratory,
                has_bullish_structure,
                ema_fully_confirmed,
                bullish_ema_gap_percent,
                ema_gap_velocity_percent,
                allows_short,
                has_bearish_structure,
                bearish_ema_gap_percent,
                short_score,
                long_score_threshold,
                short_score_threshold,
                minimum_ema_gap_percent,
                short_base_block_reason_code,
                short_base_block_reason,
                early_entry_eligible,
                early_entry_reason,
                early_entry_diagnostic_score,
                early_entry_suggested_notional_eur)
            values (
                @cycle_id,
                @decision_index,
                @bot_instance_id,
                @utc,
                @pair,
                @price,
                @fast_ema,
                @slow_ema,
                @rsi,
                @desired_position,
                @score,
                @risk_approved,
                @broker,
                @entry_rejection_reason,
                @spread_percent,
                @price_action_direction,
                @price_action_trend_percent,
                @exploratory,
                @has_bullish_structure,
                @ema_fully_confirmed,
                @bullish_ema_gap_percent,
                @ema_gap_velocity_percent,
                @allows_short,
                @has_bearish_structure,
                @bearish_ema_gap_percent,
                @short_score,
                @long_score_threshold,
                @short_score_threshold,
                @minimum_ema_gap_percent,
                @short_base_block_reason_code,
                @short_base_block_reason,
                @early_entry_eligible,
                @early_entry_reason,
                @early_entry_diagnostic_score,
                @early_entry_suggested_notional_eur)
            """,
            connection,
            transaction))
        {
            AddDecisionIdentity(command, record, decisionIndex);
            Add(command, "pair", NpgsqlDbType.Text, decision.Pair);
            Add(command, "price", NpgsqlDbType.Numeric, decision.Price);
            Add(command, "fast_ema", NpgsqlDbType.Numeric, decision.FastEma);
            Add(command, "slow_ema", NpgsqlDbType.Numeric, decision.SlowEma);
            Add(command, "rsi", NpgsqlDbType.Numeric, decision.Rsi);
            Add(command, "desired_position", NpgsqlDbType.Text, decision.DesiredPosition);
            Add(command, "score", NpgsqlDbType.Numeric, decision.Score);
            Add(command, "risk_approved", NpgsqlDbType.Boolean, decision.RiskApproved);
            Add(command, "broker", NpgsqlDbType.Text, decision.Broker);
            Add(command, "entry_rejection_reason", NpgsqlDbType.Text, decision.EntryRejectionReason);
            Add(command, "spread_percent", NpgsqlDbType.Numeric, decision.SpreadPercent);
            Add(command, "price_action_direction", NpgsqlDbType.Text, decision.PriceActionDirection);
            Add(command, "price_action_trend_percent", NpgsqlDbType.Numeric, decision.PriceActionTrendPercent);
            Add(command, "exploratory", NpgsqlDbType.Boolean, decision.Exploratory);
            Add(command, "has_bullish_structure", NpgsqlDbType.Boolean, decision.HasBullishStructure);
            Add(command, "ema_fully_confirmed", NpgsqlDbType.Boolean, decision.EmaFullyConfirmed);
            Add(command, "bullish_ema_gap_percent", NpgsqlDbType.Numeric, decision.BullishEmaGapPercent);
            Add(command, "ema_gap_velocity_percent", NpgsqlDbType.Numeric, decision.EmaGapVelocityPercent);
            Add(command, "allows_short", NpgsqlDbType.Boolean, decision.AllowsShort);
            Add(command, "has_bearish_structure", NpgsqlDbType.Boolean, decision.HasBearishStructure);
            Add(command, "bearish_ema_gap_percent", NpgsqlDbType.Numeric, decision.BearishEmaGapPercent);
            Add(command, "short_score", NpgsqlDbType.Numeric, decision.ShortScore);
            Add(command, "long_score_threshold", NpgsqlDbType.Numeric, decision.LongScoreThreshold);
            Add(command, "short_score_threshold", NpgsqlDbType.Numeric, decision.ShortScoreThreshold);
            Add(command, "minimum_ema_gap_percent", NpgsqlDbType.Numeric, decision.MinimumEmaGapPercent);
            Add(command, "short_base_block_reason_code", NpgsqlDbType.Text, decision.ShortBaseBlockReasonCode);
            Add(command, "short_base_block_reason", NpgsqlDbType.Text, decision.ShortBaseBlockReason);
            Add(command, "early_entry_eligible", NpgsqlDbType.Boolean, decision.EarlyEntryEligible);
            Add(command, "early_entry_reason", NpgsqlDbType.Text, decision.EarlyEntryReason);
            Add(command, "early_entry_diagnostic_score", NpgsqlDbType.Numeric, decision.EarlyEntryDiagnosticScore);
            Add(command, "early_entry_suggested_notional_eur", NpgsqlDbType.Numeric, decision.EarlyEntrySuggestedNotionalEur);
            command.ExecuteNonQuery();
        }

        for (var i = 0; i < decision.RiskReasons.Count; i++)
        {
            Execute(connection, transaction,
                """
                insert into dry_run_decision_risk_reasons (cycle_id, decision_index, reason_index, reason)
                values (@cycle_id, @decision_index, @reason_index, @reason)
                """,
                ("cycle_id", NpgsqlDbType.Text, record.CycleId),
                ("decision_index", NpgsqlDbType.Integer, decisionIndex),
                ("reason_index", NpgsqlDbType.Integer, i),
                ("reason", NpgsqlDbType.Text, decision.RiskReasons[i]));
        }

        for (var i = 0; i < decision.Contributions.Count; i++)
        {
            var contribution = decision.Contributions[i];
            Execute(connection, transaction,
                """
                insert into dry_run_signal_contributions (cycle_id, decision_index, contribution_index, name, value, reason)
                values (@cycle_id, @decision_index, @contribution_index, @name, @value, @reason)
                """,
                ("cycle_id", NpgsqlDbType.Text, record.CycleId),
                ("decision_index", NpgsqlDbType.Integer, decisionIndex),
                ("contribution_index", NpgsqlDbType.Integer, i),
                ("name", NpgsqlDbType.Text, contribution.Name),
                ("value", NpgsqlDbType.Numeric, contribution.Value),
                ("reason", NpgsqlDbType.Text, contribution.Reason));
        }

        SaveNormalizedAction(connection, transaction, record.CycleId, decisionIndex, decision.DryRunAction);
        SaveNormalizedFreshness(connection, transaction, record.CycleId, decisionIndex, decision.DryRunAction);
        SaveNormalizedLongRange(connection, transaction, record.CycleId, decisionIndex, decision.DryRunAction);
    }

    private void SaveNormalizedAction(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string cycleId,
        int decisionIndex,
        DryRunAction action)
    {
        using var command = new NpgsqlCommand(
            """
            insert into dry_run_actions (
                cycle_id,
                decision_index,
                pair,
                action,
                reason,
                hold_reason_code,
                exit_reason_code,
                desired_position,
                target_notional_eur,
                quantity,
                entry_price,
                last_price,
                fill_price,
                fee_eur,
                gross_notional_eur,
                net_notional_eur,
                cash_before_eur,
                cash_after_eur,
                portfolio_value_before_eur,
                portfolio_value_after_eur,
                fill_source,
                modeled_fill_price,
                modeled_fee_eur,
                round_trip_cost_estimate_pct,
                expected_funding_pct,
                atr_pct,
                stop_distance_pct,
                take_profit_distance_pct,
                open_risk_eur,
                queue_ahead_eur,
                maker_order_filled_eur,
                maker_fill_rate,
                time_to_fill_ms,
                repeg_count,
                funding_state,
                btc_regime_state,
                short_allowed,
                requested_notional_eur,
                filled_notional_eur,
                side,
                reduce_only,
                leverage,
                exit_trigger_source,
                entry_channel,
                exchange_order_id,
                exchange_fill_timestamp,
                requested_margin_eur,
                requested_leverage,
                actual_initial_margin_eur,
                actual_effective_leverage,
                target_risk_eur,
                sized_notional_eur,
                required_margin_eur,
                effective_leverage,
                projected_stop_loss_eur,
                execution_cost_model,
                stop_source,
                notional_cap_reason,
                range_basis,
                close_percentile,
                recent_swing_position)
            values (
                @cycle_id,
                @decision_index,
                @pair,
                @action,
                @reason,
                @hold_reason_code,
                @exit_reason_code,
                @desired_position,
                @target_notional_eur,
                @quantity,
                @entry_price,
                @last_price,
                @fill_price,
                @fee_eur,
                @gross_notional_eur,
                @net_notional_eur,
                @cash_before_eur,
                @cash_after_eur,
                @portfolio_value_before_eur,
                @portfolio_value_after_eur,
                @fill_source,
                @modeled_fill_price,
                @modeled_fee_eur,
                @round_trip_cost_estimate_pct,
                @expected_funding_pct,
                @atr_pct,
                @stop_distance_pct,
                @take_profit_distance_pct,
                @open_risk_eur,
                @queue_ahead_eur,
                @maker_order_filled_eur,
                @maker_fill_rate,
                @time_to_fill_ms,
                @repeg_count,
                @funding_state,
                @btc_regime_state,
                @short_allowed,
                @requested_notional_eur,
                @filled_notional_eur,
                @side,
                @reduce_only,
                @leverage,
                @exit_trigger_source,
                @entry_channel,
                @exchange_order_id,
                @exchange_fill_timestamp,
                @requested_margin_eur,
                @requested_leverage,
                @actual_initial_margin_eur,
                @actual_effective_leverage,
                @target_risk_eur,
                @sized_notional_eur,
                @required_margin_eur,
                @effective_leverage,
                @projected_stop_loss_eur,
                @execution_cost_model,
                @stop_source,
                @notional_cap_reason,
                @range_basis,
                @close_percentile,
                @recent_swing_position)
            """,
            connection,
            transaction);
        Add(command, "cycle_id", NpgsqlDbType.Text, cycleId);
        Add(command, "decision_index", NpgsqlDbType.Integer, decisionIndex);
        Add(command, "pair", NpgsqlDbType.Text, action.Pair);
        Add(command, "action", NpgsqlDbType.Text, action.Action);
        Add(command, "reason", NpgsqlDbType.Text, action.Reason);
        Add(command, "hold_reason_code", NpgsqlDbType.Text, action.HoldReasonCode);
        Add(command, "exit_reason_code", NpgsqlDbType.Text, action.ExitReasonCode);
        Add(command, "desired_position", NpgsqlDbType.Text, action.DesiredPosition);
        Add(command, "target_notional_eur", NpgsqlDbType.Numeric, action.TargetNotionalEur);
        Add(command, "quantity", NpgsqlDbType.Numeric, action.Quantity);
        Add(command, "entry_price", NpgsqlDbType.Numeric, action.EntryPrice);
        Add(command, "last_price", NpgsqlDbType.Numeric, action.LastPrice);
        Add(command, "fill_price", NpgsqlDbType.Numeric, action.FillPrice);
        Add(command, "fee_eur", NpgsqlDbType.Numeric, action.FeeEur);
        Add(command, "gross_notional_eur", NpgsqlDbType.Numeric, action.GrossNotionalEur);
        Add(command, "net_notional_eur", NpgsqlDbType.Numeric, action.NetNotionalEur);
        Add(command, "cash_before_eur", NpgsqlDbType.Numeric, action.CashBeforeEur);
        Add(command, "cash_after_eur", NpgsqlDbType.Numeric, action.CashAfterEur);
        Add(command, "portfolio_value_before_eur", NpgsqlDbType.Numeric, action.PortfolioValueBeforeEur);
        Add(command, "portfolio_value_after_eur", NpgsqlDbType.Numeric, action.PortfolioValueAfterEur);
        Add(command, "fill_source", NpgsqlDbType.Text, action.FillSource);
        Add(command, "modeled_fill_price", NpgsqlDbType.Numeric, action.ModeledFillPrice);
        Add(command, "modeled_fee_eur", NpgsqlDbType.Numeric, action.ModeledFeeEur);
        Add(command, "round_trip_cost_estimate_pct", NpgsqlDbType.Numeric, action.RoundTripCostEstimatePct);
        Add(command, "expected_funding_pct", NpgsqlDbType.Numeric, action.ExpectedFundingPct);
        Add(command, "atr_pct", NpgsqlDbType.Numeric, action.AtrPct);
        Add(command, "stop_distance_pct", NpgsqlDbType.Numeric, action.StopDistancePct);
        Add(command, "take_profit_distance_pct", NpgsqlDbType.Numeric, action.TakeProfitDistancePct);
        Add(command, "open_risk_eur", NpgsqlDbType.Numeric, action.OpenRiskEur);
        Add(command, "queue_ahead_eur", NpgsqlDbType.Numeric, action.QueueAheadEur);
        Add(command, "maker_order_filled_eur", NpgsqlDbType.Numeric, action.MakerOrderFilledEur);
        Add(command, "maker_fill_rate", NpgsqlDbType.Numeric, action.MakerFillRate);
        Add(command, "time_to_fill_ms", NpgsqlDbType.Bigint, action.TimeToFillMs);
        Add(command, "repeg_count", NpgsqlDbType.Integer, action.RepegCount);
        Add(command, "funding_state", NpgsqlDbType.Text, action.FundingState);
        Add(command, "btc_regime_state", NpgsqlDbType.Text, action.BtcRegimeState);
        Add(command, "short_allowed", NpgsqlDbType.Text, action.ShortAllowed);
        Add(command, "requested_notional_eur", NpgsqlDbType.Numeric, action.RequestedNotionalEur);
        Add(command, "filled_notional_eur", NpgsqlDbType.Numeric, action.FilledNotionalEur);
        Add(command, "side", NpgsqlDbType.Text, action.Side);
        Add(command, "reduce_only", NpgsqlDbType.Boolean, action.ReduceOnly);
        Add(command, "leverage", NpgsqlDbType.Numeric, action.Leverage);
        Add(command, "exit_trigger_source", NpgsqlDbType.Text, action.ExitTriggerSource);
        Add(command, "entry_channel", NpgsqlDbType.Text, action.EntryChannel);
        Add(command, "exchange_order_id", NpgsqlDbType.Text, action.ExchangeOrderId);
        Add(command, "exchange_fill_timestamp", NpgsqlDbType.TimestampTz, Utc(action.ExchangeFillTimestamp));
        Add(command, "requested_margin_eur", NpgsqlDbType.Numeric, action.RequestedMarginEur);
        Add(command, "requested_leverage", NpgsqlDbType.Numeric, action.RequestedLeverage);
        Add(command, "actual_initial_margin_eur", NpgsqlDbType.Numeric, action.ActualInitialMarginEur);
        Add(command, "actual_effective_leverage", NpgsqlDbType.Numeric, action.ActualEffectiveLeverage);
        Add(command, "target_risk_eur", NpgsqlDbType.Numeric, action.TargetRiskEur);
        Add(command, "sized_notional_eur", NpgsqlDbType.Numeric, action.SizedNotionalEur);
        Add(command, "required_margin_eur", NpgsqlDbType.Numeric, action.RequiredMarginEur);
        Add(command, "effective_leverage", NpgsqlDbType.Numeric, action.EffectiveLeverage);
        Add(command, "projected_stop_loss_eur", NpgsqlDbType.Numeric, action.ProjectedStopLossEur);
        Add(command, "execution_cost_model", NpgsqlDbType.Text, action.ExecutionCostModel);
        Add(command, "stop_source", NpgsqlDbType.Text, action.StopSource);
        Add(command, "notional_cap_reason", NpgsqlDbType.Text, action.NotionalCapReason);
        Add(command, "range_basis", NpgsqlDbType.Text, action.RangeBasis);
        Add(command, "close_percentile", NpgsqlDbType.Numeric, action.ClosePercentile);
        Add(command, "recent_swing_position", NpgsqlDbType.Numeric, action.RecentSwingPosition);
        command.ExecuteNonQuery();
    }

    private void SaveNormalizedFreshness(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string cycleId,
        int decisionIndex,
        DryRunAction action)
    {
        Execute(connection, transaction,
            """
            insert into dry_run_entry_freshness (
                cycle_id,
                decision_index,
                entry_freshness_position_in_24h_range_pct,
                entry_freshness_distance_from_recent_high_pct,
                entry_freshness_last_snapshot_step_pct,
                entry_freshness_short_snapshot_slope_pct,
                entry_freshness_positive_steps_in_last_3,
                entry_freshness_is_near_high,
                entry_freshness_has_fresh_upward_tape,
                entry_freshness_has_fresh_breakout,
                entry_freshness_block_reason,
                entry_freshness_recent_candle_momentum_pct,
                entry_distance_from_local_high_pct,
                local_high_source,
                breakout_buffer_pct,
                live_price_vs_signal_close_pct,
                post_fill_entry_distance_from_local_high_pct,
                post_fill_live_price_vs_signal_close_pct,
                signal_price,
                pre_submit_bid,
                pre_submit_ask,
                submitted_limit_price,
                requested_quantity,
                filled_quantity,
                average_fill_price,
                entry_deviation_from_signal_pct,
                entry_deviation_from_ask_pct,
                dip_bounce_min_score_applied)
            values (
                @cycle_id,
                @decision_index,
                @entry_freshness_position_in_24h_range_pct,
                @entry_freshness_distance_from_recent_high_pct,
                @entry_freshness_last_snapshot_step_pct,
                @entry_freshness_short_snapshot_slope_pct,
                @entry_freshness_positive_steps_in_last_3,
                @entry_freshness_is_near_high,
                @entry_freshness_has_fresh_upward_tape,
                @entry_freshness_has_fresh_breakout,
                @entry_freshness_block_reason,
                @entry_freshness_recent_candle_momentum_pct,
                @entry_distance_from_local_high_pct,
                @local_high_source,
                @breakout_buffer_pct,
                @live_price_vs_signal_close_pct,
                @post_fill_entry_distance_from_local_high_pct,
                @post_fill_live_price_vs_signal_close_pct,
                @signal_price,
                @pre_submit_bid,
                @pre_submit_ask,
                @submitted_limit_price,
                @requested_quantity,
                @filled_quantity,
                @average_fill_price,
                @entry_deviation_from_signal_pct,
                @entry_deviation_from_ask_pct,
                @dip_bounce_min_score_applied)
            """,
            ("cycle_id", NpgsqlDbType.Text, cycleId),
            ("decision_index", NpgsqlDbType.Integer, decisionIndex),
            ("entry_freshness_position_in_24h_range_pct", NpgsqlDbType.Numeric, action.EntryFreshnessPositionIn24hRangePct),
            ("entry_freshness_distance_from_recent_high_pct", NpgsqlDbType.Numeric, action.EntryFreshnessDistanceFromRecentHighPct),
            ("entry_freshness_last_snapshot_step_pct", NpgsqlDbType.Numeric, action.EntryFreshnessLastSnapshotStepPct),
            ("entry_freshness_short_snapshot_slope_pct", NpgsqlDbType.Numeric, action.EntryFreshnessShortSnapshotSlopePct),
            ("entry_freshness_positive_steps_in_last_3", NpgsqlDbType.Integer, action.EntryFreshnessPositiveStepsInLast3),
            ("entry_freshness_is_near_high", NpgsqlDbType.Boolean, action.EntryFreshnessIsNearHigh),
            ("entry_freshness_has_fresh_upward_tape", NpgsqlDbType.Boolean, action.EntryFreshnessHasFreshUpwardTape),
            ("entry_freshness_has_fresh_breakout", NpgsqlDbType.Boolean, action.EntryFreshnessHasFreshBreakout),
            ("entry_freshness_block_reason", NpgsqlDbType.Text, action.EntryFreshnessBlockReason),
            ("entry_freshness_recent_candle_momentum_pct", NpgsqlDbType.Numeric, action.EntryFreshnessRecentCandleMomentumPct),
            ("entry_distance_from_local_high_pct", NpgsqlDbType.Numeric, action.EntryDistanceFromLocalHighPct),
            ("local_high_source", NpgsqlDbType.Text, action.LocalHighSource),
            ("breakout_buffer_pct", NpgsqlDbType.Numeric, action.BreakoutBufferPct),
            ("live_price_vs_signal_close_pct", NpgsqlDbType.Numeric, action.LivePriceVsSignalClosePct),
            ("post_fill_entry_distance_from_local_high_pct", NpgsqlDbType.Numeric, action.PostFillEntryDistanceFromLocalHighPct),
            ("post_fill_live_price_vs_signal_close_pct", NpgsqlDbType.Numeric, action.PostFillLivePriceVsSignalClosePct),
            ("signal_price", NpgsqlDbType.Numeric, action.SignalPrice),
            ("pre_submit_bid", NpgsqlDbType.Numeric, action.PreSubmitBid),
            ("pre_submit_ask", NpgsqlDbType.Numeric, action.PreSubmitAsk),
            ("submitted_limit_price", NpgsqlDbType.Numeric, action.SubmittedLimitPrice),
            ("requested_quantity", NpgsqlDbType.Numeric, action.RequestedQuantity),
            ("filled_quantity", NpgsqlDbType.Numeric, action.FilledQuantity),
            ("average_fill_price", NpgsqlDbType.Numeric, action.AverageFillPrice),
            ("entry_deviation_from_signal_pct", NpgsqlDbType.Numeric, action.EntryDeviationFromSignalPct),
            ("entry_deviation_from_ask_pct", NpgsqlDbType.Numeric, action.EntryDeviationFromAskPct),
            ("dip_bounce_min_score_applied", NpgsqlDbType.Numeric, action.DipBounceMinScoreApplied));
    }

    private void SaveNormalizedLongRange(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string cycleId,
        int decisionIndex,
        DryRunAction action)
    {
        Execute(connection, transaction,
            """
            insert into dry_run_long_range_diagnostics (
                cycle_id,
                decision_index,
                long_range_entry_price,
                long_range_entry_price_source,
                long_range_absolute_low_24h,
                long_range_absolute_high_24h,
                long_range_robust_low_24h,
                long_range_robust_high_24h,
                long_range_24h_source,
                long_range_24h_sample_count,
                long_range_24h_position_raw,
                long_range_24h_position,
                long_range_max_position_for_long,
                long_range_distance_from_24h_low_pct,
                long_range_rising_snapshot_count,
                entry_blocked_by_24h_range,
                long_range_block_reason_code,
                btc_recent_change_pct,
                relative_strength_pct,
                zone,
                anti_chase_applied,
                confirmations_met,
                confirmations_required,
                effective_max_drift_pct,
                atr_pct)
            values (
                @cycle_id,
                @decision_index,
                @long_range_entry_price,
                @long_range_entry_price_source,
                @long_range_absolute_low_24h,
                @long_range_absolute_high_24h,
                @long_range_robust_low_24h,
                @long_range_robust_high_24h,
                @long_range_24h_source,
                @long_range_24h_sample_count,
                @long_range_24h_position_raw,
                @long_range_24h_position,
                @long_range_max_position_for_long,
                @long_range_distance_from_24h_low_pct,
                @long_range_rising_snapshot_count,
                @entry_blocked_by_24h_range,
                @long_range_block_reason_code,
                @btc_recent_change_pct,
                @relative_strength_pct,
                @zone,
                @anti_chase_applied,
                @confirmations_met,
                @confirmations_required,
                @effective_max_drift_pct,
                @atr_pct)
            """,
            ("cycle_id", NpgsqlDbType.Text, cycleId),
            ("decision_index", NpgsqlDbType.Integer, decisionIndex),
            ("long_range_entry_price", NpgsqlDbType.Numeric, action.LongRangeEntryPrice),
            ("long_range_entry_price_source", NpgsqlDbType.Text, action.LongRangeEntryPriceSource),
            ("long_range_absolute_low_24h", NpgsqlDbType.Numeric, action.LongRangeAbsoluteLow24h),
            ("long_range_absolute_high_24h", NpgsqlDbType.Numeric, action.LongRangeAbsoluteHigh24h),
            ("long_range_robust_low_24h", NpgsqlDbType.Numeric, action.LongRangeRobustLow24h),
            ("long_range_robust_high_24h", NpgsqlDbType.Numeric, action.LongRangeRobustHigh24h),
            ("long_range_24h_source", NpgsqlDbType.Text, action.LongRange24hSource),
            ("long_range_24h_sample_count", NpgsqlDbType.Integer, action.LongRange24hSampleCount),
            ("long_range_24h_position_raw", NpgsqlDbType.Numeric, action.LongRange24hPositionRaw),
            ("long_range_24h_position", NpgsqlDbType.Numeric, action.LongRange24hPosition),
            ("long_range_max_position_for_long", NpgsqlDbType.Numeric, action.LongRangeMaxPositionForLong),
            ("long_range_distance_from_24h_low_pct", NpgsqlDbType.Numeric, action.LongRangeDistanceFrom24hLowPct),
            ("long_range_rising_snapshot_count", NpgsqlDbType.Integer, action.LongRangeRisingSnapshotCount),
            ("entry_blocked_by_24h_range", NpgsqlDbType.Boolean, action.EntryBlockedBy24hRange),
            ("long_range_block_reason_code", NpgsqlDbType.Text, action.LongRangeBlockReasonCode),
            ("btc_recent_change_pct", NpgsqlDbType.Numeric, action.BtcRecentChangePct),
            ("relative_strength_pct", NpgsqlDbType.Numeric, action.RelativeStrengthPct),
            ("zone", NpgsqlDbType.Text, action.LongRangeZone),
            ("anti_chase_applied", NpgsqlDbType.Boolean, action.LongRangeAntiChaseApplied),
            ("confirmations_met", NpgsqlDbType.Integer, action.LongRangeConfirmationsMet),
            ("confirmations_required", NpgsqlDbType.Integer, action.LongRangeConfirmationsRequired),
            ("effective_max_drift_pct", NpgsqlDbType.Numeric, action.LongRangeEffectiveMaxDriftPct),
            ("atr_pct", NpgsqlDbType.Numeric, action.LongRangeAtrPct));
    }

    private void SaveNormalizedEntryDiagnostics(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string cycleId,
        CycleEntryDiagnostics diagnostics)
    {
        Execute(connection, transaction,
            """
            insert into dry_run_cycle_entry_diagnostic_facts (
                cycle_id,
                snapshot_pairs_available,
                active_pairs_evaluated,
                entry_pairs_evaluated,
                price_action_ready_count,
                score_at_least_075,
                score_at_least_080,
                score_at_least_085,
                score_at_least_090,
                hard_filter_pass_count,
                eligible_entry_candidates,
                chosen_pair,
                no_trade_reason,
                execution_mode,
                fill_rate,
                pairs_passed_spread,
                pairs_passed_volume,
                pairs_passed_depth,
                open_risk_eur,
                btc_regime_state,
                pairs_passed_exit_depth,
                funding_state)
            values (
                @cycle_id,
                @snapshot_pairs_available,
                @active_pairs_evaluated,
                @entry_pairs_evaluated,
                @price_action_ready_count,
                @score_at_least_075,
                @score_at_least_080,
                @score_at_least_085,
                @score_at_least_090,
                @hard_filter_pass_count,
                @eligible_entry_candidates,
                @chosen_pair,
                @no_trade_reason,
                @execution_mode,
                @fill_rate,
                @pairs_passed_spread,
                @pairs_passed_volume,
                @pairs_passed_depth,
                @open_risk_eur,
                @btc_regime_state,
                @pairs_passed_exit_depth,
                @funding_state)
            """,
            ("cycle_id", NpgsqlDbType.Text, cycleId),
            ("snapshot_pairs_available", NpgsqlDbType.Integer, diagnostics.SnapshotPairsAvailable),
            ("active_pairs_evaluated", NpgsqlDbType.Integer, diagnostics.ActivePairsEvaluated),
            ("entry_pairs_evaluated", NpgsqlDbType.Integer, diagnostics.EntryPairsEvaluated),
            ("price_action_ready_count", NpgsqlDbType.Integer, diagnostics.PriceActionReadyCount),
            ("score_at_least_075", NpgsqlDbType.Integer, diagnostics.ScoreAtLeast075),
            ("score_at_least_080", NpgsqlDbType.Integer, diagnostics.ScoreAtLeast080),
            ("score_at_least_085", NpgsqlDbType.Integer, diagnostics.ScoreAtLeast085),
            ("score_at_least_090", NpgsqlDbType.Integer, diagnostics.ScoreAtLeast090),
            ("hard_filter_pass_count", NpgsqlDbType.Integer, diagnostics.HardFilterPassCount),
            ("eligible_entry_candidates", NpgsqlDbType.Integer, diagnostics.EligibleEntryCandidates),
            ("chosen_pair", NpgsqlDbType.Text, diagnostics.ChosenPair),
            ("no_trade_reason", NpgsqlDbType.Text, diagnostics.NoTradeReason),
            ("execution_mode", NpgsqlDbType.Text, diagnostics.ExecutionMode),
            ("fill_rate", NpgsqlDbType.Numeric, diagnostics.FillRate),
            ("pairs_passed_spread", NpgsqlDbType.Integer, diagnostics.PairsPassedSpread),
            ("pairs_passed_volume", NpgsqlDbType.Integer, diagnostics.PairsPassedVolume),
            ("pairs_passed_depth", NpgsqlDbType.Integer, diagnostics.PairsPassedDepth),
            ("open_risk_eur", NpgsqlDbType.Numeric, diagnostics.OpenRiskEur),
            ("btc_regime_state", NpgsqlDbType.Text, diagnostics.BtcRegimeState),
            ("pairs_passed_exit_depth", NpgsqlDbType.Integer, diagnostics.PairsPassedExitDepth),
            ("funding_state", NpgsqlDbType.Text, diagnostics.FundingState));

        foreach (var item in diagnostics.RejectionCounts)
        {
            Execute(connection, transaction,
                """
                insert into dry_run_rejection_counts (cycle_id, reason, count)
                values (@cycle_id, @reason, @count)
                """,
                ("cycle_id", NpgsqlDbType.Text, cycleId),
                ("reason", NpgsqlDbType.Text, item.Key),
                ("count", NpgsqlDbType.Integer, item.Value));
        }

        for (var i = 0; i < diagnostics.TopCandidates.Count; i++)
        {
            var candidate = diagnostics.TopCandidates[i];
            Execute(connection, transaction,
                """
                insert into dry_run_top_candidates (
                    cycle_id,
                    candidate_index,
                    pair,
                    score,
                    desired_position,
                    spread_percent,
                    price,
                    bid,
                    ask,
                    has_bullish_structure,
                    ema_fully_confirmed,
                    bullish_ema_gap_percent,
                    ema_gap_velocity_percent,
                    early_entry_eligible,
                    early_entry_reason,
                    early_entry_diagnostic_score,
                    early_entry_suggested_notional_eur,
                    price_action_direction,
                    price_action_trend_percent,
                    price_action_state,
                    price_action_samples_available,
                    price_action_samples_required,
                    price_action_oldest_sample_utc,
                    price_action_newest_sample_utc,
                    hard_filters_passed,
                    quality_filters_passed,
                    rejection_reason,
                    exploratory)
                values (
                    @cycle_id,
                    @candidate_index,
                    @pair,
                    @score,
                    @desired_position,
                    @spread_percent,
                    @price,
                    @bid,
                    @ask,
                    @has_bullish_structure,
                    @ema_fully_confirmed,
                    @bullish_ema_gap_percent,
                    @ema_gap_velocity_percent,
                    @early_entry_eligible,
                    @early_entry_reason,
                    @early_entry_diagnostic_score,
                    @early_entry_suggested_notional_eur,
                    @price_action_direction,
                    @price_action_trend_percent,
                    @price_action_state,
                    @price_action_samples_available,
                    @price_action_samples_required,
                    @price_action_oldest_sample_utc,
                    @price_action_newest_sample_utc,
                    @hard_filters_passed,
                    @quality_filters_passed,
                    @rejection_reason,
                    @exploratory)
                """,
                ("cycle_id", NpgsqlDbType.Text, cycleId),
                ("candidate_index", NpgsqlDbType.Integer, i),
                ("pair", NpgsqlDbType.Text, candidate.Pair),
                ("score", NpgsqlDbType.Numeric, candidate.Score),
                ("desired_position", NpgsqlDbType.Text, candidate.DesiredPosition),
                ("spread_percent", NpgsqlDbType.Numeric, candidate.SpreadPercent),
                ("price", NpgsqlDbType.Numeric, candidate.Price),
                ("bid", NpgsqlDbType.Numeric, candidate.Bid),
                ("ask", NpgsqlDbType.Numeric, candidate.Ask),
                ("has_bullish_structure", NpgsqlDbType.Boolean, candidate.HasBullishStructure),
                ("ema_fully_confirmed", NpgsqlDbType.Boolean, candidate.EmaFullyConfirmed),
                ("bullish_ema_gap_percent", NpgsqlDbType.Numeric, candidate.BullishEmaGapPercent),
                ("ema_gap_velocity_percent", NpgsqlDbType.Numeric, candidate.EmaGapVelocityPercent),
                ("early_entry_eligible", NpgsqlDbType.Boolean, candidate.EarlyEntryEligible),
                ("early_entry_reason", NpgsqlDbType.Text, candidate.EarlyEntryReason),
                ("early_entry_diagnostic_score", NpgsqlDbType.Numeric, candidate.EarlyEntryDiagnosticScore),
                ("early_entry_suggested_notional_eur", NpgsqlDbType.Numeric, candidate.EarlyEntrySuggestedNotionalEur),
                ("price_action_direction", NpgsqlDbType.Text, candidate.PriceActionDirection),
                ("price_action_trend_percent", NpgsqlDbType.Numeric, candidate.PriceActionTrendPercent),
                ("price_action_state", NpgsqlDbType.Text, candidate.PriceActionState),
                ("price_action_samples_available", NpgsqlDbType.Integer, candidate.PriceActionSamplesAvailable),
                ("price_action_samples_required", NpgsqlDbType.Integer, candidate.PriceActionSamplesRequired),
                ("price_action_oldest_sample_utc", NpgsqlDbType.TimestampTz, Utc(candidate.PriceActionOldestSampleUtc)),
                ("price_action_newest_sample_utc", NpgsqlDbType.TimestampTz, Utc(candidate.PriceActionNewestSampleUtc)),
                ("hard_filters_passed", NpgsqlDbType.Boolean, candidate.HardFiltersPassed),
                ("quality_filters_passed", NpgsqlDbType.Boolean, candidate.QualityFiltersPassed),
                ("rejection_reason", NpgsqlDbType.Text, candidate.RejectionReason),
                ("exploratory", NpgsqlDbType.Boolean, candidate.Exploratory));

            for (var confirmationIndex = 0; confirmationIndex < candidate.MissingConfirmations.Count; confirmationIndex++)
            {
                Execute(connection, transaction,
                    """
                    insert into dry_run_top_candidate_missing_confirmations (
                        cycle_id,
                        candidate_index,
                        confirmation_index,
                        confirmation)
                    values (
                        @cycle_id,
                        @candidate_index,
                        @confirmation_index,
                        @confirmation)
                    """,
                    ("cycle_id", NpgsqlDbType.Text, cycleId),
                    ("candidate_index", NpgsqlDbType.Integer, i),
                    ("confirmation_index", NpgsqlDbType.Integer, confirmationIndex),
                    ("confirmation", NpgsqlDbType.Text, candidate.MissingConfirmations[confirmationIndex]));
            }
        }

        for (var i = 0; i < diagnostics.ExcludedPairs.Count; i++)
        {
            var excluded = diagnostics.ExcludedPairs[i];
            Execute(connection, transaction,
                """
                insert into dry_run_excluded_pairs (
                    cycle_id,
                    excluded_index,
                    pair,
                    reason,
                    last,
                    change_percent,
                    volume_rank,
                    est_24h_volume_eur,
                    spread_percent,
                    advisor_rank)
                values (
                    @cycle_id,
                    @excluded_index,
                    @pair,
                    @reason,
                    @last,
                    @change_percent,
                    @volume_rank,
                    @est_24h_volume_eur,
                    @spread_percent,
                    @advisor_rank)
                """,
                ("cycle_id", NpgsqlDbType.Text, cycleId),
                ("excluded_index", NpgsqlDbType.Integer, i),
                ("pair", NpgsqlDbType.Text, excluded.Pair),
                ("reason", NpgsqlDbType.Text, excluded.Reason),
                ("last", NpgsqlDbType.Numeric, excluded.Last),
                ("change_percent", NpgsqlDbType.Numeric, excluded.ChangePercent),
                ("volume_rank", NpgsqlDbType.Integer, excluded.VolumeRank),
                ("est_24h_volume_eur", NpgsqlDbType.Numeric, excluded.Est24hVolumeEur),
                ("spread_percent", NpgsqlDbType.Numeric, excluded.SpreadPercent),
                ("advisor_rank", NpgsqlDbType.Integer, excluded.AdvisorRank));
        }
    }

    private void AddDecisionIdentity(NpgsqlCommand command, DryRunCycleRecord record, int decisionIndex)
    {
        Add(command, "cycle_id", NpgsqlDbType.Text, record.CycleId);
        Add(command, "decision_index", NpgsqlDbType.Integer, decisionIndex);
        Add(command, "bot_instance_id", NpgsqlDbType.Text, botInstanceId);
        Add(command, "utc", NpgsqlDbType.TimestampTz, record.Utc.UtcDateTime);
    }

    private static void Execute(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, NpgsqlDbType Type, object? Value)[] parameters)
    {
        using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
        {
            Add(command, parameter.Name, parameter.Type, parameter.Value);
        }

        command.ExecuteNonQuery();
    }

    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object? value)
    {
        var parameter = command.Parameters.Add(name, type);
        parameter.Value = value ?? DBNull.Value;
    }

    private static DateTime? Utc(DateTimeOffset? value) =>
        value?.UtcDateTime;

    private static string? GetNullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static decimal? GetNullableDecimal(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);

    private static DateTimeOffset? GetNullableDateTimeOffset(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));

    private static bool IsBuyAction(string action) =>
        action is "WOULD_BUY" or "WOULD_OPEN" or "OPEN_LONG" or "OPEN_SHORT";

    private static bool IsSellAction(string action) =>
        action is "WOULD_SELL" or "WOULD_CLOSE" or "CLOSE";

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
