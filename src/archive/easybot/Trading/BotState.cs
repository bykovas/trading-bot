using EasyBot.Data;
using Microsoft.Extensions.Options;

namespace EasyBot.Trading;

public sealed record ReconciledState(ExchangePosition? Position, ExchangeOrder? StopOrder);

/// <summary>
/// Reconciliation logic: the exchange is always the source of truth. This must run on startup
/// and after any websocket reconnect (if/when a socket subscription is added) so local DB state
/// never drifts from what Kraken actually holds.
/// </summary>
public sealed class BotState
{
    private readonly IExchangeClient _exchange;
    private readonly IAppStateRepository _appStateRepository;
    private readonly ILogger<BotState> _logger;
    private readonly BotOptions _options;

    public BotState(IExchangeClient exchange, IAppStateRepository appStateRepository, ILogger<BotState> logger, IOptions<BotOptions> options)
    {
        _exchange = exchange;
        _appStateRepository = appStateRepository;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<ReconciledState> ReconcileAsync(CancellationToken ct)
    {
        var position = await _exchange.GetOpenPositionAsync(_options.Pair, ct);
        var orders = await _exchange.GetOpenOrdersAsync(_options.Pair, ct);
        var stopOrder = orders.FirstOrDefault(o => o.Type == Kraken.Net.Enums.FuturesOrderType.Stop);

        if (position is null && stopOrder is not null)
        {
            _logger.LogWarning("Reconciliation found an orphaned stop order {OrderId} with no open position; cancelling it", stopOrder.OrderId);
            await _exchange.CancelOrderAsync(stopOrder.OrderId, ct);
            stopOrder = null;
        }

        _logger.LogInformation(
            "Reconciled with exchange: position={Symbol} {Side} {Quantity}@{EntryPrice}, stopOrder={StopOrderId}",
            position?.Symbol, position?.Side, position?.Quantity, position?.EntryPrice, stopOrder?.OrderId);

        await _appStateRepository.SetAsync("last_reconciled_at", DateTime.UtcNow.ToString("O"), ct);

        return new ReconciledState(position, stopOrder);
    }
}
