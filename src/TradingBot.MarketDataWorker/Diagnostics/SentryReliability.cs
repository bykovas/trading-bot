using Sentry;

namespace TradingBot.MarketDataWorker;

internal static class SentryReliability
{
    public static IDisposable Initialize()
    {
        var dsn = Environment.GetEnvironmentVariable("SENTRY_DSN");
        return SentrySdk.Init(options =>
        {
            options.Dsn = dsn;
            options.Environment = RuntimeEnvironment();
            options.IsGlobalModeEnabled = false;
            options.AutoSessionTracking = false;
            options.EnableLogs = false;
            options.EnableMetrics = false;
            options.DefaultTags["service"] = "market-data-worker";
        });
    }

    private static string RuntimeEnvironment() =>
        Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        ?? "Production";
}

internal sealed class SentryFailureGate
{
    private string? _signature;
    private int _consecutive;
    private bool _reported;

    public void Report(string operation, Exception exception)
    {
        var signature = $"{operation}:{exception.GetType().FullName}";
        if (_signature != signature)
        {
            _signature = signature;
            _consecutive = 0;
            _reported = false;
        }

        _consecutive++;
        if (_consecutive < 3 || _reported)
        {
            return;
        }

        _reported = true;
        SentrySdk.CaptureException(exception, scope =>
        {
            scope.SetTag("service", "market-data-worker");
            scope.SetTag("operation", operation);
            scope.SetExtra("consecutive_failures", _consecutive);
        });
    }

    public void Recovered()
    {
        _signature = null;
        _consecutive = 0;
        _reported = false;
    }
}
