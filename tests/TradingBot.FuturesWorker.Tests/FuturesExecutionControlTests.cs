using System.Reflection;
using TradingBot.Core.Indicators;
using TradingBot.Core.MarketData;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class FuturesExecutionControlTests
{
    [Fact]
    public async Task Long_with_refreshed_quote_inside_deviation_submits_ioc_limit_at_max_allowed_price()
    {
        var broker = new StubBroker
        {
            Ticker = new FuturesTickerQuote("PF_RIVERUSD", 3.49m, 3.50m, 3.495m, 3.495m, DateTimeOffset.UtcNow),
            IocResult = new FuturesOrderResult("filled", "order-1", null, new FuturesOrderFill(2m, 3.50m, null, DateTimeOffset.UtcNow))
        };
        var (fill, brokerAfter, _) = await ExecuteEntryAsync(broker, signalPrice: 3.49456893782m);

        Assert.True(fill.PositionOpened);
        Assert.Equal(1, brokerAfter.IocCallCount);
        Assert.Equal(3.5067m, brokerAfter.LastLimitPrice);
    }

    [Fact]
    public async Task Long_with_refreshed_ask_beyond_max_allowed_is_rejected_before_submit()
    {
        var broker = new StubBroker
        {
            Ticker = new FuturesTickerQuote("PF_RIVERUSD", 3.50m, 3.52m, 3.51m, 3.51m, DateTimeOffset.UtcNow)
        };
        var (fill, brokerAfter, state) = await ExecuteEntryAsync(broker, signalPrice: 3.49456893782m);

        Assert.False(fill.PositionOpened);
        Assert.Empty(state.Positions);
        Assert.Equal(0, brokerAfter.IocCallCount);
        Assert.Equal("LIVE_ENTRY_PRICE_DEVIATION", fill.Action.HoldReasonCode);
    }

    [Fact]
    public async Task Partial_fill_commits_only_filled_quantity()
    {
        var broker = new StubBroker
        {
            Ticker = new FuturesTickerQuote("PF_RIVERUSD", 3.49m, 3.50m, 3.495m, 3.495m, DateTimeOffset.UtcNow),
            IocResult = new FuturesOrderResult("filled", "order-1", null, new FuturesOrderFill(1.25m, 3.50m, null, DateTimeOffset.UtcNow))
        };
        var (fill, _, state) = await ExecuteEntryAsync(broker, signalPrice: 3.49456893782m);

        Assert.True(fill.PositionOpened);
        Assert.Equal(1.25m, Assert.Single(state.Positions).Quantity);
        Assert.Equal(1.25m, fill.Action.FilledQuantity);
    }

    [Fact]
    public async Task Actual_average_fill_price_is_used_for_the_portfolio()
    {
        var broker = new StubBroker
        {
            Ticker = new FuturesTickerQuote("PF_RIVERUSD", 3.49m, 3.50m, 3.495m, 3.495m, DateTimeOffset.UtcNow),
            IocResult = new FuturesOrderResult("filled", "order-1", null, new FuturesOrderFill(2m, 3.501m, null, DateTimeOffset.UtcNow))
        };
        var (fill, _, state) = await ExecuteEntryAsync(broker, signalPrice: 3.49456893782m);

        Assert.True(fill.PositionOpened);
        Assert.Equal(3.501m, Assert.Single(state.Positions).EntryPrice);
        Assert.Equal(3.501m, fill.Action.AverageFillPrice);
    }

    [Fact]
    public async Task Missing_fill_readback_marks_pending_and_blocks_same_symbol()
    {
        var broker = new StubBroker
        {
            Ticker = new FuturesTickerQuote("PF_RIVERUSD", 3.49m, 3.50m, 3.495m, 3.495m, DateTimeOffset.UtcNow),
            IocResult = new FuturesOrderResult("placed", "order-1", null)
        };
        var (fill, _, state) = await ExecuteEntryAsync(broker, signalPrice: 3.49456893782m);

        Assert.False(fill.PositionOpened);
        Assert.Empty(state.Positions);
        Assert.Single(state.PendingFuturesOrders);
        Assert.Equal("FILL_RECONCILIATION_PENDING", fill.Action.HoldReasonCode);
    }

    [Fact]
    public async Task Entry_quantity_uses_fresh_executable_ask_without_fx_conversion()
    {
        var broker = new StubBroker
        {
            Ticker = new FuturesTickerQuote("PF_RIVERUSD", 3.49m, 3.50m, 3.495m, 3.495m, DateTimeOffset.UtcNow),
            IocResult = new FuturesOrderResult("filled", "order-1", null, new FuturesOrderFill(2m, 3.50m, null, DateTimeOffset.UtcNow))
        };

        var (_, brokerAfter, _) = await ExecuteEntryAsync(broker, signalPrice: 3.49456893782m);

        Assert.Equal(decimal.Round(100m / 3.50m, 8), brokerAfter.LastSize);
    }

    [Fact]
    public void Available_collateral_sums_only_usd_accounting_units()
    {
        var total = FuturesDecisionWorker.SumFuturesAvailableCollateralUsd([
            new FuturesAccountBalance("USD", 50m, 45m),
            new FuturesAccountBalance("USDC", 10m, 9m),
            new FuturesAccountBalance("EUR", 100m, 100m)
        ]);

        Assert.Equal(54m, total);
    }

    private static async Task<(FuturesFillResult Fill, StubBroker Broker, PortfolioState State)> ExecuteEntryAsync(
        StubBroker broker,
        decimal signalPrice)
    {
        var config = new FuturesBotConfiguration
        {
            Futures = new FuturesOptions
            {
                LiveTradingEnabled = true,
                TargetMarginUsd = 10m,
                DefaultLeverage = 10m,
                MaxLeverage = 10m
            },
            Entry = new FuturesEntryOptions { MaxEntryPriceDeviationPct = 0.35m },
            Portfolio = new FuturesPortfolioOptions { StartingCashUsd = 200m },
            Fees = new FuturesFeesOptions { MakerPct = 0m, TakerPct = 0m },
            Worker = new WorkerOptions { RunOnce = true },
            DryRun = new DryRunOptions { OutputDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) }
        };
        InvokeNormalize(config);
        var store = new NullStore();
        var portfolio = new FuturesVirtualPortfolio(config, store);
        var state = portfolio.Load();
        var worker = new FuturesDecisionWorker(
            config,
            new SampleMarketDataSource(),
            new IndicatorEngine(),
            new LongShortStrategy(config),
            new MarginRiskManager(config),
            portfolio,
            new TpSlOrchestrator(config),
            broker);
        var method = typeof(FuturesDecisionWorker).GetMethod("ApplyOrExecuteLiveAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = (Task<FuturesFillResult>)method.Invoke(worker, new object?[]
        {
            state,
            "RIVER/USD",
            FuturesDesiredExposure.Long,
            3.49456893782m,
            config.Futures.DerivedNotionalUsd(config.Futures.DefaultLeverage),
            config.Futures.DefaultLeverage,
            false,
            string.Empty,
            null,
            new InstrumentOptions { Pair = "RIVER/USD", KrakenPair = "PF_RIVERUSD", Enabled = true, QuantityDecimals = 8, PriceDecimals = 4 },
            null,
            CancellationToken.None,
            signalPrice,
            false
        })!;

        return (await task, broker, state);
    }

    private static void InvokeNormalize(FuturesBotConfiguration config)
    {
        var method = typeof(FuturesBotConfiguration).GetMethod("Normalize", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(config, null);
    }

    private sealed class StubBroker : IFuturesBroker
    {
        public bool IsConfigured => true;
        public FuturesTickerQuote? Ticker { get; init; }
        public FuturesOrderResult IocResult { get; init; } = FuturesOrderResult.Rejected("not configured");
        public int IocCallCount { get; private set; }
        public decimal? LastLimitPrice { get; private set; }
        public decimal? LastSize { get; private set; }

        public Task<IReadOnlyList<FuturesAccountBalance>> GetAccountsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FuturesAccountBalance>>([new FuturesAccountBalance("EUR", 200m, 200m)]);

        public Task<IReadOnlyList<FuturesOpenPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FuturesOpenPosition>>(Array.Empty<FuturesOpenPosition>());

        public Task<IReadOnlyList<FuturesOpenOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FuturesOpenOrder>>(Array.Empty<FuturesOpenOrder>());

        public Task<FuturesTickerQuote?> GetTickerAsync(string symbol, CancellationToken cancellationToken) =>
            Task.FromResult(Ticker);

        public Task<FuturesOrderResult> SendOrderAsync(string symbol, string side, decimal size, bool reduceOnly, decimal leverage, CancellationToken cancellationToken) =>
            Task.FromResult(new FuturesOrderResult("placed", "close-1", null));

        public Task<FuturesOrderResult> SendIocLimitOrderAsync(string symbol, string side, decimal size, decimal limitPrice, bool reduceOnly, CancellationToken cancellationToken)
        {
            IocCallCount++;
            LastSize = size;
            LastLimitPrice = limitPrice;
            return Task.FromResult(IocResult);
        }

        public Task<FuturesOrderResult> SendTriggerOrderAsync(
            string symbol,
            string side,
            decimal size,
            string orderType,
            decimal stopPrice,
            string triggerSignal,
            bool reduceOnly,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FuturesOrderResult("placed", $"{orderType}-1", null));

        public Task<FuturesOrderResult> SendTrailingStopOrderAsync(
            string symbol,
            string side,
            decimal size,
            decimal trailingStopPercent,
            string triggerSignal,
            bool reduceOnly,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FuturesOrderResult("placed", "trailing-1", null));

        public Task<FuturesOrderResult> CancelOrderAsync(string orderId, CancellationToken cancellationToken) =>
            Task.FromResult(new FuturesOrderResult("cancelled", orderId, null));

        public Task<bool> SetLeveragePreferenceAsync(string symbol, decimal maxLeverage, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task CancelAllAfterAsync(int timeoutSeconds, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class NullStore : IDryRunPortfolioStore
    {
        public string StateDescription => "null";
        public string EventsDescription => "null";
        public PortfolioState? Load() => null;
        public void Save(PortfolioState state) { }
        public void AppendCycle(DryRunCycleRecord record) { }
        public void AppendMarketSnapshots(IReadOnlyList<MarketSnapshotRecord> snapshots) { }
        public IReadOnlyList<MarketSnapshotRecord> LoadRecentMarketSnapshots(DateTimeOffset sinceUtc) => Array.Empty<MarketSnapshotRecord>();
    }
}
