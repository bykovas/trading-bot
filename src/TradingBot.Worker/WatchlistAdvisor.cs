using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TradingBot.Worker;

internal interface IWatchlistAdvisor
{
    Task<WatchlistAdvice> SelectAsync(
        IReadOnlyList<InstrumentMarketState> candidates,
        int maxRecommendations,
        CancellationToken cancellationToken);
}

internal sealed class HeuristicWatchlistAdvisor : IWatchlistAdvisor
{
    public Task<WatchlistAdvice> SelectAsync(
        IReadOnlyList<InstrumentMarketState> candidates,
        int maxRecommendations,
        CancellationToken cancellationToken)
    {
        var recommendations = candidates
            .Where(candidate => candidate.LastPrice > 0m && string.IsNullOrWhiteSpace(candidate.DataWarning))
            .OrderByDescending(candidate => candidate.LastVolume)
            .ThenBy(candidate => candidate.VolatilityPercent)
            .Take(maxRecommendations)
            .Select((candidate, index) => new WatchlistRecommendation(
                candidate.Instrument.Pair,
                index + 1,
                $"heuristic pick: volume {candidate.LastVolume:0.####}, volatility {candidate.VolatilityPercent:0.##}%"))
            .ToList();

        var warnings = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.DataWarning))
            .Select(candidate => $"{candidate.Instrument.Pair}: {candidate.DataWarning}")
            .ToList();

        if (recommendations.Count == 0)
        {
            warnings.Add("no usable candidate market data; no active watchlist selected");
        }

        return Task.FromResult(new WatchlistAdvice("heuristic", recommendations, warnings));
    }
}

internal sealed class OpenAiCompatibleWatchlistAdvisor(
    HttpClient httpClient,
    AiOptions options) : IWatchlistAdvisor
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async Task<WatchlistAdvice> SelectAsync(
        IReadOnlyList<InstrumentMarketState> candidates,
        int maxRecommendations,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey) || string.IsNullOrWhiteSpace(options.Model))
        {
            throw new InvalidOperationException("AI provider configured but API key or model is missing");
        }

        var aiAdvice = await AskModelAsync(candidates, maxRecommendations, cancellationToken);
        var validPairs = candidates.Select(candidate => candidate.Instrument.Pair).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recommendations = aiAdvice.Recommendations
            .Where(item => validPairs.Contains(item.Pair))
            .OrderBy(item => item.Priority)
            .Take(maxRecommendations)
            .ToList();

        if (recommendations.Count == 0)
        {
            throw new InvalidOperationException("AI returned no valid configured pairs");
        }

        return aiAdvice with { Recommendations = recommendations };
    }

    private async Task<WatchlistAdvice> AskModelAsync(
        IReadOnlyList<InstrumentMarketState> candidates,
        int maxRecommendations,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(BuildPayload(candidates, maxRecommendations), _jsonOptions),
            Encoding.UTF8,
            "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"AI HTTP {(int)response.StatusCode}: {TrimForLog(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("AI response content is empty");
        }

        var cleanJson = ExtractJsonObject(content);
        var parsed = JsonSerializer.Deserialize<AiWatchlistResponse>(cleanJson, _jsonOptions)
            ?? throw new JsonException("AI watchlist JSON is empty");

        var recommendations = parsed.Recommended
            .Select((item, index) => new WatchlistRecommendation(
                item.Pair.Trim().ToUpperInvariant(),
                item.Priority <= 0 ? index + 1 : item.Priority,
                string.IsNullOrWhiteSpace(item.Reason) ? "AI selected this configured candidate" : item.Reason.Trim()))
            .ToList();

        return new WatchlistAdvice(
            "openai-compatible",
            recommendations,
            parsed.Warnings.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).ToList());
    }

    private object BuildPayload(IReadOnlyList<InstrumentMarketState> candidates, int maxRecommendations)
    {
        var system = """
            You are a watchlist selection assistant for a deterministic trading bot.
            You do not make trade decisions. You do not say buy or sell.
            Select only from the configured candidate pairs. Never invent symbols.
            Prefer liquid, usable candidates with controlled volatility and clear market data.
            Return strict JSON only.
            """;

        var candidateText = candidates.Select(candidate => new
        {
            pair = candidate.Instrument.Pair,
            venue = candidate.Instrument.Venue,
            usable = candidate.LastPrice > 0m && string.IsNullOrWhiteSpace(candidate.DataWarning),
            warning = candidate.DataWarning,
            lastPrice = candidate.LastPrice,
            changePercent = candidate.ChangePercent,
            volatilityPercent = candidate.VolatilityPercent,
            lastVolume = candidate.LastVolume,
            pairStatus = candidate.PairRules?.Status
        });

        var user = JsonSerializer.Serialize(new
        {
            task = $"Select up to {maxRecommendations} pairs to watch for the next deterministic decision cycle.",
            requiredSchema = new
            {
                recommended = new[]
                {
                    new { pair = "SOL/EUR", priority = 1, reason = "short reason without trade instruction" }
                },
                warnings = new[] { "optional warning" }
            },
            candidates = candidateText
        }, _jsonOptions);

        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["temperature"] = 0.1,
            ["messages"] = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            }
        };

        if (options.UseJsonResponseFormat)
        {
            payload["response_format"] = new { type = "json_object" };
        }

        return payload;
    }

    private static string ExtractJsonObject(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBrace = trimmed.IndexOf('{');
            var lastBrace = trimmed.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                return trimmed[firstBrace..(lastBrace + 1)];
            }
        }

        return trimmed;
    }

    private static string TrimForLog(string value) =>
        value.Length <= 300 ? value : value[..300];

    private sealed class AiWatchlistResponse
    {
        public List<AiRecommendation> Recommended { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }

    private sealed class AiRecommendation
    {
        public string Pair { get; set; } = string.Empty;
        public int Priority { get; set; }
        public string Reason { get; set; } = string.Empty;
    }
}

