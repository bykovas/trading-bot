namespace TradingBot.FuturesWorker;

internal sealed class FuturesUniversePreferenceProvider(
    FuturesBotConfiguration config,
    IBotUniversePreferenceStore store)
{
    private readonly IReadOnlyList<string> _appsettingsForceInclude = config.UniverseDiscovery.ForceInclude.ToArray();
    private readonly IReadOnlyList<string> _appsettingsForceExclude = config.UniverseDiscovery.Blacklist.ToArray();
    private string? _lastAppliedSignature;
    private string? _lastFailureMessage;

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var preferences = await store.LoadAsync(config.BotInstance.Id, cancellationToken);
            if (preferences is null)
            {
                config.UniverseDiscovery.ForceInclude = _appsettingsForceInclude.ToList();
                config.UniverseDiscovery.Blacklist = _appsettingsForceExclude.ToList();
                _lastAppliedSignature = null;
                return;
            }

            var forceInclude = PostgresBotUniversePreferenceStore.NormalizePairs(preferences.ForceIncludePairs);
            var forceExclude = PostgresBotUniversePreferenceStore.NormalizePairs(preferences.ForceExcludePairs);
            if (forceInclude.Intersect(forceExclude, StringComparer.OrdinalIgnoreCase).Any())
            {
                throw new InvalidOperationException("Database universe preferences contain pairs in both include and exclude lists.");
            }

            config.Trading.MaxActiveInstruments = preferences.AutoInstrumentCount;
            config.UniverseDiscovery.ForceInclude = forceInclude.ToList();
            config.UniverseDiscovery.Blacklist = forceExclude.ToList();

            if (_lastFailureMessage is not null)
            {
                Console.WriteLine($"bot-universe-preferences: instance={config.BotInstance.Id} refresh recovered");
                _lastFailureMessage = null;
            }

            var signature = $"{preferences.AutoInstrumentCount}:{string.Join(',', forceInclude)}:{string.Join(',', forceExclude)}";
            if (!string.Equals(_lastAppliedSignature, signature, StringComparison.Ordinal))
            {
                Console.WriteLine(
                    $"bot-universe-preferences: instance={config.BotInstance.Id} source=database " +
                    $"auto={preferences.AutoInstrumentCount} include={forceInclude.Count} exclude={forceExclude.Count}");
                _lastAppliedSignature = signature;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            if (!string.Equals(_lastFailureMessage, error.Message, StringComparison.Ordinal))
            {
                Console.WriteLine(
                    $"bot-universe-preferences: instance={config.BotInstance.Id} refresh failed; retaining last effective values: {error.Message}");
                _lastFailureMessage = error.Message;
            }
        }
    }
}
