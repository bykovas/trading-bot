using EasyBot.Data;
using Kraken.Net.Enums;
using Microsoft.Extensions.Options;
using Skender.Stock.Indicators;

namespace EasyBot.Trading;

/// <summary>
/// Main trading loop. Runs once per closed candle (aligned to UTC candle boundaries, never a
/// naive fixed-interval timer) plus a small safety buffer to make sure the exchange has
/// published the closed candle by the time we fetch it.
/// </summary>
public sealed class TradingWorker : BackgroundService
{
    private readonly IExchangeClient _exchange;
    private readonly BotState _botState;
    private readonly ICandleRepository _candleRepository;
    private readonly ITradeRepository _tradeRepository;
    private readonly IBotEventRepository _eventRepository;
    private readonly IAppStateRepository _appStateRepository;
    private readonly ILogger<TradingWorker> _logger;
    private readonly BotOptions _options;
    private readonly TimeSpan _candleInterval;

    private int _consecutiveFailures;
    private long? _openTradeId;

    public TradingWorker(
        IExchangeClient exchange,
        BotState botState,
        ICandleRepository candleRepository,
        ITradeRepository tradeRepository,
        IBotEventRepository eventRepository,
        IAppStateRepository appStateRepository,
        ILogger<TradingWorker> logger,
        IOptions<BotOptions> options)
    {
        _exchange = exchange;
        _botState = botState;
        _candleRepository = candleRepository;
        _tradeRepository = tradeRepository;
        _eventRepository = eventRepository;
        _appStateRepository = appStateRepository;
        _logger = logger;
        _options = options.Value;
        _candleInterval = TimeframeMapper.ToTimeSpan(_options.Timeframe);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await WarmStartAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            // Startup reconciliation can fail on transient network/credential problems.
            // Never take the whole host down for this - log, pause trading for safety, and
            // let the main loop keep retrying exchange calls on its own schedule.
            _logger.LogCritical(ex, "Startup reconciliation/cache warm failed");
            await _appStateRepository.SetAsync("status", "error", CancellationToken.None);
            await _appStateRepository.SetAsync("paused", "true", CancellationToken.None);
            await SafeLogEventAsync("critical", $"Startup reconciliation failed: {ex.Message}. Trading paused for safety.", CancellationToken.None);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeUntilNextCandleClose(_candleInterval) + TimeSpan.FromSeconds(_options.CandleCloseSafetyBufferSeconds);
            _logger.LogInformation("Sleeping {Delay} until next {Timeframe} candle close", delay, _options.Timeframe);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (stoppingToken.IsCancellationRequested)
                break;

            try
            {
                await RunIterationAsync(stoppingToken);
                _consecutiveFailures = 0;
                await _appStateRepository.SetAsync("status", "running", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogCritical(ex, "Trading loop iteration failed ({Count} consecutive failures)", _consecutiveFailures);
                await _appStateRepository.SetAsync("status", "error", stoppingToken);
                await SafeLogEventAsync("critical", $"Loop iteration failed: {ex.Message}", stoppingToken);

                if (_consecutiveFailures >= 3)
                {
                    await _appStateRepository.SetAsync("paused", "true", stoppingToken);
                    _logger.LogCritical("Auto-pausing trading after {Count} consecutive failed iterations", _consecutiveFailures);
                    await SafeLogEventAsync("alert", $"Bot auto-paused after {_consecutiveFailures} consecutive failed iterations", stoppingToken);
                }
            }
        }
    }

    internal static TimeSpan TimeUntilNextCandleClose(TimeSpan candleInterval)
    {
        var elapsed = DateTime.UtcNow - DateTime.UnixEpoch;
        var ticksSinceLastClose = elapsed.Ticks % candleInterval.Ticks;
        var ticksUntilNextClose = candleInterval.Ticks - ticksSinceLastClose;
        return TimeSpan.FromTicks(ticksUntilNextClose);
    }

    private async Task WarmStartAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting up: reconciling with exchange and warming candle cache");
        await _botState.ReconcileAsync(ct);

        var candles = await _exchange.GetCandlesAsync(_options.Pair, _options.Timeframe, _options.CandleHistoryDepth, ct);
        foreach (var candle in candles)
            await _candleRepository.UpsertAsync(_options.Pair, _options.Timeframe, candle, ct);

