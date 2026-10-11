using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace TradingBot.FuturesWorker;

internal sealed record BykoV2LocalSignal(
    string Pair,
    BykoV2Rule Rule,
    DateTimeOffset BarClosedAt,
    decimal Price,
    BykoV2Verdict Verdict)
{
    public FuturesDesiredExposure Desired => Rule is BykoV2Rule.FlushLong or BykoV2Rule.BreakoutLong
        ? FuturesDesiredExposure.Long
        : FuturesDesiredExposure.Short;
}

internal interface IBykoV2VerdictStore
{
    bool IsAvailable { get; }
    bool Record(string botInstanceId, string pair, DateTimeOffset barClosedAt, BykoV2Verdict verdict, DateTimeOffset evaluatedAt, TimeSpan cooldown);
    void RecordDebykoFiring(string botInstanceId, string pair, string rule, DateTimeOffset firedAt, string? firingId, DateTimeOffset receivedAt);
    IReadOnlyList<BykoV2SignalNotice> PendingNotices(string botInstanceId, DateTimeOffset now) => Array.Empty<BykoV2SignalNotice>();
    void MarkNoticeSent(string botInstanceId, string key) { }
}

internal sealed record BykoV2SignalNotice(string Key, string Kind, string Pair, string Rule, DateTimeOffset Bar, bool? BotVerdict, bool DebykoReceived)
{
    public string Compose(string label, string emoji = "") => $"{emoji}{(Kind is "local" or "received" ? "🛜" : "ℹ️")} " + System.Net.WebUtility.HtmlEncode($"{label} · {Pair} · {Rule}\n" + (Kind switch
    {
        "local" => "Boto signalas: rastas įėjimas. Tai signalas, ne atidaryta pozicija.",
        "received" => "DEBYKO signalas gautas. Tai signalas, ne atidaryta pozicija.",
        _ => $"Palyginimas: botas {(BotVerdict is null ? "nežinoma" : BotVerdict.Value ? "+" : "−")}; DEBYKO {(DebykoReceived ? "gautas" : "negautas per 90 s")}."
    }) + $"\nUTC: {Bar:yyyy-MM-dd HH:mm:ss}");
}

internal sealed class NullBykoV2VerdictStore : IBykoV2VerdictStore
{
    public bool IsAvailable => false;
    public bool Record(string botInstanceId, string pair, DateTimeOffset barClosedAt, BykoV2Verdict verdict, DateTimeOffset evaluatedAt, TimeSpan cooldown) => false;
    public void RecordDebykoFiring(string botInstanceId, string pair, string rule, DateTimeOffset firedAt, string? firingId, DateTimeOffset receivedAt) { }
}

internal sealed class PostgresBykoV2VerdictStore(string connectionString) : IBykoV2VerdictStore
{
    private bool _schemaReady;
    public bool IsAvailable => !string.IsNullOrWhiteSpace(connectionString);

    public bool Record(
        string botInstanceId,
        string pair,
        DateTimeOffset barClosedAt,
        BykoV2Verdict verdict,
        DateTimeOffset evaluatedAt,
        TimeSpan cooldown)
    {
        EnsureSchema();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var insert = new NpgsqlCommand(
            """
            insert into byko_v2_shadow_verdicts (
                bot_instance_id, pair, rule, bar_closed_at, bot_verdict, bot_reason,
                values_jsonb, bot_evaluated_at, fired_edge)
            values (
                @bot, @pair, @rule, @bar, @verdict, @reason,
                @values, @evaluated, false)
            on conflict (bot_instance_id, pair, rule, bar_closed_at) do nothing
            returning id
            """,
            connection,
            transaction);
        insert.Parameters.AddWithValue("bot", botInstanceId);
        insert.Parameters.AddWithValue("pair", pair);
        insert.Parameters.AddWithValue("rule", RuleName(verdict.Rule));
        insert.Parameters.AddWithValue("bar", barClosedAt.UtcDateTime);
        insert.Parameters.AddWithValue("verdict", (object?)verdict.Fired ?? DBNull.Value);
        insert.Parameters.AddWithValue("reason", verdict.Reason);
        insert.Parameters.Add("values", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(verdict.Values);
        insert.Parameters.AddWithValue("evaluated", evaluatedAt.UtcDateTime);
        var insertedId = insert.ExecuteScalar();
        if (insertedId is null)
        {
            transaction.Rollback();
            return false;
        }

        var verdictId = Convert.ToInt64(insertedId, System.Globalization.CultureInfo.InvariantCulture);
        AttachPendingDebykoFiring(
            connection,
            transaction,
            verdictId,
            botInstanceId,
            pair,
            RuleName(verdict.Rule),
            barClosedAt,
            verdict.Rule is BykoV2Rule.BreakoutLong or BykoV2Rule.BreakoutShort ? 15 : 1);

        bool? previous = null;
        DateTimeOffset? lastFired = null;
        using (var read = new NpgsqlCommand(
            """
            select last_verdict, last_fired_at
            from byko_v2_rule_state
            where bot_instance_id = @bot and pair = @pair and rule = @rule
            for update
            """,
            connection,
            transaction))
        {
            read.Parameters.AddWithValue("bot", botInstanceId);
            read.Parameters.AddWithValue("pair", pair);
            read.Parameters.AddWithValue("rule", RuleName(verdict.Rule));
            using var reader = read.ExecuteReader();
            if (reader.Read())
            {
                previous = reader.IsDBNull(0) ? null : reader.GetBoolean(0);
                lastFired = reader.IsDBNull(1)
                    ? null
                    : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc));
            }
        }

