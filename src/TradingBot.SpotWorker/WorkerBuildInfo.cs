namespace TradingBot.SpotWorker;

internal sealed record WorkerBuildInfo(
    string Version,
    string Commit,
    string BuildUtc,
    string ImageTag,
    string StrategyVersion,
    string ChangeSet)
{
    public static WorkerBuildInfo FromEnvironment() => new(
        Value("TRADINGBOT_WORKER_VERSION", "local"),
        Value("TRADINGBOT_WORKER_COMMIT", "unknown"),
        Value("TRADINGBOT_WORKER_BUILD_UTC", "unknown"),
        Value("TRADINGBOT_WORKER_IMAGE_TAG", "local"),
        Value("TRADINGBOT_WORKER_STRATEGY_VERSION", "local"),
        Value("TRADINGBOT_WORKER_CHANGE_SET", "local"));

    private static string Value(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
