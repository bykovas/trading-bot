using System.Text.Json;
using TradingBot.Arena;

// This executable never starts a worker or loads its trading configuration.
if (args.Length != 2 || args[0] is not ("capture" or "capture-existing-key-readonly"))
{
    Console.Error.WriteLine("Usage: arena-import (capture|capture-existing-key-readonly) <private-output.json>. Read-only Kraken capture only; no Arena write.");
    return 2;
}
PrivateFiles.DirectoryOutsideRepository(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
if (File.Exists(args[1])) throw new InvalidOperationException("Capture file exists; preserve the original audit evidence.");
string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"Set {name} in a protected environment, never on the command line.");
using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
var reader = new KrakenHistoryReader(http, Required("TRADINGBOT_KRAKEN_FUTURES_API_KEY"), Required("TRADINGBOT_KRAKEN_FUTURES_API_SECRET"), existingKeyReadOnly: args[0] == "capture-existing-key-readonly");
var capture = await reader.CaptureAsync(CancellationToken.None);
await using (var stream = PrivateFiles.CreateNew(args[1]))
    await JsonSerializer.SerializeAsync(stream, capture);
Console.WriteLine($"Complete capture: {capture.Executions.Count} execution events, {capture.Ledger.Count} ledger rows, {capture.PositionUpdates?.Count} position events; no Arena writes.");
return 0;
