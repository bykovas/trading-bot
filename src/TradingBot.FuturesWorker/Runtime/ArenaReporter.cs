using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Npgsql;
using TradingBot.Arena;

namespace TradingBot.FuturesWorker;

/// <summary>Optional reporting tasks have their own HTTP clients, DB pool and disk state. The trading worker never calls this process.</summary>
internal sealed class ArenaReporter : IDisposable
{
    private readonly Channel<bool> _snapshots = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Channel<bool> _history = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly HttpClient _arenaHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient _krakenHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly ArenaNativeSource _source;
    private readonly ArenaFeed _feed;
    private readonly string _key;
    private string? _fingerprint;
    private readonly FileStream _owner;

    public ArenaReporter(FuturesBotConfiguration config, string key)
    {
        _key = key;
        string directory = Path.Combine(config.DryRun.OutputDirectory, "arena");
        // The existing /app/data mount is durable; an explicit directory is useful for an isolated fixture/recovery.
        directory = Environment.GetEnvironmentVariable("DEBYKO_ARENA_STATE_DIRECTORY") ?? directory;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var lockOptions = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) lockOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        _owner = new FileStream(Path.Combine(directory, "reporter.lock"), lockOptions);
        _source = new(config, _krakenHttp, directory);
        _feed = new(_source, new ArenaSubmission(_arenaHttp, key), new ArenaFileStore(directory), Log);
    }
    private void Log(string message) => Console.WriteLine(message.Replace(_key, "[key]", StringComparison.Ordinal));
    private void Observe(PortfolioPosition[] held)
    {
        string fingerprint = JsonSerializer.Serialize(held.Select(p => new
        {
            p.Pair,
            p.Side,
            p.Quantity,
            p.EntryPrice,
            p.OpenedAtUtc,
            p.Origin,
            p.Leverage,
            p.DebykoFiringId,
            p.StopLossOrderId,
            p.TakeProfitOrderId,
            p.TrailingStopOrderId
        }));
        if (fingerprint == _fingerprint) return;
        _source.Observe(held);
        _fingerprint = fingerprint;
        _snapshots.Writer.TryWrite(true); _history.Writer.TryWrite(true);
    }
    public Task RunAsync(CancellationToken ct) => Task.WhenAll(
        LoopAsync("positions", _snapshots, TimeSpan.FromSeconds(60), _feed.SnapshotAsync, ct),
        LoopAsync("history", _history, TimeSpan.FromSeconds(60), _feed.ReconcileAsync, ct),
        DispatchLoopAsync(ct), ObserveLoopAsync(ct));
    private async Task LoopAsync(string name, Channel<bool> wake, TimeSpan period, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        DateTimeOffset blockedUntil = default;
        while (!ct.IsCancellationRequested)
        {
            TimeSpan delay = period;
            var remaining = blockedUntil - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                try { await Task.Delay(remaining, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            }
            try { await action(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (ArenaHttpException ex) when (ex.RetryAfter is { } after && after > TimeSpan.Zero)
            { blockedUntil = DateTimeOffset.UtcNow + after; Log($"Arena {name} reporting throttled; trading unaffected: {ex}"); }
            catch (Exception ex) { Log($"Arena {name} reporting failure (trading unaffected): {ex}"); }
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(delay);
                try { await wake.Reader.ReadAsync(wait.Token); } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* Heartbeat deadline, not shutdown. */ }
                while (wake.Reader.TryRead(out _)) { /* Coalesce wakes; the next read is current native state. */ }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }
    private async Task DispatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _feed.DispatchAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { Log($"Arena outbox reporting failure (trading unaffected): {ex}"); }
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }
    private async Task ObserveLoopAsync(CancellationToken ct)
    {
        string? lastError = null;
        while (!ct.IsCancellationRequested)
        {
            try { Observe(await _source.HeldAsync(ct)); lastError = null; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                if (ex.ToString() != lastError) { Log($"Arena position observation failure (separate process): {ex}"); lastError = ex.ToString(); }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }
    public void Dispose()
    {
        _source.Dispose(); _arenaHttp.Dispose(); _krakenHttp.Dispose(); _owner.Dispose();
    }
}

internal sealed class ArenaNativeSource : IArenaFeedSource, IDisposable
{
    private readonly FuturesBotConfiguration _config;
    private readonly HttpClient _http;
    private readonly NpgsqlDataSource _database;
    private readonly string _checkpoint;
    private readonly string _accountFile;
    private readonly string _protectionFile;
    private readonly KrakenExecutionUniverseProvider _universe;
    private PortfolioPosition[] _held = [];
    private bool _observed;
    private string? _account;
    private readonly object _accountGate = new();
    private readonly DateTimeOffset _since = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
    public ArenaNativeSource(FuturesBotConfiguration config, HttpClient http, string directory)
    {
        _config = config; _http = http; _checkpoint = Path.Combine(directory, "native-history.json");
        _accountFile = Path.Combine(directory, "native-account.txt");
        _protectionFile = Path.Combine(directory, "protection-orders.json");
        if (File.Exists(_accountFile))
        {
            if (new FileInfo(_accountFile).Length > 128) throw new InvalidDataException("Invalid reporting account checkpoint.");
            _account = File.ReadAllText(_accountFile);
            if (!Guid.TryParse(_account, out _)) throw new InvalidDataException("Invalid reporting account checkpoint.");
        }
        var connection = new NpgsqlConnectionStringBuilder(config.Database.ConnectionString) { MaxPoolSize = 2, ApplicationName = "byko-arena-reporting", CommandTimeout = 10 };
        connection.Options = (connection.Options ?? "") + " -c default_transaction_read_only=on";
        _database = NpgsqlDataSource.Create(connection.ConnectionString);
        _universe = new(http, config.Kraken, config.CandidateUniverse, config.BykoV2.Universe);
    }
    public async Task<PortfolioPosition[]> HeldAsync(CancellationToken ct)
    {
        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var query = new NpgsqlCommand("""
            SELECT pair,side,quantity,entry_price,opened_at_utc,leverage,origin,debyko_firing_id,trailing_stop_order_id
            FROM portfolio_position_state WHERE bot_instance_id=$1 ORDER BY position_index LIMIT 1001
            """, connection);
        query.Parameters.AddWithValue(_config.BotInstance.Id);
        var rows = new List<PortfolioPosition>();
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(new PortfolioPosition
        {
            Pair = reader.GetString(0),
            Side = reader.GetString(1),
            Quantity = reader.GetDecimal(2),
            EntryPrice = reader.GetDecimal(3),
            OpenedAtUtc = reader.IsDBNull(4) ? null : new DateTimeOffset(reader.GetDateTime(4)),
            Leverage = reader.IsDBNull(5) ? null : reader.GetDecimal(5),
            Origin = reader.IsDBNull(6) ? null : reader.GetString(6),
            DebykoFiringId = reader.IsDBNull(7) ? null : reader.GetString(7),
            TrailingStopOrderId = reader.IsDBNull(8) ? null : reader.GetString(8)
        });
        if (rows.Count > 1000) throw new InvalidDataException("Bot position scope exceeds bound; do not publish a partial snapshot.");
        return rows.ToArray();
    }
    public void Observe(PortfolioPosition[] held) { Volatile.Write(ref _held, held); Volatile.Write(ref _observed, true); }
    private KrakenHistoryReader Reader() => new(_http, _config.Kraken.ApiKey, _config.Kraken.ApiSecret, existingKeyReadOnly: true);
    private void Account(string account)
    {
        lock (_accountGate)
        {
            string? original = Interlocked.CompareExchange(ref _account, account, null);
            if (original is not null && original != account) throw new InvalidDataException("Kraken account changed; reporting stopped rather than mixing accounts.");
            if (!File.Exists(_accountFile))
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.WriteThrough };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using var stream = new FileStream(_accountFile, options);
                stream.Write(System.Text.Encoding.UTF8.GetBytes(account)); stream.Flush(true);
            }
        }
    }
    public async Task<JsonObject> SnapshotAsync(CancellationToken ct)
    {
        if (!Volatile.Read(ref _observed)) throw new InvalidDataException("Waiting for the first saved bot portfolio; an uninitialized source is not flat.");
        var held = Volatile.Read(ref _held);
        var observed = await Reader().CurrentOpenPositionsAsync(ct); Account(observed.AccountUid);
        var instruments = (await _universe.GetUniverseAsync(ct)).Instruments;
        if (!ReferenceEquals(held, Volatile.Read(ref _held))) throw new InvalidDataException("Position changed during native read; next change wakeup obtains a new snapshot.");
        var botHeld = held.Where(p => p.Origin?.Equals(PositionOrigins.Bot, StringComparison.OrdinalIgnoreCase) == true && p.Quantity != 0).Select(p =>
            new ArenaHeldPosition(instruments.SingleOrDefault(i => i.Pair.Equals(p.Pair, StringComparison.OrdinalIgnoreCase))?.KrakenPair
                ?? throw new InvalidDataException("No native contract mapping for held bot position."),
                p.Side.ToLowerInvariant(), p.Quantity, p.EntryPrice,
                p.OpenedAtUtc ?? throw new InvalidDataException("Missing actual opening time, not inferred from today's configuration."),
                p.Leverage ?? throw new InvalidDataException("Missing historical position leverage, not today's default."))).ToArray();
        return ArenaCurrentSnapshot.Create(observed.Body, botHeld, observed.ReceivedAt, DateTimeOffset.UtcNow);
    }
    public async Task<IReadOnlyList<JsonObject>> ClosedAsync(CancellationToken ct)
    {
        HistoryCapture? previous = null;
        if (File.Exists(_checkpoint))
        {
            if (new FileInfo(_checkpoint).Length > 80 * 1024 * 1024) throw new InvalidDataException("Native source checkpoint exceeds 80MiB; explicit recovery required.");
            await using var read = File.OpenRead(_checkpoint);
            previous = await JsonSerializer.DeserializeAsync<HistoryCapture>(read, cancellationToken: ct) ?? throw new InvalidDataException("Invalid native source checkpoint.");
            Account(previous.AccountUid);
        }
        var retainedProtection = await CaptureProtectionAsync(ct);
        var capture = await Reader().CaptureAsync(previous, ct); Account(capture.AccountUid);
        await WritePrivateAsync(_checkpoint, capture, 80 * 1024 * 1024, ct);
        foreach (var p in await CaptureProtectionAsync(ct)) retainedProtection[p.Key] = p.Value;
        var journal = await JournalAsync(ct);
        var proof = BotJournalEvidence.Read(journal, "", capture.Executions).Concat(retainedProtection.Values)
            .GroupBy(p => p.OrderId, StringComparer.Ordinal).Select(g => g.First()).ToArray();
        var facts = KrakenEvidence.Normalize(capture, _since);
        var history = PositionReconstruction.Build(facts.Fills, facts.Funding, proof.Select(p => p.Annotation).ToArray(), _since, capture.CapturedAt);
        var selected = PositionReconstruction.BotOwnedOnly(history, proof.Select(p => p.OrderId).ToHashSet(StringComparer.Ordinal));
        return selected.Included.Closed.Select(p => p.Payload()).ToArray();
    }
    private async Task<Dictionary<string, BotOrderEvidence>> CaptureProtectionAsync(CancellationToken ct)
    {
        var retained = new Dictionary<string, BotOrderEvidence>(StringComparer.Ordinal);
        if (File.Exists(_protectionFile))
        {
            if (new FileInfo(_protectionFile).Length > 8 * 1024 * 1024) throw new InvalidDataException("Protection proof checkpoint exceeds its bound.");
            await using var stream = File.OpenRead(_protectionFile);
            retained = await JsonSerializer.DeserializeAsync<Dictionary<string, BotOrderEvidence>>(stream, cancellationToken: ct)
                ?? throw new InvalidDataException("Invalid protection proof checkpoint.");
        }
        var lines = new List<string>();
        if (Directory.Exists(_config.Logging.Directory))
            foreach (string path in Directory.EnumerateFiles(_config.Logging.Directory, "worker-*.log").OrderDescending(StringComparer.Ordinal).Take(4))
            {
                // Read only a bounded tail, including while the writer has the current log open. Preserve parsed identities across rotations.
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                bool partial = file.Length > 4 * 1024 * 1024;
                if (partial) file.Seek(-4 * 1024 * 1024, SeekOrigin.End);
                using var reader = new StreamReader(file);
                if (partial) _ = await reader.ReadLineAsync(ct);
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    if (line.Contains("futures-tpsl-arm:", StringComparison.Ordinal) || line.Contains("futures-trailing-arm:", StringComparison.Ordinal) || line.Contains("futures-trailing-rollback:", StringComparison.Ordinal)) lines.Add(line);
                }
            }
        foreach (var proof in BotJournalEvidence.Read(new JsonObject(), string.Join('\n', lines))) retained[proof.OrderId] = proof;
        if (retained.Count > 10000) throw new InvalidDataException("Protection ownership checkpoint full; recover without dropping provenance.");
        await WritePrivateAsync(_protectionFile, retained, 8 * 1024 * 1024, ct);
        return retained;
    }
    private static async Task WritePrivateAsync<T>(string path, T value, int maximum, CancellationToken ct)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(temporary, options)) { await JsonSerializer.SerializeAsync(file, value, cancellationToken: ct); file.Flush(true); }
            if (new FileInfo(temporary).Length > maximum) throw new InvalidDataException("Reporting checkpoint bound reached; previous checkpoint retained.");
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private async Task<JsonObject> JournalAsync(CancellationToken ct)
    {
        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, tx)) await readOnly.ExecuteNonQueryAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT jsonb_build_object('pair',a.pair,'reason',a.reason,'exchange_order_id',a.exchange_order_id,
                'exit_reason_code',a.exit_reason_code,'leverage',a.leverage,'requested_leverage',a.requested_leverage,
                'fills',COALESCE((SELECT jsonb_agg(jsonb_build_object('order_id',f.order_id,'source',f.source))
                    FROM dry_run_action_fills f WHERE f.cycle_id=a.cycle_id AND f.decision_index=a.decision_index),'[]'::jsonb))::text
            FROM dry_run_actions a JOIN dry_run_cycles c ON c.cycle_id=a.cycle_id
            WHERE c.bot_instance_id=$1 AND (a.exchange_order_id IS NOT NULL OR a.reason LIKE '%live Kraken Futures%accepted id=%') ORDER BY c.utc,a.decision_index LIMIT 100001
            """, connection, tx);
        command.Parameters.AddWithValue(_config.BotInstance.Id);
        var actions = await ReadJournalRowsAsync(command, value => JsonNode.Parse(value), "ownership", ct);
        await using var signals = new NpgsqlCommand("SELECT firing_id FROM debyko_webhook_events WHERE bot_instance_id=$1 AND firing_id IS NOT NULL ORDER BY id LIMIT 100001", connection, tx);
        signals.Parameters.AddWithValue(_config.BotInstance.Id);
        var rows = await ReadJournalRowsAsync(signals, value => new JsonObject { ["firing_id"] = value }, "signals", ct);
        await tx.CommitAsync(ct);
        return new() { ["actions"] = actions, ["signals"] = rows };
    }
    private static async Task<JsonArray> ReadJournalRowsAsync(NpgsqlCommand command, Func<string, JsonNode?> parse, string kind, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<JsonNode>();
        for (int count = 0; await reader.ReadAsync(ct); count++)
        {
            if (count >= 100000) throw new InvalidDataException($"Arena {kind} journal exceeds its bound; do not truncate it.");
            rows.Add(parse(reader.GetString(0)) ?? throw new InvalidDataException($"Invalid {kind} journal row."));
        }
        return new JsonArray(rows.ToArray());
    }
    public void Dispose() => _database.Dispose();
}
