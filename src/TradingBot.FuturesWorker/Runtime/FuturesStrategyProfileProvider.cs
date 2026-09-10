using System.Text.Json.Nodes;

namespace TradingBot.FuturesWorker;

internal sealed class FuturesStrategyProfileProvider(
    FuturesBotConfiguration config,
    IBotStrategyProfileStore store)
{
    private readonly JsonObject _baseConfiguration = FuturesBotConfiguration.SerializeForStrategyProfile(config);
    private string? _lastAppliedSignature;
    private string? _lastFailureMessage;

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var assignment = await store.LoadAsync(config.BotInstance.Id, cancellationToken);
            var resolved = _baseConfiguration.DeepClone().AsObject();
            var signature = "appsettings";

            if (assignment is not null)
            {
                MergeAllowedStrategySections(resolved, assignment.Values);
                MergeAllowedStrategySections(resolved, assignment.Overrides);
                signature = $"{assignment.ProfileId:N}:{assignment.Revision}:{assignment.Values}:{assignment.Overrides}";
            }

            config.ApplyStrategyProfile(resolved);

            if (_lastFailureMessage is not null)
            {
                Console.WriteLine($"strategy-profile: instance={config.BotInstance.Id} refresh recovered");
                _lastFailureMessage = null;
            }

            if (!string.Equals(_lastAppliedSignature, signature, StringComparison.Ordinal))
            {
                Console.WriteLine(assignment is null
                    ? $"strategy-profile: instance={config.BotInstance.Id} source=appsettings"
                    : $"strategy-profile: instance={config.BotInstance.Id} source=database profile={assignment.ProfileName} revision={assignment.Revision}");
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
                    $"strategy-profile: instance={config.BotInstance.Id} refresh failed; retaining last effective strategy: {error.Message}");
                _lastFailureMessage = error.Message;
            }
        }
    }

    private static readonly IReadOnlySet<string> AllowedSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Trading",
        "Strategy",
        "Funding",
        "Entry",
        "Freshness",
        "Dip",
        "Filters",
        "Exits",
        "Regime",
        "Shorts",
        "Reversal",
        "Risk",
        "ExecutionPolicy",
        "TpSl"
    };

    private static void MergeAllowedStrategySections(JsonObject destination, JsonObject source)
    {
        foreach (var section in source)
        {
            if (!AllowedSections.Contains(section.Key) || section.Value is not JsonObject sourceSection)
            {
                continue;
            }

            if (FindProperty(destination, section.Key) is not JsonObject destinationSection)
            {
                throw new InvalidOperationException($"Strategy configuration section '{section.Key}' is unavailable.");
            }

            Merge(destinationSection, sourceSection);
        }
    }

    private static JsonNode? FindProperty(JsonObject source, string name)
    {
        foreach (var property in source)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static void Merge(JsonObject destination, JsonObject source)
    {
        foreach (var property in source)
        {
            if (property.Value is JsonObject sourceObject && destination[property.Key] is JsonObject destinationObject)
            {
                Merge(destinationObject, sourceObject);
                continue;
            }

            destination[property.Key] = property.Value?.DeepClone();
        }
    }
}