internal sealed class CachedWatchlistAdvisor(
    IWatchlistAdvisor refreshAdvisor,
    IWatchlistAdvisor fallbackAdvisor,
    int refreshSeconds,
    IClock? clock = null) : IWatchlistAdvisor
{
    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WatchlistAdvice? _cachedAdvice;
    private DateTimeOffset _cachedAt;

    public async Task<WatchlistAdvice> SelectAsync(
        IReadOnlyList<InstrumentMarketState> candidates,
        int maxRecommendations,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var refreshInterval = TimeSpan.FromSeconds(Math.Max(0, refreshSeconds));

        if (refreshInterval > TimeSpan.Zero && _cachedAdvice is not null && now - _cachedAt <= refreshInterval)
        {
            return BuildCachedAdvice(now, Array.Empty<string>());
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            now = _clock.UtcNow;
            if (refreshInterval > TimeSpan.Zero && _cachedAdvice is not null && now - _cachedAt <= refreshInterval)
            {
                return BuildCachedAdvice(now, Array.Empty<string>());
            }

            try
            {
                var refreshed = await refreshAdvisor.SelectAsync(candidates, maxRecommendations, cancellationToken);
                var normalized = Normalize(refreshed, candidates, maxRecommendations);
                if (normalized.Recommendations.Count == 0)
                {
                    throw new InvalidOperationException("watchlist refresh returned no valid configured pairs");
                }

                _cachedAdvice = normalized;
                _cachedAt = now;
                return normalized;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
            {
                if (_cachedAdvice is not null && refreshInterval > TimeSpan.Zero && now - _cachedAt <= refreshInterval * 2)
                {
                    return BuildCachedAdvice(now, new[] { $"watchlist refresh failed: {ex.Message}; serving stale cached selection" });
                }

                var fallback = await fallbackAdvisor.SelectAsync(candidates, maxRecommendations, cancellationToken);
                return fallback with
                {
                    Provider = "heuristic",
                    Recommendations = Normalize(fallback, candidates, maxRecommendations).Recommendations,
                    Warnings = fallback.Warnings.Concat(new[] { $"watchlist refresh failed: {ex.Message}; used heuristic watchlist" }).ToList()
                };
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private WatchlistAdvice BuildCachedAdvice(DateTimeOffset now, IReadOnlyList<string> extraWarnings)
    {
        var age = now - _cachedAt;
        return _cachedAdvice! with
        {
            Provider = $"{_cachedAdvice.Provider} (cached {FormatAge(age)} ago)",
            Warnings = _cachedAdvice.Warnings.Concat(extraWarnings).ToList()
        };
    }

    private static WatchlistAdvice Normalize(
        WatchlistAdvice advice,
        IReadOnlyList<InstrumentMarketState> candidates,
        int maxRecommendations)
    {
        var validPairs = candidates.Select(candidate => candidate.Instrument.Pair).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recommendations = advice.Recommendations
            .Where(item => validPairs.Contains(item.Pair))
            .OrderBy(item => item.Priority)
            .Take(maxRecommendations)
            .Select((item, index) => item with { Priority = index + 1 })
            .ToList();

        return advice with { Recommendations = recommendations };
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalMinutes < 1)
        {
            return $"{Math.Max(0, (int)age.TotalSeconds)}s";
        }

        if (age.TotalHours < 1)
        {
            return $"{(int)age.TotalMinutes}m";
        }

        return $"{(int)age.TotalHours}h";
    }
}

internal interface IClock
{
    DateTimeOffset UtcNow { get; }
}

internal sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
