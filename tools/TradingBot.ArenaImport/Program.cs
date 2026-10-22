using System.Text.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using TradingBot.Arena;

if (args.Length == 3 && args[0] == "replay-trades")
{
    string dir = PrivateFiles.DirectoryOutsideRepository(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
    var plan = JsonSerializer.Deserialize<ArenaImportPlan>(await File.ReadAllTextAsync(args[1])) ?? throw new InvalidDataException("Missing approved plan.");
    var contract = new ArenaContract(await File.ReadAllTextAsync(Path.Combine(dir, "openapi.json")));
    var registry = JsonSerializer.Deserialize<ArenaRegistryListing[]>(await File.ReadAllTextAsync(Path.Combine(dir, "registry.json")))!;
    if (args[2] != plan.Digest || contract.SchemaHash != plan.SchemaHash || ArenaSubmission.RegistryHash(registry) != plan.RegistryHash)
        throw new InvalidDataException("Approved identity/contract digest changed; no write.");
    var errors = contract.Validate(plan.Profile, plan.Trades, plan.Positions, registry, DateTimeOffset.UtcNow);
    if (errors.Count != 0) throw new InvalidDataException(string.Join("; ", errors));
    _ = ArenaContract.Batches(plan.Trades); // Validate all batches before the first HTTP write.
    string key = Environment.GetEnvironmentVariable("DEBYKO_ARENA_KEY") is { Length: > 0 } value ? value : throw new InvalidOperationException("Protected DEBYKO_ARENA_KEY is required.");
    using var replayHttp = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    var client = new ArenaSubmission(replayHttp, key);
    int total = 0;
    foreach (byte[] batch in ArenaContract.Batches(plan.Trades))
    {
        var rows = JsonNode.Parse(batch)!.AsArray().OfType<JsonObject>().ToArray();
        var receipt = await client.TradesAsync(rows, default);
        await using (var file = PrivateFiles.CreateNew(Path.Combine(dir, "replay-" + Guid.NewGuid().ToString("N") + ".json")))
            await JsonSerializer.SerializeAsync(file, new { PlanDigest = plan.Digest, Receipt = receipt });
        if (receipt.Status != 200 || receipt.Accepted != 0 || receipt.Updated != 0) throw new InvalidDataException("Imported-history replay was not a no-op; stop and inspect the durable receipt.");
        total += receipt.Submitted;
        Console.WriteLine($"Arena closed-history replay: HTTP200 submitted={receipt.Submitted} accepted=0 updated=0");
    }
    Console.WriteLine($"Verified {total} imported native trade IDs; profile and positions were not sent. No worker activation.");
    return 0;
}
if (args.Length == 4 && args[0] == "send")
{
    string dir = PrivateFiles.DirectoryOutsideRepository(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
    var plan = JsonSerializer.Deserialize<ArenaImportPlan>(await File.ReadAllTextAsync(args[1])) ?? throw new InvalidDataException("Missing prepared plan.");
    var contract = new ArenaContract(await File.ReadAllTextAsync(Path.Combine(dir, "openapi.json")));
    var registry = JsonSerializer.Deserialize<ArenaRegistryListing[]>(await File.ReadAllTextAsync(Path.Combine(dir, "registry.json")))!;
    string key = Environment.GetEnvironmentVariable("DEBYKO_ARENA_KEY") is { Length: > 0 } value ? value : throw new InvalidOperationException("DEBYKO_ARENA_KEY must come from the protected environment.");
    using var arenaHttp = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    var client = new ArenaSubmission(arenaHttp, key);
    Task<DateTimeOffset> Verify(JsonObject expected, CancellationToken ct)
    {
        PrivateFiles.DirectoryOutsideRepository(Path.GetDirectoryName(Path.GetFullPath(args[3]))!);
        var native = JsonNode.Parse(File.ReadAllText(args[3]))!.AsObject();
        var original = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "capture.json")))!.AsObject();
        if (native["account_uid"]?.ToString() != original["AccountUid"]?.ToString()) throw new InvalidDataException("Fresh snapshot belongs to a different Kraken account.");
        ArenaSnapshotCheck.Matches(native["body"]!.AsObject(), expected);
        return Task.FromResult(DateTimeOffset.Parse(native["received_at"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture));
    }
    async Task Checkpoint(ArenaReceipt receipt, CancellationToken ct)
    {
        string file = Path.Combine(dir, "receipt-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "-" + Guid.NewGuid().ToString("N") + ".json");
        await using var stream = PrivateFiles.CreateNew(file); await JsonSerializer.SerializeAsync(stream, new { PlanDigest = plan.Digest, Receipt = receipt }, cancellationToken: ct);
        Console.WriteLine($"Arena {receipt.Path}: HTTP {receipt.Status}, submitted={receipt.Submitted}, accepted={receipt.Accepted}, updated={receipt.Updated}");
    }
    bool profileRecorded = Directory.EnumerateFiles(dir, "receipt-*.json").Any(file =>
    {
        var saved = JsonNode.Parse(File.ReadAllText(file));
        return saved?["PlanDigest"]?.ToString() == plan.Digest && saved?["Receipt"]?["Path"]?.ToString() == "/v1/profile"
            && saved?["Receipt"]?["Status"]?.GetValue<int>() == 202 && saved?["Receipt"]?["Accepted"]?.GetValue<int>() == 1;
    });
    _ = await client.ImportAsync(plan, args[2], contract, registry, Verify, Checkpoint, CancellationToken.None, profileAlreadyRecorded: profileRecorded);
    Console.WriteLine("Send completed. Verify Arena's public record/tape/signal/curve before closing issue #22. No automatic trading-bot deployment.");
    return 0;
}
if (args.Length is 2 or 3 && args[0] is "check" or "prepare")
{
    bool prepare = args[0] == "prepare";
    if (args.Length != (prepare ? 3 : 2)) throw new ArgumentException("check <private-preview.json> OR prepare <private-preview.json> <owner-confirmed-capital-USD>");
    string dir = PrivateFiles.DirectoryOutsideRepository(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
    var preview = JsonNode.Parse(await File.ReadAllTextAsync(args[1]))!.AsObject();
    var contract = new ArenaContract(await File.ReadAllTextAsync(Path.Combine(dir, "openapi.json")));
    var registry = JsonSerializer.Deserialize<ArenaRegistryListing[]>(await File.ReadAllTextAsync(Path.Combine(dir, "registry.json")))!;
    string capital = prepare ? ReconstructedPosition.Digits(KrakenEvidence.ExactDecimal(args[2])) : "1";
    JsonObject profile = new() { ["handle"] = "omenas", ["capital_ccy"] = "USD", ["declared_capital"] = capital,
        ["effective_from"] = "2026-10-11T00:00:00Z", ["bio"] = "DEBYKO's own bot: four DQL rules on Kraken Futures, live since 11 Oct 2026." };
    var trades = preview["trades"]!.AsArray().OfType<JsonObject>().ToArray();
    var positions = preview["positions"]!.AsObject();
    var errors = contract.Validate(profile, trades, positions, registry, DateTimeOffset.UtcNow);
    if (errors.Count > 0) throw new InvalidDataException(string.Join("; ", errors));
    var batches = ArenaContract.Batches(trades);
    Console.WriteLine($"Offline schema/semantic checks: {trades.Length} trades, {batches.Count} batches, {batches.Sum(b => b.Length)} bytes; no network or writes.");
    if (prepare)
    {
        string Hash(string name) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, name)))).ToLowerInvariant();
        string source = ArenaContract.Digest(new JsonObject { ["capture"] = Hash("capture.json"), ["journal"] = Hash("journal.json"), ["protection"] = Hash("protection-orders.log") });
        var plan = new ArenaImportPlan(profile, trades, positions, source, contract.SchemaHash, ArenaSubmission.RegistryHash(registry));
        string file = Path.Combine(dir, "prepared-" + plan.Digest + ".json");
        await using (var stream = PrivateFiles.CreateNew(file)) await JsonSerializer.SerializeAsync(stream, plan);
        Console.WriteLine("Prepared payload digest: " + plan.Digest + ". Preparation does not approve or send an import.");
    }
    else Console.WriteLine("CAPITAL NOT CONFIRMED: 1 USD was an in-memory validation placeholder only; no profile/approved plan was written.");
    return 0;
}
if (args.Length == 2 && args[0] == "preview")
{
    string dir = PrivateFiles.DirectoryOutsideRepository(Path.GetFullPath(args[1]));
    var sourceCapture = JsonSerializer.Deserialize<HistoryCapture>(await File.ReadAllTextAsync(Path.Combine(dir, "capture.json"))) ?? throw new InvalidDataException("Missing sourceCapture.");
    var journal = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "journal.json")))!.AsObject();
    var proof = BotJournalEvidence.Read(journal, await File.ReadAllTextAsync(Path.Combine(dir, "protection-orders.log")), sourceCapture.Executions);
    var facts = KrakenEvidence.Normalize(sourceCapture, DateTimeOffset.Parse("2026-10-11T00:00:00Z"));
    var history = PositionReconstruction.Build(facts.Fills, facts.Funding, proof.Select(p => p.Annotation).ToArray(),
        DateTimeOffset.Parse("2026-10-11T00:00:00Z"), sourceCapture.CapturedAt);
    var selected = PositionReconstruction.BotOwnedOnly(history, proof.Select(p => p.OrderId).ToHashSet(StringComparer.Ordinal));
    JsonObject preview = new() { ["captured_at"] = sourceCapture.CapturedAt.ToString("O"), ["open_positions_at"] = sourceCapture.OpenPositionsAt.ToString("O"),
        ["capital_confirmed"] = false, ["approved_for_send"] = false,
        ["trades"] = new JsonArray(selected.Included.Closed.Select(p => (JsonNode)p.Payload()).ToArray()),
        ["positions"] = new JsonObject { ["account"] = "live", ["positions"] = new JsonArray(selected.Included.Open.Select(p => (JsonNode)p.Payload()).ToArray()) },
        ["excluded"] = JsonSerializer.SerializeToNode(selected.Excluded) };
    await using (var stream = PrivateFiles.CreateNew(Path.Combine(dir, "preview-" + ArenaContract.Digest(preview)[..16] + ".json"))) await JsonSerializer.SerializeAsync(stream, preview);
    Console.WriteLine($"PREVIEW ONLY: {selected.Included.Closed.Count} bot closed positions; {selected.Included.Open.Count} bot open positions; {selected.Excluded.Count} manual/mixed/unproven cycles excluded.");
    foreach (var p in selected.Included.Closed) Console.WriteLine($"{p.OpenedAt:yyyy-MM-dd HH:mm:ss} → {p.ClosedAt:yyyy-MM-dd HH:mm:ss} UTC | {p.Symbol} {p.Side} | base {p.Size} | entry {p.Entry} | exit {p.Exit} | fee {p.Fees} USD | funding {p.Funding} USD | fills {p.Fills.Count} | signal {p.FiringId is not null}");
    return 0;
}
// This executable never starts a worker or loads its trading configuration.
if (args.Length != 2 || args[0] is not ("capture" or "capture-existing-key-readonly" or "snapshot-existing-key-readonly"))
{
    Console.Error.WriteLine("Usage: arena-import (capture|capture-existing-key-readonly) <private-output.json> OR preview <private-directory>. No Arena write.");
    return 2;
}
PrivateFiles.DirectoryOutsideRepository(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
if (File.Exists(args[1])) throw new InvalidOperationException("Capture file exists; preserve the original audit evidence.");
string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"Set {name} in a protected environment, never on the command line.");
using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
var reader = new KrakenHistoryReader(http, Required("TRADINGBOT_KRAKEN_FUTURES_API_KEY"), Required("TRADINGBOT_KRAKEN_FUTURES_API_SECRET"), existingKeyReadOnly: args[0] != "capture");
if (args[0] == "snapshot-existing-key-readonly")
{
    var snapshot = await reader.CurrentOpenPositionsAsync(CancellationToken.None);
    await using var output = PrivateFiles.CreateNew(args[1]);
    await JsonSerializer.SerializeAsync(output, new JsonObject { ["account_uid"] = snapshot.AccountUid, ["body"] = snapshot.Body, ["received_at"] = snapshot.ReceivedAt.ToString("O") });
    Console.WriteLine("Fresh read-only native open snapshot saved; no Arena write.");
    return 0;
}
var capture = await reader.CaptureAsync(CancellationToken.None);
await using (var stream = PrivateFiles.CreateNew(args[1]))
    await JsonSerializer.SerializeAsync(stream, capture);
Console.WriteLine($"Complete capture: {capture.Executions.Count} execution events, {capture.Ledger.Count} ledger rows, {capture.PositionUpdates?.Count} position events; no Arena writes.");
return 0;
