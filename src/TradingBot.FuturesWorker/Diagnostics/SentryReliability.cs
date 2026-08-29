using Sentry;

namespace TradingBot.FuturesWorker;

internal static class SentryReliability
{
    public static IDisposable Initialize(string? instance)
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
            options.DefaultTags["service"] = "futures-worker";
            if (!string.IsNullOrWhiteSpace(instance))
            {
                options.DefaultTags["instance"] = instance;
            }
        });
    }

    public static void SetInstance(string instance) =>
        SentrySdk.ConfigureScope(scope => scope.SetTag("instance", instance));

    private static string RuntimeEnvironment() =>
        Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        ?? "Production";
}

internal sealed class SentryFailureGate
{
    private readonly string _service;
    private readonly string? _instance;
    private string? _signature;
    private int _consecutive;
    private bool _reported;

    public SentryFailureGate(string service, string? instance = null)
    {
        _service = service;
        _instance = instance;
    }

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
            scope.SetTag("service", _service);
            scope.SetTag("operation", operation);
            if (!string.IsNullOrWhiteSpace(_instance))
            {
                scope.SetTag("instance", _instance);
            }

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
