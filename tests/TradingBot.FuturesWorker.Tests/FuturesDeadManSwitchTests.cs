using System.Reflection;
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

        public Task CancelAllAfterAsync(int timeoutSeconds, CancellationToken cancellationToken)
        {
            CancelAllAfterCallCount++;
            LastDeadManSwitchSeconds = timeoutSeconds;
            return Task.CompletedTask;
        }
    }
}
