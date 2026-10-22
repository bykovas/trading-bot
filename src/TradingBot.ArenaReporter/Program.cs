using TradingBot.FuturesWorker;

var config = FuturesBotConfiguration.Load();

// Standalone reporting entry point: never construct a trading worker or broker.
using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
string? key = Environment.GetEnvironmentVariable("DEBYKO_ARENA_KEY");
if (string.IsNullOrWhiteSpace(key) || config.BotInstance.Id != "futures-live" || !config.Futures.LiveTradingEnabled || !config.Database.Enabled)
{
    Console.WriteLine("Arena reporter disabled: account-owned key and live BYKO configuration are required. Trading is not started in reporting mode.");
    try { await Task.Delay(Timeout.InfiniteTimeSpan, stopping.Token); } catch (OperationCanceledException) { }
    return;
}
try
{
    using var reporter = new ArenaReporter(config, key);
    await reporter.RunAsync(stopping.Token);
}
catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
catch (Exception ex)
{
    Console.Error.WriteLine($"Arena reporter stopped (separate process; trading unaffected): {ex.ToString().Replace(key, "[key]", StringComparison.Ordinal)}");
    Environment.ExitCode = 1;
}
return;