        var edge = verdict.Fired == true
            && previous != true
            && (lastFired is null || evaluatedAt - lastFired >= cooldown);
        using (var updateJournal = new NpgsqlCommand(
            """
            update byko_v2_shadow_verdicts
            set fired_edge = @edge
            where bot_instance_id = @bot and pair = @pair and rule = @rule and bar_closed_at = @bar
            """,
            connection,
            transaction))
        {
            updateJournal.Parameters.AddWithValue("edge", edge);
            updateJournal.Parameters.AddWithValue("bot", botInstanceId);
            updateJournal.Parameters.AddWithValue("pair", pair);
            updateJournal.Parameters.AddWithValue("rule", RuleName(verdict.Rule));
            updateJournal.Parameters.AddWithValue("bar", barClosedAt.UtcDateTime);
            updateJournal.ExecuteNonQuery();
        }

        var nextVerdict = verdict.Fired ?? previous;
        using (var upsertState = new NpgsqlCommand(
            """
            insert into byko_v2_rule_state (
                bot_instance_id, pair, rule, last_bar_closed_at, last_verdict, last_fired_at, updated_at)
            values (
                @bot, @pair, @rule, @bar, @verdict, @last_fired, @updated)
            on conflict (bot_instance_id, pair, rule) do update set
                last_bar_closed_at = excluded.last_bar_closed_at,
                last_verdict = excluded.last_verdict,
                last_fired_at = excluded.last_fired_at,
                updated_at = excluded.updated_at
            """,
            connection,
            transaction))
        {
            upsertState.Parameters.AddWithValue("bot", botInstanceId);
            upsertState.Parameters.AddWithValue("pair", pair);
            upsertState.Parameters.AddWithValue("rule", RuleName(verdict.Rule));
            upsertState.Parameters.AddWithValue("bar", barClosedAt.UtcDateTime);
            upsertState.Parameters.AddWithValue("verdict", (object?)nextVerdict ?? DBNull.Value);
            upsertState.Parameters.AddWithValue("last_fired", (object?)(edge ? evaluatedAt.UtcDateTime : lastFired?.UtcDateTime) ?? DBNull.Value);
            upsertState.Parameters.AddWithValue("updated", evaluatedAt.UtcDateTime);
            upsertState.ExecuteNonQuery();
        }

