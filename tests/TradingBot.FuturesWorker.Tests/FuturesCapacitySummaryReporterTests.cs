using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class FuturesCapacitySummaryReporterTests
{
    [Fact]
    public async Task Reports_all_instances_and_marks_the_completed_window_only_after_delivery()
    {
        var store = new FakeStore();
        var telegram = new FakeTelegramNotifier(sendSucceeds: true);
        var reporter = new FuturesCapacitySummaryReporter(store, telegram, "futures-lukas-live");
        var now = new DateTimeOffset(2026, 9, 16, 14, 1, 0, TimeSpan.Zero);

        await reporter.RecordAsync("futures-live", now.AddHours(-2), new FuturesCapacitySkipCounts(2, 1), CancellationToken.None);
        await reporter.RecordAsync("futures-lukas-live", now.AddHours(-2), new FuturesCapacitySkipCounts(1, 0), CancellationToken.None);

        await reporter.ReportCompletedWindowAsync(now, CancellationToken.None);

        Assert.Single(telegram.Messages);
        Assert.Contains("BYKO — 3 (slotai: 2, marža: 1)", telegram.Messages[0]);
        Assert.Contains("LUKO — 1 (slotai: 1, marža: 0)", telegram.Messages[0]);
        Assert.Contains("PUKO — 0", telegram.Messages[0]);
        Assert.Single(store.DeliveredWindows);
        Assert.Empty(store.ClaimedWindows);
    }

    [Fact]
    public async Task Releases_claim_when_telegram_delivery_fails_so_the_window_can_retry()
    {
        var store = new FakeStore();
        var telegram = new FakeTelegramNotifier(sendSucceeds: false);
        var reporter = new FuturesCapacitySummaryReporter(store, telegram, "futures-lukas-live");
        var now = new DateTimeOffset(2026, 9, 16, 14, 1, 0, TimeSpan.Zero);

        await reporter.RecordAsync("futures-live", now.AddHours(-2), new FuturesCapacitySkipCounts(1, 0), CancellationToken.None);
        await reporter.ReportCompletedWindowAsync(now, CancellationToken.None);

        Assert.Empty(store.DeliveredWindows);
        Assert.Empty(store.ClaimedWindows);
    }

    [Fact]
    public void Uses_utc_two_hour_windows()
    {
        var utc = new DateTimeOffset(2026, 9, 16, 15, 59, 59, TimeSpan.FromHours(3));

        var window = FuturesCapacitySummaryReporter.WindowStart(utc);

        Assert.Equal(new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero), window);
    }

    private sealed class FakeTelegramNotifier(bool sendSucceeds) : ITelegramNotifier
    {
        public List<string> Messages { get; } = [];

        public Task SendAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> TrySendAsync(string text, CancellationToken cancellationToken)
        {
            Messages.Add(text);
            return Task.FromResult(sendSucceeds);
        }

        public Task SendAlertAsync(string reason, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeStore : IFuturesCapacitySummaryStore
    {
        private readonly Dictionary<(DateTimeOffset WindowStart, string InstanceId), FuturesCapacitySkipCounts> _counts = [];

        public HashSet<DateTimeOffset> ClaimedWindows { get; } = [];
        public HashSet<DateTimeOffset> DeliveredWindows { get; } = [];

        public Task AddAsync(string botInstanceId, DateTimeOffset windowStartUtc, FuturesCapacitySkipCounts counts, CancellationToken cancellationToken)
        {
            if (counts.Total > 0)
            {
                var key = (windowStartUtc, botInstanceId);
                _counts.TryGetValue(key, out var existing);
                existing ??= new FuturesCapacitySkipCounts(0, 0);
                _counts[key] = new FuturesCapacitySkipCounts(existing.Slots + counts.Slots, existing.Margin + counts.Margin);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<FuturesCapacitySummaryRow>> LoadAsync(DateTimeOffset windowStartUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FuturesCapacitySummaryRow>>(
                _counts
                    .Where(entry => entry.Key.WindowStart == windowStartUtc)
                    .Select(entry => new FuturesCapacitySummaryRow(entry.Key.InstanceId, entry.Value.Slots, entry.Value.Margin))
                    .ToList());

        public Task<bool> TryClaimDeliveryAsync(DateTimeOffset windowStartUtc, string reporterInstanceId, CancellationToken cancellationToken)
        {
            if (DeliveredWindows.Contains(windowStartUtc) || !ClaimedWindows.Add(windowStartUtc))
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }

        public Task MarkDeliveredAsync(DateTimeOffset windowStartUtc, CancellationToken cancellationToken)
        {
            ClaimedWindows.Remove(windowStartUtc);
            DeliveredWindows.Add(windowStartUtc);
            return Task.CompletedTask;
        }

        public Task ReleaseDeliveryClaimAsync(DateTimeOffset windowStartUtc, CancellationToken cancellationToken)
        {
            ClaimedWindows.Remove(windowStartUtc);
            return Task.CompletedTask;
        }
    }
}
