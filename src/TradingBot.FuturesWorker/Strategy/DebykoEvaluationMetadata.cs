using System.Text.Json;

namespace TradingBot.FuturesWorker;

internal sealed record DebykoEvaluationMetadata(Guid RuleId, string Version, DateTimeOffset BarEnd,
    DateTimeOffset DataAsOf, int Attempt, string Mode)
{
    public static string? RejectionReason(DebykoEvaluationMetadata? metadata, BykoV2Options config, string rule)
    {
        if (metadata is null) return "missing or invalid DEBYKO evaluation metadata";
        if (metadata.Mode != "live") return $"DEBYKO mode mismatch: expected=live actual={metadata.Mode}";
        if (!config.DebykoRuleIds.TryGetValue(rule, out var id)) return $"DEBYKO rule ID pin missing: rule={rule}";
        if (id != metadata.RuleId) return $"DEBYKO rule ID mismatch: rule={rule} expected={id} actual={metadata.RuleId}";
        if (!config.DebykoRuleVersions.TryGetValue(rule, out var version)) return $"DEBYKO version pin missing: rule={rule}";
        return version == metadata.Version ? null
            : $"DEBYKO version mismatch: rule={rule} expected={version} actual={metadata.Version}";
    }

    public static DebykoEvaluationMetadata? Read(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var data = document.RootElement.GetProperty("data");
            var watch = data.GetProperty("watch");
            var evaluation = data.GetProperty("evaluation");
            var result = new DebykoEvaluationMetadata(watch.GetProperty("id").GetGuid(),
                watch.GetProperty("version").GetString()!, evaluation.GetProperty("bar_end").GetDateTimeOffset(),
                evaluation.GetProperty("data_as_of").GetDateTimeOffset(), evaluation.GetProperty("attempt").GetInt32(),
                evaluation.GetProperty("mode").GetString()!);
            return result.RuleId != Guid.Empty && result.Version?.Length == 64 && result.Version.All(Uri.IsHexDigit)
                && result.Attempt >= 0 && result.DataAsOf >= result.BarEnd.AddSeconds(15)
                && result.Mode is "live" or "settled"
                ? result : null;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
