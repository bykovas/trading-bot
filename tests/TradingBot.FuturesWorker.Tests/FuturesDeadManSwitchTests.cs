using System.Reflection;
using System.Text.Json;
using TradingBot.Core.Indicators;
using TradingBot.Core.MarketData;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class FuturesDeadManSwitchTests
{
    [Fact]
    public void Normalize_clamps_dead_man_switch_to_at_least_twice_loop_interval()
    {
        var config = new FuturesBotConfiguration
        {
            Worker = new WorkerOptions { LoopIntervalSeconds = 120 },
            Futures = new FuturesOptions { DeadManSwitchSeconds = 90 }
        };

        InvokeNormalize(config);

        Assert.Equal(240, config.Futures.DeadManSwitchSeconds);
    }

    [Fact]
    public async Task Live_cycle_refreshes_dead_man_switch_even_when_holding_position()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "trading-bot-tests", Guid.NewGuid().ToString("N"));
        var config = new FuturesBotConfiguration
        {
            BotInstance = new BotInstanceOptions { Id = "futures-live", Name = "test" },
            Futures = new FuturesOptions
            {
                LiveTradingEnabled = true,
                DeadManSwitchEnabled = true,
                DefaultLeverage = 2m,
                MaxLeverage = 2m,
                DeadManSwitchSeconds = 240
            },
            Worker = new WorkerOptions { RunOnce = true, LoopIntervalSeconds = 120 },
            Kraken = new KrakenOptions { MarketDataMode = "sample" },
            Trading = new TradingOptions { MaxActiveInstruments = 3, TimeframeMinutes = 5 },
            DryRun = new DryRunOptions { OutputDirectory = outputDirectory },
            CandidateUniverse =
            [
                new InstrumentOptions
                {
                    Pair = "XBT/EUR",
                    KrakenPair = "PF_XBTUSD",
                    Enabled = true
                }
            ]
        };
        InvokeNormalize(config);

        var broker = new RecordingFuturesBroker(
        [
            new FuturesOpenPosition("PF_XBTUSD", "LONG", 0.01m, 100m, 101m, 2m)
        ]);
        var worker = new FuturesDecisionWorker(
            config,
            new SampleMarketDataSource(),
            new IndicatorEngine(),
            new LongShortStrategy(config),
            new MarginRiskManager(config),
            new FuturesVirtualPortfolio(config, new FileDryRunPortfolioStore(config.DryRun)),
            new TpSlOrchestrator(config),
            broker);

        await worker.RunAsync(CancellationToken.None);

        Assert.Equal(1, broker.CancelAllAfterCallCount);
        Assert.Equal(240, broker.LastDeadManSwitchSeconds);
    }

    [Fact]
    public async Task Live_reconciliation_arms_tpsl_for_imported_position()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "trading-bot-tests", Guid.NewGuid().ToString("N"));
        var config = new FuturesBotConfiguration
        {
            BotInstance = new BotInstanceOptions { Id = "futures-live", Name = "test" },
            Futures = new FuturesOptions
            {
                LiveTradingEnabled = true,
                DefaultLeverage = 10m,
                MaxLeverage = 10m,
                DeadManSwitchSeconds = 240
            },
            TpSl = new TpSlOptions { Enabled = true, TakeProfitPercent = 3m, StopLossPercent = 2m },
            Worker = new WorkerOptions { RunOnce = true, LoopIntervalSeconds = 120 },
            Kraken = new KrakenOptions { MarketDataMode = "sample" },
            Trading = new TradingOptions { MaxActiveInstruments = 3, TimeframeMinutes = 5 },
            DryRun = new DryRunOptions { OutputDirectory = outputDirectory },
            CandidateUniverse =
            [
                new InstrumentOptions
                {
                    Pair = "ZEC/USD",
                    KrakenPair = "PF_ZECUSD",
                    Enabled = true
                }
            ]
        };
        InvokeNormalize(config);

        var broker = new RecordingFuturesBroker(
        [
            new FuturesOpenPosition("PF_ZECUSD", "LONG", 0.86m, 506.64m, 509m, 10m)
        ]);
        var worker = new FuturesDecisionWorker(
            config,
            new SampleMarketDataSource(),
            new IndicatorEngine(),
            new LongShortStrategy(config),
            new MarginRiskManager(config),
            new FuturesVirtualPortfolio(config, new FileDryRunPortfolioStore(config.DryRun)),
            new TpSlOrchestrator(config),
            broker);

        var state = new PortfolioState { CashEur = 100m };
        state.Positions.Add(new PortfolioPosition { Pair = "ZEC/USD", Side = "LONG", Origin = PositionOrigins.Bot });
        var method = typeof(FuturesDecisionWorker).GetMethod("ReconcileWithKrakenAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = (Task)method.Invoke(worker, new object?[]
        {
            state,
            config.CandidateUniverse,
            Array.Empty<InstrumentMarketState>(),
            DateTimeOffset.UtcNow,
            CancellationToken.None
        })!;
        await task;

        var position = Assert.Single(state.Positions);
        Assert.Equal("EXCHANGE_OPEN", position.TpOrderState);
        Assert.Equal("EXCHANGE_OPEN", position.SlOrderState);
        Assert.Equal(521.8392m, position.TakeProfitPrice);
        Assert.Equal(496.5072m, position.StopLossPrice);
        Assert.Equal(537.0384m, position.ExchangeTakeProfitPrice);
        Assert.Equal(486.3744m, position.ExchangeStopLossPrice);
        Assert.Equal(3m, position.TakeProfitDistancePct);
        Assert.Equal(2m, position.StopDistancePct);
        Assert.Equal(2, broker.TriggerOrderCallCount);
        Assert.Contains(broker.TriggerOrders, order => order.OrderType == "stp" && order.StopPrice == 486.3744m);
        Assert.Contains(broker.TriggerOrders, order => order.OrderType == "take_profit" && order.StopPrice == 537.0384m);
    }

    [Fact]
    public async Task Live_reconciliation_preserves_existing_exchange_tpsl_orders()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "trading-bot-tests", Guid.NewGuid().ToString("N"));
        var config = new FuturesBotConfiguration
        {
            BotInstance = new BotInstanceOptions { Id = "futures-live", Name = "test" },
            Futures = new FuturesOptions
            {
                LiveTradingEnabled = true,
                DefaultLeverage = 10m,
                MaxLeverage = 10m,
                DeadManSwitchSeconds = 240
            },
            TpSl = new TpSlOptions { Enabled = true, TakeProfitPercent = 3m, StopLossPercent = 2m },
            Worker = new WorkerOptions { RunOnce = true, LoopIntervalSeconds = 120 },
            Kraken = new KrakenOptions { MarketDataMode = "sample" },
            Trading = new TradingOptions { MaxActiveInstruments = 3, TimeframeMinutes = 5 },
            DryRun = new DryRunOptions { OutputDirectory = outputDirectory },
            CandidateUniverse =
            [
                new InstrumentOptions
                {
                    Pair = "BCH/USD",
                    KrakenPair = "PF_BCHUSD",
                    PriceDecimals = 2,
                    Enabled = true
                }
            ]
        };
        InvokeNormalize(config);

        var broker = new RecordingFuturesBroker(
        [
            new FuturesOpenPosition("PF_BCHUSD", "LONG", 1.86m, 234.7502m, 234.33m, 10m)
        ])
        {
            OpenOrders =
            [
                new FuturesOpenOrder("sl-1", "PF_BCHUSD", "sell", "stop_loss", 1.86m, 230.05m, true),
                new FuturesOpenOrder("tp-1", "PF_BCHUSD", "sell", "take_profit", 1.86m, 244.14m, true)
            ]
        };
        var worker = new FuturesDecisionWorker(
            config,
            new SampleMarketDataSource(),
            new IndicatorEngine(),
            new LongShortStrategy(config),
            new MarginRiskManager(config),
            new FuturesVirtualPortfolio(config, new FileDryRunPortfolioStore(config.DryRun)),
            new TpSlOrchestrator(config),
            broker);

        var state = new PortfolioState { CashEur = 100m };
        state.Positions.Add(new PortfolioPosition { Pair = "BCH/USD", Side = "LONG", Origin = PositionOrigins.Bot });
        var method = typeof(FuturesDecisionWorker).GetMethod("ReconcileWithKrakenAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = (Task)method.Invoke(worker, new object?[]
        {
            state,
            config.CandidateUniverse,
            Array.Empty<InstrumentMarketState>(),
            DateTimeOffset.UtcNow,
            CancellationToken.None
        })!;
        await task;

        var position = Assert.Single(state.Positions);
        Assert.Equal("EXCHANGE_OPEN", position.TpOrderState);
        Assert.Equal("EXCHANGE_OPEN", position.SlOrderState);
        Assert.Equal(241.792706m, position.TakeProfitPrice);
        Assert.Equal(230.055196m, position.StopLossPrice);
        Assert.Equal(244.14m, position.ExchangeTakeProfitPrice);
        Assert.Equal(230.05m, position.ExchangeStopLossPrice);
        Assert.Equal(0, broker.TriggerOrderCallCount);
    }

    [Fact]
    public async Task Live_reconciliation_rounds_exchange_tpsl_to_price_precision()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "trading-bot-tests", Guid.NewGuid().ToString("N"));
        var config = new FuturesBotConfiguration
        {
            BotInstance = new BotInstanceOptions { Id = "futures-live", Name = "test" },
            Futures = new FuturesOptions
            {
                LiveTradingEnabled = true,
                DefaultLeverage = 10m,
                MaxLeverage = 10m,
                DeadManSwitchSeconds = 240
            },
            TpSl = new TpSlOptions { Enabled = true, TakeProfitPercent = 3m, StopLossPercent = 2m },
            Worker = new WorkerOptions { RunOnce = true, LoopIntervalSeconds = 120 },
            Kraken = new KrakenOptions { MarketDataMode = "sample" },
            Trading = new TradingOptions { MaxActiveInstruments = 3, TimeframeMinutes = 5 },
            DryRun = new DryRunOptions { OutputDirectory = outputDirectory },
            CandidateUniverse =
            [
                new InstrumentOptions
                {
                    Pair = "BCH/USD",
                    KrakenPair = "PF_BCHUSD",
                    PriceDecimals = 2,
                    Enabled = true
                }
            ]
        };
        InvokeNormalize(config);

        var broker = new RecordingFuturesBroker(
        [
            new FuturesOpenPosition("PF_BCHUSD", "LONG", 1.86m, 234.750215m, 234.33m, 10m)
        ]);
        var worker = new FuturesDecisionWorker(
            config,
            new SampleMarketDataSource(),
            new IndicatorEngine(),
            new LongShortStrategy(config),
            new MarginRiskManager(config),
            new FuturesVirtualPortfolio(config, new FileDryRunPortfolioStore(config.DryRun)),
            new TpSlOrchestrator(config),
            broker);

        var state = new PortfolioState { CashEur = 100m };
        state.Positions.Add(new PortfolioPosition { Pair = "BCH/USD", Side = "LONG", Origin = PositionOrigins.Bot });
        var method = typeof(FuturesDecisionWorker).GetMethod("ReconcileWithKrakenAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = (Task)method.Invoke(worker, new object?[]
        {
            state,
            config.CandidateUniverse,
            Array.Empty<InstrumentMarketState>(),
            DateTimeOffset.UtcNow,
            CancellationToken.None
        })!;
        await task;

        Assert.Contains(broker.TriggerOrders, order => order.OrderType == "stp" && order.StopPrice == 225.37m);
        Assert.Contains(broker.TriggerOrders, order => order.OrderType == "take_profit" && order.StopPrice == 248.83m);
        var position = Assert.Single(state.Positions);
        Assert.Equal(230.05521070m, position.StopLossPrice);
        Assert.Equal(241.79272145m, position.TakeProfitPrice);
        Assert.Equal(225.37m, position.ExchangeStopLossPrice);
        Assert.Equal(248.83m, position.ExchangeTakeProfitPrice);
    }

    [Fact]
    public async Task Fast_exit_check_closes_open_position_on_stop_loss_without_full_cycle()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "trading-bot-tests", Guid.NewGuid().ToString("N"));
        var config = new FuturesBotConfiguration
        {
            BotInstance = new BotInstanceOptions { Id = "futures-virtual", Name = "test" },
            Futures = new FuturesOptions
            {
                DefaultLeverage = 10m,
                MaxLeverage = 10m,
                FastExitCheckSeconds = 10
            },
            Worker = new WorkerOptions { LoopIntervalSeconds = 120 },
            Kraken = new KrakenOptions { MarketDataMode = "sample" },
            DryRun = new DryRunOptions { OutputDirectory = outputDirectory },
            CandidateUniverse =
            [
                new InstrumentOptions
                {
                    Pair = "SNX/USD",
                    KrakenPair = "PF_SNXUSD",
                    Enabled = true
                }
            ]
        };
        InvokeNormalize(config);

        var store = new FileDryRunPortfolioStore(config.DryRun);
        store.Save(new PortfolioState
        {
            CashEur = 99m,
            Positions =
            [
                new PortfolioPosition
                {
                    Pair = "SNX/USD",
                    Side = "LONG",
                    Quantity = 10m,
                    EntryPrice = 1m,
                    EntryNotionalEur = 10m,
                    InitialMarginEur = 1m,
                    LastPrice = 1m,
                    MarkPrice = 1m,
                    Leverage = 10m,
                    StopLossPrice = 0.98m,
                    TakeProfitPrice = 1.03m,
                    SlOrderState = "SIMULATED_OPEN",
                    TpOrderState = "SIMULATED_OPEN",
                    OpenedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
                }
            ]
        });

        var worker = new FuturesDecisionWorker(
            config,
            new FixedLightMarketDataSource(0.97m),
            new IndicatorEngine(),
            new LongShortStrategy(config),
            new MarginRiskManager(config),
            new FuturesVirtualPortfolio(config, store),
            new TpSlOrchestrator(config));

        await worker.RunFastExitCheckAsync(CancellationToken.None);

        var saved = store.Load();
        Assert.NotNull(saved);
        Assert.Empty(saved.Positions);
        var action = Assert.Single(saved.ActionHistory);
        Assert.NotNull(action.LastSellAtUtc);
        Assert.NotNull(action.LastStopLossAtUtc);

        var eventsPath = Path.Combine(outputDirectory, config.DryRun.EventsFile);
        var closeCycle = Assert.Single(File.ReadAllLines(eventsPath));
        using var doc = JsonDocument.Parse(closeCycle);
        var decision = Assert.Single(doc.RootElement.GetProperty("decisions").EnumerateArray());
        var closeAction = decision.GetProperty("dryRunAction");
        Assert.Equal("WOULD_CLOSE", closeAction.GetProperty("action").GetString());
        Assert.Equal("SELL_STOP_LOSS", closeAction.GetProperty("exitReasonCode").GetString());
        Assert.Equal("SNX/USD", closeAction.GetProperty("pair").GetString());
    }

    [Fact]
    public void Normalize_allows_leverage_up_to_ten_and_clamps_above()
    {
        var tenX = new FuturesBotConfiguration { Futures = new FuturesOptions { MaxLeverage = 10m, DefaultLeverage = 10m } };
        InvokeNormalize(tenX);
        Assert.Equal(10m, tenX.Futures.MaxLeverage);
        Assert.Equal(10m, tenX.Futures.DefaultLeverage);

        var tooHigh = new FuturesBotConfiguration { Futures = new FuturesOptions { MaxLeverage = 20m, DefaultLeverage = 20m } };
        InvokeNormalize(tooHigh);
        Assert.Equal(10m, tooHigh.Futures.MaxLeverage);
        Assert.Equal(10m, tooHigh.Futures.DefaultLeverage);
    }

    [Fact]
    public void Normalize_preserves_fixed_tpsl_percentages()
    {
        var config = new FuturesBotConfiguration
        {
            Exits = new FuturesExitOptions { TakeProfitAtrMult = 9m, StopAtrMult = 8m },
            TpSl = new TpSlOptions { Enabled = true, TakeProfitPercent = 3m, StopLossPercent = 2m }
        };

        InvokeNormalize(config);

        Assert.Equal(3m, config.TpSl.TakeProfitPercent);
        Assert.Equal(2m, config.TpSl.StopLossPercent);
    }

    private static void InvokeNormalize(FuturesBotConfiguration config)
    {
        var method = typeof(FuturesBotConfiguration).GetMethod("Normalize", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(config, null);
    }

    private sealed class RecordingFuturesBroker(IReadOnlyList<FuturesOpenPosition> positions) : IFuturesBroker
    {
        public bool IsConfigured => true;
        public int CancelAllAfterCallCount { get; private set; }
        public int? LastDeadManSwitchSeconds { get; private set; }
        public decimal? LastLeverageSet { get; private set; }
        public string? LastLeverageSymbol { get; private set; }
        public IReadOnlyList<FuturesOpenOrder> OpenOrders { get; init; } = Array.Empty<FuturesOpenOrder>();
        public List<(string Symbol, string Side, string OrderType, decimal Size, decimal StopPrice, string TriggerSignal, bool ReduceOnly)> TriggerOrders { get; } = new();
        public List<(string Symbol, string Side, decimal Size, decimal TrailingStopPercent, string TriggerSignal, bool ReduceOnly)> TrailingOrders { get; } = new();
        public List<string> CancelledOrders { get; } = new();
        public int TriggerOrderCallCount => TriggerOrders.Count;

        public Task<IReadOnlyList<FuturesAccountBalance>> GetAccountsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FuturesAccountBalance>>(
            [
                new FuturesAccountBalance("EUR", 100m, 100m)
            ]);

        public Task<IReadOnlyList<FuturesOpenPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(positions);

        public Task<IReadOnlyList<FuturesOpenOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken) =>
            Task.FromResult(OpenOrders);

        public Task<FuturesTickerQuote?> GetTickerAsync(string symbol, CancellationToken cancellationToken) =>
            Task.FromResult<FuturesTickerQuote?>(new FuturesTickerQuote(symbol, 100m, 100.1m, 100m, 100m, DateTimeOffset.UtcNow));

        public Task<FuturesOrderResult> SendOrderAsync(
            string symbol,
            string side,
            decimal size,
            bool reduceOnly,
            decimal leverage,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FuturesOrderResult("placed", "order-1", null));

        public Task<FuturesOrderResult> SendIocLimitOrderAsync(
            string symbol,
            string side,
            decimal size,
            decimal limitPrice,
            bool reduceOnly,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FuturesOrderResult("filled", "order-1", null, new FuturesOrderFill(size, limitPrice, null, DateTimeOffset.UtcNow)));

        public Task<FuturesOrderResult> SendTriggerOrderAsync(
            string symbol,
            string side,
            decimal size,
            string orderType,
            decimal stopPrice,
            string triggerSignal,
            bool reduceOnly,
            CancellationToken cancellationToken)
        {
            TriggerOrders.Add((symbol, side, orderType, size, stopPrice, triggerSignal, reduceOnly));
            return Task.FromResult(new FuturesOrderResult("placed", $"{orderType}-1", null));
        }

        public Task<FuturesOrderResult> SendTrailingStopOrderAsync(
            string symbol,
            string side,
            decimal size,
            decimal trailingStopPercent,
            string triggerSignal,
            bool reduceOnly,
            CancellationToken cancellationToken)
        {
            TrailingOrders.Add((symbol, side, size, trailingStopPercent, triggerSignal, reduceOnly));
            return Task.FromResult(new FuturesOrderResult("placed", "trailing-1", null));
        }

        public Task<FuturesOrderResult> CancelOrderAsync(string orderId, CancellationToken cancellationToken)
        {
            CancelledOrders.Add(orderId);
            return Task.FromResult(new FuturesOrderResult("cancelled", orderId, null));
        }

        public Task<bool> SetLeveragePreferenceAsync(string symbol, decimal maxLeverage, CancellationToken cancellationToken)
        {
            LastLeverageSymbol = symbol;
            LastLeverageSet = maxLeverage;
            return Task.FromResult(true);
        }

        public Task CancelAllAfterAsync(int timeoutSeconds, CancellationToken cancellationToken)
        {
            CancelAllAfterCallCount++;
            LastDeadManSwitchSeconds = timeoutSeconds;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedLightMarketDataSource(decimal markPrice) : IMarketDataSource
    {
        public Task<IReadOnlyList<InstrumentMarketState>> GetLightMarketStatesAsync(
            IReadOnlyList<InstrumentOptions> instruments,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InstrumentMarketState>>(instruments.Select(instrument => new InstrumentMarketState
            {
                Instrument = instrument,
                Candles = Array.Empty<Candle>(),
                Quote = new Quote(
                    Bid: markPrice,
                    Ask: markPrice,
                    Last: markPrice,
                    VolumeToday: 100_000m,
                    MarkPrice: markPrice)
            }).ToList());

        public Task<IReadOnlyList<InstrumentMarketState>> GetFullMarketStatesAsync(
            IReadOnlyList<InstrumentOptions> instruments,
            int timeframeMinutes,
            IReadOnlyList<InstrumentMarketState> lightStates,
            CancellationToken cancellationToken) =>
            Task.FromResult(lightStates);
    }
}
