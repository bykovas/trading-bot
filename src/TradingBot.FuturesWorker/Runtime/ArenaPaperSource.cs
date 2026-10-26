using System.Globalization;
using System.Text.Json.Nodes;
using Npgsql;
using TradingBot.Arena;

namespace TradingBot.FuturesWorker;

/// <summary>Read-only modeled executions. No Kraken private reader or broker is constructed in paper mode.</summary>
internal sealed class ArenaPaperSource : IArenaObservedSource
{
    private readonly NpgsqlDataSource _database;
    private readonly KrakenExecutionUniverseProvider _universe;
    private readonly string _instance;
    private readonly DateTimeOffset _since;

    public ArenaPaperSource(FuturesBotConfiguration config, HttpClient publicHttp, DateTimeOffset since)
    {
        if (config.Futures.LiveTradingEnabled || !config.Futures.PaperTradingEnabled)
            throw new InvalidOperationException("Paper reporting requires paper execution, never a live account.");
        _instance = config.BotInstance.Id;
        _since = since;
        var connection = new NpgsqlConnectionStringBuilder(config.Database.ConnectionString)
        {
            MaxPoolSize = 2, ApplicationName = "byko-arena-paper-reporting", CommandTimeout = 10
        };
        connection.Options = (connection.Options ?? "") + " -c default_transaction_read_only=on";
        _database = NpgsqlDataSource.Create(connection.ConnectionString);
        _universe = new(publicHttp, config.Kraken, config.CandidateUniverse, config.BykoV2.Universe);
    }

    public static DateTimeOffset ParseSince(string? value)
    {
        if (value is null || !value.EndsWith('Z') || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var since) || since.Year < 1970 || since > DateTimeOffset.UtcNow)
            throw new InvalidDataException("Set ARENA_PAPER_SINCE_UTC to the explicit UTC paper-mode start (ending Z); do not import earlier history implicitly.");
        return since;
    }

    public async Task<PortfolioPosition[]> HeldAsync(CancellationToken ct)
    {
        await using var connection = await _database.OpenConnectionAsync(ct);
        return await ArenaNativeSource.ReadHeldAsync(connection, null, _instance, ct);
    }
    public void Observe(PortfolioPosition[] held) { /* Wake only; snapshots read holdings and fills in one transaction. */ }
    public async Task<JsonObject> SnapshotAsync(CancellationToken ct) => (await ReadAsync(true, ct)).Snapshot();
    public async Task<IReadOnlyList<JsonObject>> ClosedAsync(CancellationToken ct) => (await ReadAsync(false, ct)).Closed;

    private async Task<ArenaPaperHistory> ReadAsync(bool snapshot, CancellationToken ct)
    {
        var instruments = (await _universe.GetUniverseAsync(ct)).Instruments;
        string Symbol(string pair) => instruments.SingleOrDefault(i => i.Pair.Equals(pair, StringComparison.OrdinalIgnoreCase))?.KrakenPair
            ?? throw new InvalidDataException("Missing native contract mapping for paper execution; do not invent a symbol.");
        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        DateTimeOffset updated;
        await using (var summary = new NpgsqlCommand("SELECT updated_at,cash_quote_currency FROM portfolio_state_summary WHERE bot_instance_id=$1", connection, tx))
        {
            summary.Parameters.AddWithValue(_instance);
            await using var reader = await summary.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || reader.IsDBNull(1) || reader.GetString(1) != "USD")
                throw new InvalidDataException("Missing USD paper portfolio; not a flat snapshot.");
            updated = new(reader.GetDateTime(0));
        }
        var held = snapshot ? await ArenaNativeSource.ReadHeldAsync(connection, tx, _instance, ct) : [];
        var fills = new List<ArenaPaperFill>();
        // Require explicit modeled fill evidence on the action. Live rows and unexecuted WOULD_* decisions are not paper fills.
        await using (var command = new NpgsqlCommand("""
            SELECT c.cycle_id,a.decision_index,a.pair,a.side,a.action,f.occurred_at,f.quantity,f.price,
                a.entry_price,f.fee_eur,a.leverage,a.actual_initial_margin_eur,f.source,
                COALESCE(a.exchange_order_id,f.order_id),a.exit_reason_code,
                (SELECT count(*) FROM dry_run_action_fills other WHERE other.cycle_id=a.cycle_id AND other.decision_index=a.decision_index)
            FROM dry_run_cycles c JOIN dry_run_actions a ON a.cycle_id=c.cycle_id
            JOIN dry_run_action_fills f ON f.cycle_id=a.cycle_id AND f.decision_index=a.decision_index
            WHERE c.bot_instance_id=$1 AND c.utc >= $2 AND f.occurred_at >= $2
                AND a.action IN ('WOULD_OPEN_LONG','WOULD_OPEN_SHORT','WOULD_CLOSE') AND f.source='MODELED_PORTFOLIO'
            ORDER BY f.occurred_at,c.utc,c.cycle_id,a.decision_index,f.fill_index LIMIT 20001
            """, connection, tx))
        {
            command.Parameters.AddWithValue(_instance);
            command.Parameters.AddWithValue(_since.UtcDateTime);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (fills.Count >= 20000 || reader.GetInt64(15) != 1)
                    throw new InvalidDataException("Paper journal bound or unsupported multi-fill action; do not truncate/approximate.");
                fills.Add(new(reader.GetString(0) + ":" + reader.GetInt32(1), Symbol(reader.GetString(2)), reader.GetString(3).ToLowerInvariant(),
                    reader.GetString(4) == "WOULD_CLOSE", new(reader.GetDateTime(5)), reader.GetDecimal(6), reader.GetDecimal(7),
                    reader.GetDecimal(8), reader.GetDecimal(9), reader.GetDecimal(10), reader.IsDBNull(11) ? null : reader.GetDecimal(11),
                    reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14)));
            }
        }
        await tx.CommitAsync(ct);
        var history = ArenaPaperReconstruction.Build(_instance, fills, DateTimeOffset.UtcNow);
        if (snapshot)
        {
            ArenaPaperReconstruction.CheckHoldings(history, held.Select(p => new ArenaHeldPosition(Symbol(p.Pair), p.Side.ToLowerInvariant(),
                p.Quantity, p.EntryPrice, p.OpenedAtUtc ?? throw new InvalidDataException("Missing paper opening time."),
                p.Leverage ?? throw new InvalidDataException("Missing paper leverage."))).ToArray());
            // A flat DEBYKO-only worker intentionally does not rewrite its unchanged portfolio.
            // Open positions need a fresh worker valuation; a verified flat paper ledger does not.
            if (held.Length > 0) ArenaPaperReconstruction.CheckFresh(updated, DateTimeOffset.UtcNow);
        }
        return history;
    }
    public void Dispose() => _database.Dispose();
}
