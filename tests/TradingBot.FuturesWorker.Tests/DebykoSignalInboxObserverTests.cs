using TradingBot.Core.Common;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class DebykoSignalInboxObserverTests
{
    [Fact]
    public async Task Fresh_signed_signal_is_consumed_without_executing_any_trade_path()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var store = new FakeInbox(cancellation)
        {
            Next = Event(expiresAt: DateTimeOffset.UtcNow.AddMinutes(1))
        };
        var observer = new DebykoSignalInboxObserver(
            store,
            "futures-live",
            TimeSpan.FromMilliseconds(1));

        await observer.RunAsync(cancellation.Token);

        Assert.Equal(17, store.CompletedId);
        Assert.Null(store.IgnoredId);
        Assert.Contains("execution disabled", store.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expired_signal_is_recorded_as_ignored()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var store = new FakeInbox(cancellation)
        {
            Next = Event(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1))
        };
        var observer = new DebykoSignalInboxObserver(
            store,
            "futures-live",
            TimeSpan.FromMilliseconds(1));

        await observer.RunAsync(cancellation.Token);

        Assert.Equal(17, store.IgnoredId);
        Assert.Null(store.CompletedId);
        Assert.Contains("expired", store.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static DebykoWebhookEvent Event(DateTimeOffset expiresAt) => new(
        Id: 17,
        ChannelId: Guid.NewGuid(),
        BotInstanceId: "futures-live",
        EventId: "fire-17",
        FiringId: "fire-17",
        EventType: "watch.became_true",
        DestinationSequence: 1,
        Tag: "BYKO_FLUSH_LONG",
        Instrument: "BTC-PERP",
        FiredAtUtc: DateTimeOffset.UtcNow,
        ExpiresAtUtc: expiresAt,
        PayloadJson: "{}",
        ReceivedAtUtc: DateTimeOffset.UtcNow);

    private sealed class FakeInbox(CancellationTokenSource cancellation) : IDebykoSignalInbox
    {
        public bool IsAvailable => true;
        public DebykoWebhookEvent? Next { get; set; }
        public long? CompletedId { get; private set; }
        public long? IgnoredId { get; private set; }
        public string Detail { get; private set; } = string.Empty;

        public Task<DebykoWebhookChannel?> FindChannelAsync(Guid channelId, CancellationToken cancellationToken) =>
            Task.FromResult<DebykoWebhookChannel?>(null);

        public Task<bool> StoreAsync(
            DebykoWebhookChannel channel,
            DebykoWebhookEnvelope envelope,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<DebykoWebhookEvent?> ClaimNextAsync(
            string botInstanceId,
            TimeSpan staleClaimAfter,
            CancellationToken cancellationToken)
        {
            var next = Next;
            Next = null;
            return Task.FromResult(next);
        }

        public Task MarkCompletedAsync(long eventId, string detail, CancellationToken cancellationToken)
        {
            CompletedId = eventId;
            Detail = detail;
            cancellation.Cancel();
            return Task.CompletedTask;
        }

        public Task MarkIgnoredAsync(long eventId, string detail, CancellationToken cancellationToken)
        {
            IgnoredId = eventId;
            Detail = detail;
            cancellation.Cancel();
            return Task.CompletedTask;
        }

        public Task MarkForRetryAsync(long eventId, string error, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