        await _appStateRepository.SetAsync("status", "running", ct);
        await SafeLogEventAsync("info", $"Startup reconciliation complete; cached {candles.Count} candles", ct);
    }

    private async Task RunIterationAsync(CancellationToken ct)
    {
        var latest = await _exchange.GetCandlesAsync(_options.Pair, _options.Timeframe, 2, ct);
        var closedCandle = latest.OrderByDescending(c => c.OpenTime).First();
        await _candleRepository.UpsertAsync(_options.Pair, _options.Timeframe, closedCandle, ct);

        var history = await _candleRepository.GetRecentAsync(_options.Pair, _options.Timeframe, Math.Max(_options.CandleHistoryDepth, _options.EmaSlow + 20), ct);
        if (history.Count < _options.EmaSlow + 2)
        {
            _logger.LogInformation("Not enough candle history yet ({Count} candles)", history.Count);
            return;
        }

        var quotes = history
            .Select(h => new Quote { Date = h.OpenTime, Open = h.Open, High = h.High, Low = h.Low, Close = h.Close, Volume = h.Volume })
            .OrderBy(q => q.Date)
            .ToList();

        var atr = (decimal?)quotes.GetAtr(_options.AtrPeriods).LastOrDefault()?.Atr;
        var signal = Strategy.DetectCrossoverSignal(quotes, _options.EmaFast, _options.EmaSlow);

        var position = await _exchange.GetOpenPositionAsync(_options.Pair, ct);
        var paused = await _appStateRepository.GetAsync("paused", ct) == "true";

        if (signal != Signal.None && position is not null && IsOpposite(signal, position.Side))
        {
            await ClosePositionAsync(position, "reverse", closedCandle, ct);
            position = null;
        }

        if (signal != Signal.None && position is null)
        {
            if (paused)
            {
                _logger.LogInformation("Signal {Signal} detected but trading is paused; skipping entry", signal);
                await SafeLogEventAsync("info", $"Signal {signal} skipped: trading paused", ct);
            }
            else if (atr is > 0)
            {
                await OpenPositionAsync(signal, closedCandle, atr.Value, ct);
            }
        }
        else if (position is not null && atr is > 0)
        {
            await UpdateTrailingStopAsync(position, closedCandle, atr.Value, ct);
        }
    }

    private static bool IsOpposite(Signal signal, PositionSide side) =>
        (signal == Signal.Long && side == PositionSide.Short) ||
        (signal == Signal.Short && side == PositionSide.Long);

    private async Task OpenPositionAsync(Signal signal, ExchangeCandle candle, decimal atr, CancellationToken ct)
    {
        var side = signal == Signal.Long ? PositionSide.Long : PositionSide.Short;
        var orderSide = side == PositionSide.Long ? OrderSide.Buy : OrderSide.Sell;

        var equity = await _exchange.GetEquityAsync(ct);
        var sizing = PositionSizer.Calculate(equity, _options.RiskPercent, atr, _options.AtrMultiplier, _options.LeverageMax, candle.Close, side);

        if (sizing.Size <= 0)
        {
            _logger.LogWarning("Computed position size is zero or negative; skipping entry");
            return;
        }

        await _exchange.PlaceMarketOrderAsync(_options.Pair, orderSide, sizing.Size, reduceOnly: false, ct);
        _logger.LogInformation("Opened {Side} {Size} {Pair} @ ~{Price}", side, sizing.Size, _options.Pair, candle.Close);
        await SafeLogEventAsync("info", $"Opened {side} {sizing.Size} {_options.Pair} @ ~{candle.Close}", ct);

        var stopSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
        try
        {
            await _exchange.PlaceStopOrderAsync(_options.Pair, stopSide, sizing.Size, sizing.StopPrice, reduceOnly: true, ct);
            await SafeLogEventAsync("info", $"Placed stop for {_options.Pair} at {sizing.StopPrice}", ct);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to place protective stop after opening {Side} position; closing position at market immediately", side);
            await SafeLogEventAsync("critical", $"Stop placement failed after opening {side} position: {ex.Message}. Force-closing at market.", ct);
            await _exchange.PlaceMarketOrderAsync(_options.Pair, stopSide, sizing.Size, reduceOnly: true, ct);
            return;
        }

        _openTradeId = await _tradeRepository.OpenTradeAsync(DateTime.UtcNow, _options.Pair, side, sizing.Size, candle.Close, sizing.StopPrice, ct);
    }

    private async Task ClosePositionAsync(ExchangePosition position, string closeReason, ExchangeCandle candle, CancellationToken ct)
    {
        var closeSide = position.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

        var openOrders = await _exchange.GetOpenOrdersAsync(_options.Pair, ct);
        var stopOrder = openOrders.FirstOrDefault(o => o.Type == FuturesOrderType.Stop);
        if (stopOrder is not null)
            await _exchange.CancelOrderAsync(stopOrder.OrderId, ct);

        await _exchange.PlaceMarketOrderAsync(_options.Pair, closeSide, position.Quantity, reduceOnly: true, ct);
        _logger.LogInformation("Closed {Side} {Quantity} {Pair} ({Reason})", position.Side, position.Quantity, _options.Pair, closeReason);
        await SafeLogEventAsync("info", $"Closed {position.Side} {position.Quantity} {_options.Pair} ({closeReason})", ct);

        if (_openTradeId is { } tradeId)
        {
            var pnl = position.UnrealizedPnl;
            await _tradeRepository.CloseTradeAsync(tradeId, DateTime.UtcNow, candle.Close, pnl, fee: null, closeReason, ct);
            _openTradeId = null;
        }
    }

    private async Task UpdateTrailingStopAsync(ExchangePosition position, ExchangeCandle candle, decimal atr, CancellationToken ct)
    {
        var openOrders = await _exchange.GetOpenOrdersAsync(_options.Pair, ct);
        var stopOrder = openOrders.FirstOrDefault(o => o.Type == FuturesOrderType.Stop);
        if (stopOrder?.StopPrice is not { } currentStop)
        {
            _logger.LogWarning("Position open with no stop order found; skipping trailing update this iteration");
            return;
        }

        var stopDistance = _options.AtrMultiplier * atr;
        var candidateStop = position.Side == PositionSide.Long
            ? candle.High - stopDistance
            : candle.Low + stopDistance;

        var newStop = PositionSizer.ComputeTrailingStop(currentStop, candidateStop, position.Side);
        if (newStop == currentStop)
            return;

        await _exchange.UpdateStopOrderAsync(stopOrder.OrderId, newStop, ct);
        _logger.LogInformation("Trailed stop for {Pair} from {OldStop} to {NewStop}", _options.Pair, currentStop, newStop);
        await SafeLogEventAsync("info", $"Trailed stop from {currentStop} to {newStop}", ct);
    }

    private async Task SafeLogEventAsync(string level, string message, CancellationToken ct)
    {
        try
        {
            await _eventRepository.LogAsync(level, message, ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write bot_events row: {Message}", message);
        }
    }
}
