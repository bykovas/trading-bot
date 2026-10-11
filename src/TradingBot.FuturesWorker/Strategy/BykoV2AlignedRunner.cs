using System.Text.Json;

namespace TradingBot.FuturesWorker;

internal sealed class BykoV2AlignedRunner(
    FuturesBotConfiguration config,
    IDebykoMarketClient client,
    IBykoV2VerdictStore verdictStore,
    IBykoV2AlignedStore alignedStore,
    IClock? clock = null)
{
    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private readonly Dictionary<(Guid Rule, string Pair, DateTimeOffset Bar), DateTimeOffset> _nextPoll = new();
    private readonly Dictionary<(string Pair, DateTimeOffset Bar, DateTimeOffset Slice, bool Flush, bool Reference), DebykoFrameRead> _frames = new();
    private DateTimeOffset _authorizationRetryAt;

    internal static DateTimeOffset BarEnd(DateTimeOffset now, BykoV2Rule rule)
    {
        var seconds = rule is BykoV2Rule.BreakoutLong or BykoV2Rule.BreakoutShort ? 900 : 60;
        var epoch = now.ToUnixTimeSeconds();
        return DateTimeOffset.FromUnixTimeSeconds(epoch - epoch % seconds);
    }

    public async Task<IReadOnlyList<BykoV2LocalSignal>> EvaluateAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        if (now < _authorizationRetryAt) return Array.Empty<BykoV2LocalSignal>();
        foreach (var key in _nextPoll.Keys.Where(key => now - key.Bar > TimeSpan.FromMinutes(30)).ToArray())
            _nextPoll.Remove(key);
        foreach (var key in _frames.Keys.Where(key => now - key.Bar > TimeSpan.FromMinutes(30)).ToArray())
            _frames.Remove(key);

        var signals = new List<BykoV2LocalSignal>();
        foreach (var rule in Enum.GetValues<BykoV2Rule>())
        {
            var ruleName = PostgresBykoV2VerdictStore.RuleName(rule);
            if (!config.BykoV2.DebykoRuleIds.TryGetValue(ruleName, out var ruleId) || ruleId == Guid.Empty)
                continue;
            if (!config.BykoV2.DebykoRuleVersions.TryGetValue(ruleName, out var expectedVersion)
                || expectedVersion.Length != 64 || !expectedVersion.All(Uri.IsHexDigit))
                continue;
            var duration = rule is BykoV2Rule.BreakoutLong or BykoV2Rule.BreakoutShort ? 900 : 60;
            var currentBar = BarEnd(now, rule);
            var bars = new[] { currentBar }.Concat(_nextPoll
                .Where(item => item.Key.Rule == ruleId && item.Key.Bar < currentBar && item.Value <= now
                    && now - item.Key.Bar <= TimeSpan.FromSeconds(duration + 120))
                .Select(item => item.Key.Bar)).Distinct().OrderBy(value => value).ToArray();
            foreach (var bar in bars)
            {
            if (now < bar.AddSeconds(15)) continue;

            foreach (var pair in config.BykoV2.Universe.OrderBy(value => value, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (config.RuntimeLimits.NewEntriesPaused) return Array.Empty<BykoV2LocalSignal>();
                var key = (ruleId, pair, bar);
                if (_nextPoll.TryGetValue(key, out var next) && now < next) continue;
                if (alignedStore.IsFinished(config.BotInstance.Id, ruleId, expectedVersion, pair, bar))
                {
                    _nextPoll[key] = bar.AddMinutes(16);
                    continue;
                }

                _nextPoll[key] = now.AddSeconds(10);
                DebykoBarResult result;
                try
                {
                    result = await client.GetResultAsync(ruleId, Instrument(pair), bar, cancellationToken);
                }
                catch (HttpRequestException error)
                {
                    Console.WriteLine($"byko-v2-aligned: result unavailable rule={ruleName} pair={pair} status={error.StatusCode}");
                    if (error.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                    {
                        _authorizationRetryAt = _clock.UtcNow.AddMinutes(5);
                        return signals;
                    }
                    continue;
                }

                if (result.RuleId != ruleId || result.Instrument != Instrument(pair) || result.BarEnd != bar)
                    throw new InvalidOperationException("DEBYKO result identity does not match requested rule/instrument/bar.");
                if (result.DataAsOf is not { } slice || string.IsNullOrWhiteSpace(result.Version))
                {
                    // not_live/not_measured is not a negative decision, nor permission to use a newer quote.
                    _nextPoll[key] = now.AddSeconds(30);
                    continue;
                }
                if (!string.Equals(result.Version, expectedVersion, StringComparison.Ordinal))
                {
                    _nextPoll[key] = now.AddSeconds(30);
                    continue;
                }
                if (slice < bar.AddSeconds(15) || slice >= bar.AddSeconds(duration)
                    || slice > _clock.UtcNow || result.Attempt < 0
                    || result.Version.Length != 64 || !result.Version.All(Uri.IsHexDigit))
                    throw new InvalidOperationException("DEBYKO result has invalid evaluation slice/version metadata.");

                var frame = await FrameAsync(pair, bar, slice, rule, false, cancellationToken);
                var btc = await FrameAsync("XBT/USD", bar, slice, rule, true, cancellationToken);
                var verdict = frame.Frame is { } pairFrame && btc.Frame is { } btcFrame
                    ? BykoV2DqlStrategy.Evaluate(rule, pairFrame, btcFrame, config.BykoV2)
                    : new BykoV2Verdict(rule, null, frame.Reason ?? btc.Reason ?? "input unavailable", new Dictionary<string, decimal?>());

                alignedStore.Record(config.BotInstance.Id, new(
                    ruleId, result.Version, pair, ruleName, bar, slice, result.Attempt,
                    verdict.Fired, result.Verdict, result.Final,
                    verdict.Fired is null ? verdict.Reason : result.Reason,
                    result.RawBody, JsonSerializer.Serialize(new { pair = frame.RawBodies, btc = btc.RawBodies, units = "published", normalization = "byko-v2/1" }),
                    JsonSerializer.Serialize(verdict.Values)));

                // Retries use a new slice. Do not freeze an entry on an unfinished Agent attempt.
                if (!result.Final) continue;
                var edge = verdictStore.Record(config.BotInstance.Id, pair, bar, verdict, _clock.UtcNow,
                    TimeSpan.FromMinutes(rule is BykoV2Rule.FlushLong or BykoV2Rule.FlushShort
                        ? config.BykoV2.FlushCooldownMinutes : config.BykoV2.BreakoutCooldownMinutes));
                if (result.Final) _nextPoll[key] = bar.AddMinutes(16);
                if (edge && _clock.UtcNow - slice <= TimeSpan.FromSeconds(config.BykoV2.SignalMaxAgeSeconds)
                    && frame.Frame is { Bid: > 0m, Ask: > 0m } priceFrame)
                {
                    var price = rule is BykoV2Rule.FlushLong or BykoV2Rule.BreakoutLong ? priceFrame.Ask.Value : priceFrame.Bid.Value;
                    signals.Add(new(pair, rule, bar, price, verdict, slice));
                }
            }
            }
        }
        return signals;
    }

    public IReadOnlyList<BykoV2SignalNotice> PendingNotices() => alignedStore.PendingNotices(config.BotInstance.Id, _clock.UtcNow);

    private async Task<DebykoFrameRead> FrameAsync(string pair, DateTimeOffset bar, DateTimeOffset slice, BykoV2Rule rule,
        bool reference, CancellationToken cancellationToken)
    {
        var key = (pair, bar, slice, rule is BykoV2Rule.FlushLong or BykoV2Rule.FlushShort, reference);
        if (!_frames.TryGetValue(key, out var frame))
        {
            frame = await client.GetFrameAsync(pair, bar, slice, cancellationToken, rule, reference);
            _frames[key] = frame;
        }
        return frame;
    }

    internal static string Instrument(string pair) => pair == "XBT/USD" ? "BTC-PERP" : pair.Replace("/USD", "-PERP", StringComparison.Ordinal);
}