        transaction.Commit();
        return edge;
    }

    public void RecordDebykoFiring(
        string botInstanceId,
        string pair,
        string rule,
        DateTimeOffset firedAt,
        string? firingId,
        DateTimeOffset receivedAt)
    {
        EnsureSchema();
        var timeframe = rule.Contains("BREAKOUT", StringComparison.OrdinalIgnoreCase) ? 15 : 1;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var normalizedRule = rule.Trim().ToUpperInvariant();
        var firingKey = string.IsNullOrWhiteSpace(firingId)
            ? $"{normalizedRule}:{pair}:{firedAt:O}"
            : firingId.Trim();
        using (var insert = new NpgsqlCommand(
            """
            insert into byko_v2_debyko_firings (
                bot_instance_id, pair, rule, firing_key, fired_at, received_at)
            values (@bot, @pair, @rule, @firing_key, @fired_at, @received_at)
            on conflict (bot_instance_id, firing_key) do update set
                received_at = excluded.received_at
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("bot", botInstanceId);
            insert.Parameters.AddWithValue("pair", pair);
            insert.Parameters.AddWithValue("rule", normalizedRule);
            insert.Parameters.AddWithValue("firing_key", firingKey);
            insert.Parameters.AddWithValue("fired_at", firedAt.UtcDateTime);
            insert.Parameters.AddWithValue("received_at", receivedAt.UtcDateTime);
            insert.ExecuteNonQuery();
        }

        using var command = new NpgsqlCommand(
            """
            update byko_v2_shadow_verdicts
            set debyko_fired = true,
                debyko_firing_id = @firing_id,
                debyko_fired_at = @fired_at,
                webhook_received_at = @received_at
            where id = (
                select id
                from byko_v2_shadow_verdicts
                where bot_instance_id = @bot and pair = @pair and rule = @rule
                  and bar_closed_at <= @fired_at
                  and bar_closed_at >= @fired_at - @lookback
                order by bar_closed_at desc
                limit 1
            )
            returning id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("bot", botInstanceId);
        command.Parameters.AddWithValue("pair", pair);
        command.Parameters.AddWithValue("rule", normalizedRule);
        command.Parameters.AddWithValue("firing_id", firingKey);
        command.Parameters.AddWithValue("fired_at", firedAt.UtcDateTime);
        command.Parameters.AddWithValue("received_at", receivedAt.UtcDateTime);
        command.Parameters.Add("lookback", NpgsqlDbType.Interval).Value = TimeSpan.FromMinutes(timeframe + 1);
        var shadowId = command.ExecuteScalar();
        if (shadowId is not null)
        {
            using var link = new NpgsqlCommand(
                """
                update byko_v2_debyko_firings
                set shadow_verdict_id = @shadow_id
                where bot_instance_id = @bot and firing_key = @firing_key
                """,
                connection,
                transaction);
            link.Parameters.AddWithValue("shadow_id", Convert.ToInt64(shadowId, System.Globalization.CultureInfo.InvariantCulture));
            link.Parameters.AddWithValue("bot", botInstanceId);
            link.Parameters.AddWithValue("firing_key", firingKey);
            link.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void AttachPendingDebykoFiring(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long verdictId,
        string botInstanceId,
        string pair,
        string rule,
        DateTimeOffset barClosedAt,
        int timeframeMinutes)
    {
        using var command = new NpgsqlCommand(
            """
            with candidate as (
                select id, firing_key, fired_at, received_at
                from byko_v2_debyko_firings
                where bot_instance_id = @bot and pair = @pair and rule = @rule
                  and shadow_verdict_id is null
                  and fired_at >= @bar
                  and fired_at <= @bar + @lookback
                order by fired_at asc
                limit 1
                for update
            ), linked as (
                update byko_v2_debyko_firings firing
                set shadow_verdict_id = @verdict_id
                from candidate
                where firing.id = candidate.id
                returning candidate.firing_key, candidate.fired_at, candidate.received_at
            )
            update byko_v2_shadow_verdicts verdict
            set debyko_fired = true,
                debyko_firing_id = linked.firing_key,
                debyko_fired_at = linked.fired_at,
                webhook_received_at = linked.received_at
            from linked
            where verdict.id = @verdict_id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("verdict_id", verdictId);
        command.Parameters.AddWithValue("bot", botInstanceId);
        command.Parameters.AddWithValue("pair", pair);
        command.Parameters.AddWithValue("rule", rule);
        command.Parameters.AddWithValue("bar", barClosedAt.UtcDateTime);
        command.Parameters.Add("lookback", NpgsqlDbType.Interval).Value = TimeSpan.FromMinutes(timeframeMinutes + 1);
        command.ExecuteNonQuery();
    }

    private void EnsureSchema()
    {
        if (_schemaReady)
        {
            return;
        }

        using var connection = Open();
        using var command = new NpgsqlCommand(
            """
            create table if not exists byko_v2_shadow_verdicts (
                id bigserial primary key,
                bot_instance_id text not null,
                pair text not null,
                rule text not null,
                bar_closed_at timestamptz not null,
                bot_verdict boolean,
                bot_reason text not null,
                values_jsonb jsonb not null,
                bot_evaluated_at timestamptz not null,
                fired_edge boolean not null default false,
                debyko_fired boolean not null default false,
                debyko_firing_id text,
                debyko_fired_at timestamptz,
                webhook_received_at timestamptz,
                unique (bot_instance_id, pair, rule, bar_closed_at)
            );
            create index if not exists ix_byko_v2_shadow_verdicts_recent
                on byko_v2_shadow_verdicts (bot_instance_id, bar_closed_at desc);

            create table if not exists byko_v2_signal_notices (
                bot_instance_id text not null,
                notice_key text not null,
                sent_at timestamptz not null default now(),
                primary key (bot_instance_id, notice_key)
            );

            create table if not exists byko_v2_debyko_firings (
                id bigserial primary key,
                bot_instance_id text not null,
                pair text not null,
                rule text not null,
                firing_key text not null,
                fired_at timestamptz not null,
                received_at timestamptz not null,
                shadow_verdict_id bigint references byko_v2_shadow_verdicts(id) on delete set null,
                unique (bot_instance_id, firing_key)
            );
            create index if not exists ix_byko_v2_debyko_firings_match
                on byko_v2_debyko_firings (bot_instance_id, pair, rule, fired_at desc);

            create table if not exists byko_v2_rule_state (
                bot_instance_id text not null,
                pair text not null,
                rule text not null,
                last_bar_closed_at timestamptz not null,
                last_verdict boolean,
                last_fired_at timestamptz,
                updated_at timestamptz not null,
                primary key (bot_instance_id, pair, rule)
            );
            """,
            connection);
        command.ExecuteNonQuery();
        _schemaReady = true;
    }

    private NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        return connection;
    }

    public IReadOnlyList<BykoV2SignalNotice> PendingNotices(string botInstanceId, DateTimeOffset now)
    {
        EnsureSchema();
        using var connection = Open();
        using var command = new NpgsqlCommand(
            """
            with candidates as (
                select 'local:' || id as key, 'local' as kind, pair, rule, bar_closed_at as bar,
                       bot_verdict, debyko_fired
                from byko_v2_shadow_verdicts
                where bot_instance_id = @bot and fired_edge and bot_evaluated_at >= @since
                union all
                select 'received:' || id, 'received', pair, rule, fired_at, null::boolean, true
                from byko_v2_debyko_firings
                where bot_instance_id = @bot and received_at >= @since
                union all
                select 'comparison:' || id, 'comparison', pair, rule, bar_closed_at, bot_verdict, debyko_fired
                from byko_v2_shadow_verdicts
                where bot_instance_id = @bot and (fired_edge or debyko_fired)
                  and bot_evaluated_at >= @since
                  and (debyko_fired or bar_closed_at <= @deadline)
                union all
                select 'unmatched:' || id, 'comparison', pair, rule, fired_at, null::boolean, true
                from byko_v2_debyko_firings
                where bot_instance_id = @bot and shadow_verdict_id is null
                  and received_at >= @since and received_at <= @deadline
            )
            select key, kind, pair, rule, bar, bot_verdict, debyko_fired from candidates c
            where not exists (
                select 1 from byko_v2_signal_notices n where n.bot_instance_id = @bot and n.notice_key = c.key
            )
            order by bar, case kind when 'local' then 0 when 'received' then 1 else 2 end, key
            limit 30
            """, connection);
        command.Parameters.AddWithValue("bot", botInstanceId);
        command.Parameters.AddWithValue("since", now.AddMinutes(-10).UtcDateTime);
        command.Parameters.AddWithValue("deadline", now.AddSeconds(-90).UtcDateTime);
        using var reader = command.ExecuteReader();
        var notices = new List<BykoV2SignalNotice>();
        while (reader.Read())
        {
            notices.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc)),
                reader.IsDBNull(5) ? null : reader.GetBoolean(5), reader.GetBoolean(6)));
        }
        return notices;
    }

    public void MarkNoticeSent(string botInstanceId, string key)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand(
            "insert into byko_v2_signal_notices (bot_instance_id, notice_key) values (@bot, @key) on conflict do nothing", connection);
        command.Parameters.AddWithValue("bot", botInstanceId);
        command.Parameters.AddWithValue("key", key);
        command.ExecuteNonQuery();
    }

    internal static string RuleName(BykoV2Rule rule) => rule switch
    {
        BykoV2Rule.FlushLong => "BYKO_FLUSH_LONG",
        BykoV2Rule.FlushShort => "BYKO_FLUSH_SHORT",
        BykoV2Rule.BreakoutLong => "BYKO_BREAKOUT_LONG",
        BykoV2Rule.BreakoutShort => "BYKO_BREAKOUT_SHORT",
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, null)
    };
}

internal sealed class BykoV2LocalSignalRunner(
    FuturesBotConfiguration config,
    IMarketDataStore marketDataStore,
    IBykoV2VerdictStore verdictStore,
    IClock? clock = null)
{
    private readonly IClock _clock = clock ?? SystemClock.Instance;

    public IReadOnlyList<BykoV2LocalSignal> EvaluateLatest()
    {
        if (!config.BykoV2.Enabled || !config.BykoV2.LocalEvaluationEnabled || !verdictStore.IsAvailable)
        {
            return Array.Empty<BykoV2LocalSignal>();
        }

        var now = _clock.UtcNow;
        var quotes = marketDataStore.LoadQuotes(MarketDataVenue.Futures, now.AddMinutes(-2))
            .ToDictionary(quote => quote.Pair, StringComparer.OrdinalIgnoreCase);
        var frames = new Dictionary<string, BykoV2MarketFrame>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in config.BykoV2.Universe)
        {
            quotes.TryGetValue(pair, out var quote);
            frames[pair] = LoadFrame(pair, quote);
        }

        if (!frames.TryGetValue("XBT/USD", out var btc))
        {
            return Array.Empty<BykoV2LocalSignal>();
        }

        var signals = new List<BykoV2LocalSignal>();
        foreach (var pair in config.BykoV2.Universe.OrderBy(value => value, StringComparer.Ordinal))
        {
            var frame = frames[pair];
            foreach (var rule in Enum.GetValues<BykoV2Rule>())
            {
                var candles = rule is BykoV2Rule.FlushLong or BykoV2Rule.FlushShort
                    ? frame.OneMinute
                    : frame.FifteenMinute;
                var timeframe = rule is BykoV2Rule.FlushLong or BykoV2Rule.FlushShort ? 1 : 15;
                if (candles.Count == 0)
                {
                    continue;
                }

                var barClosedAt = candles[^1].OpenTime.AddMinutes(timeframe);
                if (now < barClosedAt.AddSeconds(config.BykoV2.EvaluationDelaySeconds)
                    || now - barClosedAt > TimeSpan.FromMinutes(timeframe + 2))
                {
                    continue;
                }

                var verdict = BykoV2Strategy.Evaluate(rule, frame, btc, config.BykoV2);
                var cooldown = TimeSpan.FromMinutes(
                    timeframe == 1 ? config.BykoV2.FlushCooldownMinutes : config.BykoV2.BreakoutCooldownMinutes);
                var edge = verdictStore.Record(
                    config.BotInstance.Id,
                    pair,
                    barClosedAt,
                    verdict,
                    now,
                    cooldown);
                if (edge && frame.Bid is > 0m && frame.Ask is > 0m)
                {
                    var price = rule is BykoV2Rule.FlushLong or BykoV2Rule.BreakoutLong
                        ? frame.Ask.Value
                        : frame.Bid.Value;
                    signals.Add(new BykoV2LocalSignal(pair, rule, barClosedAt, price, verdict));
                }
            }
        }

        return signals;
    }

    private BykoV2MarketFrame LoadFrame(string pair, SharedMarketQuoteRecord? quote)
    {
        var oneMinute = LoadCandles(pair, 1, 80);
        var fiveMinute = LoadCandles(pair, 5, 20);
        var fifteenMinute = LoadCandles(pair, 15, 80);
        var oneHour = LoadCandles(pair, 60, 240);
        BykoV2LiquidationWindow? liquidation = null;
        if (oneMinute.Count > 0)
        {
            var end = oneMinute[^1].OpenTime.AddMinutes(1);
            var value = marketDataStore.LoadLiquidationWindow(
                MarketDataVenue.Futures,
                pair,
                end.AddMinutes(-config.BykoV2.FlushWindowMinutes),
                end);
            liquidation = new BykoV2LiquidationWindow(value.LongValueUsd, value.ShortValueUsd, value.Complete);
        }

        return new BykoV2MarketFrame(
            pair,
            oneMinute,
            fiveMinute,
            fifteenMinute,
            oneHour,
            quote?.Bid,
            quote?.Ask,
            quote?.Volume24h,
            quote?.FundingRatePercent / 100m * 8m,
            liquidation);
    }

    private IReadOnlyList<Candle> LoadCandles(string pair, int timeframeMinutes, int maxBars) =>
        marketDataStore.LoadStrategyCandles(MarketDataVenue.Futures, pair, timeframeMinutes, maxBars)
            .Select(value => new Candle(
                value.OpenTime,
                value.Open,
                value.High,
                value.Low,
                value.Close,
                value.Volume,
                0))
            .ToList();
}
