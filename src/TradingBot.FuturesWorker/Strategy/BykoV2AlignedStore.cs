using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace TradingBot.FuturesWorker;

internal sealed record BykoV2AlignedEvaluation(
    Guid RuleId, string Version, string Pair, string Rule, DateTimeOffset BarEnd,
    DateTimeOffset DataAsOf, int Attempt, bool? LocalVerdict, bool? DebykoVerdict,
    bool Final, string? Reason, string RawResult, string RawInputs, string ValuesJson);

internal interface IBykoV2AlignedStore
{
    bool IsFinished(string bot, Guid ruleId, string version, string pair, DateTimeOffset barEnd);
    void Record(string bot, BykoV2AlignedEvaluation evaluation);
    IReadOnlyList<BykoV2SignalNotice> PendingNotices(string bot, DateTimeOffset now);
}

internal sealed class PostgresBykoV2AlignedStore(string connectionString) : IBykoV2AlignedStore
{
    private bool _ready;
    private readonly object _schemaGate = new();
    private readonly object _retentionGate = new();
    private DateTimeOffset _nextCleanup;

    private NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        lock (_schemaGate)
        {
            if (!_ready)
            {
            using var schema = new NpgsqlCommand("""
                create table if not exists byko_v2_aligned_checks (
                    id bigserial primary key,
                    bot_instance_id text not null,
                    rule_id uuid not null,
                    version text not null,
                    pair text not null,
                    rule text not null,
                    bar_end timestamptz not null,
                    data_as_of timestamptz not null,
                    attempt integer not null,
                    bot_verdict boolean,
                    debyko_verdict boolean,
                    final boolean not null,
                    reason text,
                    raw_result jsonb not null,
                    raw_inputs jsonb not null,
                    values_jsonb jsonb not null,
                    input_hash text not null,
                    recorded_at timestamptz not null default now(),
                    unique(bot_instance_id,rule_id,version,pair,bar_end,attempt,input_hash)
                );
                create index if not exists ix_byko_v2_aligned_checks_bar
                    on byko_v2_aligned_checks(bot_instance_id,rule_id,pair,bar_end desc);
                create index if not exists ix_byko_v2_aligned_checks_retention
                    on byko_v2_aligned_checks(recorded_at);
                """, connection);
            schema.ExecuteNonQuery();
            _ready = true;
            }
        }
        return connection;
    }

    public bool IsFinished(string bot, Guid ruleId, string version, string pair, DateTimeOffset barEnd)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand("""
            select exists(select 1 from byko_v2_aligned_checks
                where bot_instance_id=@bot and rule_id=@id and version=@version and pair=@pair and bar_end=@bar and final)
            """, connection);
        command.Parameters.AddWithValue("bot", bot);
        command.Parameters.AddWithValue("id", ruleId);
        command.Parameters.AddWithValue("version", version);
        command.Parameters.AddWithValue("pair", pair);
        command.Parameters.AddWithValue("bar", barEnd.UtcDateTime);
        return command.ExecuteScalar() is true;
    }

    public void Record(string bot, BykoV2AlignedEvaluation e)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand("""
            insert into byko_v2_aligned_checks
                (bot_instance_id,rule_id,version,pair,rule,bar_end,data_as_of,attempt,
                 bot_verdict,debyko_verdict,final,reason,raw_result,raw_inputs,values_jsonb,input_hash)
            values(@bot,@id,@version,@pair,@rule,@bar,@slice,@attempt,@local,@remote,@final,@reason,
                   @result,@inputs,@values,@hash)
            on conflict do nothing
            """, connection);
        command.Parameters.AddWithValue("bot", bot);
        command.Parameters.AddWithValue("id", e.RuleId);
        command.Parameters.AddWithValue("version", e.Version);
        command.Parameters.AddWithValue("pair", e.Pair);
        command.Parameters.AddWithValue("rule", e.Rule);
        command.Parameters.AddWithValue("bar", e.BarEnd.UtcDateTime);
        command.Parameters.AddWithValue("slice", e.DataAsOf.UtcDateTime);
        command.Parameters.AddWithValue("attempt", e.Attempt);
        command.Parameters.AddWithValue("local", NpgsqlDbType.Boolean, (object?)e.LocalVerdict ?? DBNull.Value);
        command.Parameters.AddWithValue("remote", NpgsqlDbType.Boolean, (object?)e.DebykoVerdict ?? DBNull.Value);
        command.Parameters.AddWithValue("final", e.Final);
        command.Parameters.AddWithValue("reason", (object?)e.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("result", NpgsqlDbType.Jsonb, e.RawResult);
        command.Parameters.AddWithValue("inputs", NpgsqlDbType.Jsonb, e.RawInputs);
        command.Parameters.AddWithValue("values", NpgsqlDbType.Jsonb, e.ValuesJson);
        command.Parameters.AddWithValue("hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(e.RawResult + e.RawInputs))));
        command.ExecuteNonQuery();
        lock (_retentionGate)
        {
            if (DateTimeOffset.UtcNow >= _nextCleanup)
            {
                using var cleanup = new NpgsqlCommand("delete from byko_v2_aligned_checks where recorded_at < now() - interval '4 hours'", connection);
                cleanup.ExecuteNonQuery();
                _nextCleanup = DateTimeOffset.UtcNow.AddMinutes(10);
            }
        }
    }

    public IReadOnlyList<BykoV2SignalNotice> PendingNotices(string bot, DateTimeOffset now)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand("""
            select 'aligned:' || c.id,pair,rule,bar_end,bot_verdict,debyko_verdict,data_as_of,reason
            from byko_v2_aligned_checks c
            where bot_instance_id=@bot and final and recorded_at>=@since
              and (bot_verdict is true or debyko_verdict is true)
              and not exists(select 1 from byko_v2_signal_notices n
                  where n.bot_instance_id=@bot and n.notice_key='aligned:' || c.id)
            order by bar_end,id limit 30
            """, connection);
        command.Parameters.AddWithValue("bot", bot);
        command.Parameters.AddWithValue("since", now.AddMinutes(-10).UtcDateTime);
        using var reader = command.ExecuteReader();
        var notices = new List<BykoV2SignalNotice>();
        while (reader.Read())
        {
            notices.Add(new(reader.GetString(0), "aligned", reader.GetString(1), reader.GetString(2),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc)),
                reader.IsDBNull(4) ? null : reader.GetBoolean(4), false,
                reader.IsDBNull(5) ? null : reader.GetBoolean(5),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc)),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return notices;
    }
}
