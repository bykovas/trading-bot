using TradingBot.Core.Indicators;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class BykoV2SignalNoticeTests
{
    [Theory]
    [InlineData("local", "🛜")]
    [InlineData("received", "🛜")]
    [InlineData("comparison", "ℹ️")]
    [InlineData("correction", "ℹ️")]
    public void Notices_start_with_instance_and_notice_icons(string kind, string icon)
    {
        var notice = new BykoV2SignalNotice("key", kind, "ADA/USD", "BYKO_BREAKOUT_LONG", DateTimeOffset.UtcNow, true, true);
        Assert.StartsWith("😋" + icon + " BYKO", notice.Compose("BYKO", "😋"));
    }

    [Theory]
    [InlineData(true, false, 0, 0)]
    [InlineData(false, false, 1, 0)]
    [InlineData(false, true, 1, 1)]
    public async Task Pause_suppresses_notices_and_only_successful_delivery_is_marked(bool paused, bool delivered, int reads, int marks)
    {
        var config = new FuturesBotConfiguration
        {
            BykoV2 = new BykoV2Options { Enabled = true },
            Telegram = new TelegramNotificationOptions { Enabled = true, BotToken = "test", ChatId = "test", Label = "PUKO" }
        };
        if (paused)
        {
            config.SetRuntimeLimits(FuturesRuntimeLimits.Resolve(config,
                new Dictionary<string, decimal> { [BotConfigOverrideKeys.PositionMarginUsd] = 0m }).Limits);
        }
        var store = new NoticeStore();
        var notifier = new Notifier(delivered);
        var worker = new FuturesDecisionWorker(config, null!, new IndicatorEngine(), new LongShortStrategy(config),
            new MarginRiskManager(config), null!, new TpSlOrchestrator(config), telegramNotifier: notifier, bykoV2VerdictStore: store);

        await worker.SendBykoV2SignalNoticesAsync(CancellationToken.None);

        Assert.Equal(reads, store.Reads);
        Assert.Equal(marks, store.Marks);
        Assert.Equal(reads, notifier.Sends);
    }

    [Fact]
    public void Missing_webhook_is_not_presented_as_a_negative_DQL_verdict_or_an_open_position()
    {
        var notice = new BykoV2SignalNotice("key", "comparison", "ETH/USD", "BYKO_FLUSH_LONG", DateTimeOffset.UtcNow, true, false);
        Assert.Contains("negautas per 5 min", notice.Compose("BYKO"));
        var local = notice with { Kind = "local" };
        Assert.Contains("Boto signalas", local.Compose("BYKO"));
        Assert.DoesNotContain("atidaryta pozicija", local.Compose("BYKO"));
        Assert.Contains("DEBYKO signalas", (notice with { Kind = "received" }).Compose("BYKO"));
        Assert.Contains("pavėluotai", (notice with { Kind = "correction", DebykoReceived = true }).Compose("BYKO"));
    }

    private sealed class NoticeStore : IBykoV2VerdictStore
    {
        public bool IsAvailable => true;
        public int Reads;
        public int Marks;
        public bool Record(string botInstanceId, string pair, DateTimeOffset barClosedAt, BykoV2Verdict verdict, DateTimeOffset evaluatedAt, TimeSpan cooldown) => false;
        public void RecordDebykoFiring(string botInstanceId, string pair, string rule, DateTimeOffset firedAt, string? firingId, DateTimeOffset receivedAt) { }
        public IReadOnlyList<BykoV2SignalNotice> PendingNotices(string botInstanceId, DateTimeOffset now)
        {
            Reads++;
            return [new("key", "local", "ETH/USD", "BYKO_FLUSH_LONG", now, true, false)];
        }
        public void MarkNoticeSent(string botInstanceId, string key) => Marks++;
    }

    private sealed class Notifier(bool delivered) : ITelegramNotifier
    {
        public int Sends;
        public Task SendAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendAlertAsync(string reason, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> TrySendAsync(string text, CancellationToken cancellationToken)
        {
            Sends++;
            return Task.FromResult(delivered);
        }
    }
}
