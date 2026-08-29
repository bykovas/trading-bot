using Sentry;

namespace TradingBot.Api;

internal sealed class SentryFailureGate
{
    private readonly object _sync = new();
    private string? _signature;
    private int _consecutive;
    private bool _reported;

    public void Report(string operation, Exception exception)
    {
        var signature = $"{operation}:{exception.GetType().FullName}";
        int consecutive;
        lock (_sync)
        {
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
            consecutive = _consecutive;
        }

        SentrySdk.CaptureException(exception, scope =>
        {
            scope.SetTag("service", "api");
            scope.SetTag("operation", operation);
            scope.SetExtra("consecutive_failures", consecutive);
        });
    }

    public void Recovered()
    {
        lock (_sync)
        {
            _signature = null;
            _consecutive = 0;
            _reported = false;
        }
    }
}
