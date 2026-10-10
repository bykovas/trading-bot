using TradingBot.Core.Common;

namespace TradingBot.FuturesWorker;

internal sealed class DebykoSignalInboxObserver(
    IDebykoSignalInbox inbox,
    string botInstanceId,
    TimeSpan? pollInterval = null,
    TimeProvider? timeProvider = null)
{
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!inbox.IsAvailable)
        {
            Console.WriteLine("debyko signal inbox: disabled (shared database unavailable)");
            return;
        }

        Console.WriteLine(
            $"debyko signal inbox: observing bot={botInstanceId} every {_pollInterval.TotalSeconds:0.###}s; execution=off");
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var signal = await inbox.ClaimNextAsync(
                    botInstanceId,
                    staleClaimAfter: TimeSpan.FromSeconds(30),
                    cancellationToken);
                if (signal is null)
                {
                    await Task.Delay(_pollInterval, _timeProvider, cancellationToken);
                    continue;
                }

                if (!string.Equals(signal.EventType, "watch.became_true", StringComparison.Ordinal))
                {
                    await inbox.MarkIgnoredAsync(
                        signal.Id,
                        $"unsupported DEBYKO event type {signal.EventType}",
                        cancellationToken);
                    continue;
                }

                if (signal.ExpiresAtUtc is { } expiresAt && expiresAt <= _timeProvider.GetUtcNow())
                {
                    await inbox.MarkIgnoredAsync(
                        signal.Id,
                        $"DEBYKO signal expired at {expiresAt:O}",
                        cancellationToken);
                    continue;
                }

                // Transport-only first step: prove signed delivery and per-instance routing
                // without allowing a new external input to place an order before its entry,
                // risk and shadow-comparison contract is implemented and reviewed.
                Console.WriteLine(
                    $"DEBYKO_SIGNAL_RECEIVED id={signal.EventId} bot={signal.BotInstanceId} tag={signal.Tag ?? "(none)"} instrument={signal.Instrument ?? "(none)"} firedAt={signal.FiredAtUtc:O} execution=off");
                await inbox.MarkCompletedAsync(
                    signal.Id,
                    "signed signal received; transport-only mode, execution disabled",
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"debyko signal inbox FAILED: {ex.GetType().Name}: {ex.Message}");
                await Task.Delay(_pollInterval, _timeProvider, cancellationToken);
            }
        }
    }
}
