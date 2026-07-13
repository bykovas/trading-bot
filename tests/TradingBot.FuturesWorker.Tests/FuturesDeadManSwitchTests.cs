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

        public Task<IReadOnlyList<FuturesAccountBalance>> GetAccountsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FuturesAccountBalance>>(
            [
                new FuturesAccountBalance("EUR", 100m, 100m)
            ]);

        public Task<IReadOnlyList<FuturesOpenPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(positions);

        public Task<FuturesOrderResult> SendOrderAsync(
            string symbol,
            string side,
            decimal size,
            bool reduceOnly,
            decimal leverage,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FuturesOrderResult("placed", "order-1", null));

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
