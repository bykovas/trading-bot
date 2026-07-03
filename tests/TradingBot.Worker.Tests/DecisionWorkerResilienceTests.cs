using TradingBot.Worker;
using Xunit;

namespace TradingBot.Worker.Tests;

public class DecisionWorkerResilienceTests
{
    [Fact]
    public async Task Five_consecutive_cycle_failures_trip_the_kill_switch()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "trading-bot-tests", Guid.NewGuid().ToString("N"));
        var config = new BotConfiguration
        {
            Worker = new WorkerOptions { RunOnce = false, LoopIntervalSeconds = 0 },
            Kraken = new KrakenOptions { MarketDataMode = "sample" },
            Ai = new AiOptions { Provider = "none" },
            Risk = new RiskOptions { KillSwitch = false },
            DryRun = new DryRunOptions
            {
                Enabled = true,
                ApplyVirtualFills = true,
                OutputDirectory = outputDirectory,
                StateFile = "portfolio-state.json",
                EventsFile = "events.jsonl"
            },
            CandidateUniverse = new List<InstrumentOptions>
            {
                new() { Pair = "AAA/EUR", KrakenPair = "AAAEUR", Venue = "Kraken", Enabled = true }
            }
        };

        using var cts = new CancellationTokenSource();
        var marketData = new ThrowingMarketDataSource(cts, cancelAfterCalls: 5);
        var worker = new DecisionWorker(
            config,
            marketData,
            new HeuristicWatchlistAdvisor(),
            new IndicatorEngine(),
            new TechnicalDecisionEngine(),
            new RiskManager(),
            new DryRunPortfolio(config.DryRun, config.Portfolio, config.ExecutionPolicy, config.PositionExit, config.PositionSizing),
            broker: null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunAsync(cts.Token));

        Assert.True(config.Risk.KillSwitch, "kill switch should auto-trip after 5 consecutive failures");
        Assert.True(marketData.CallCount >= 5);
    }

    // Simulates a market-data source that fails every cycle. It cancels the token on
    // the Nth call so the worker's loop can exit once we have observed enough failures.
    private sealed class ThrowingMarketDataSource(CancellationTokenSource cancellation, int cancelAfterCalls) : IMarketDataSource
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<InstrumentMarketState>> GetLightMarketStatesAsync(
            IReadOnlyList<InstrumentOptions> instruments,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount >= cancelAfterCalls)
            {
                cancellation.Cancel();
            }

            throw new InvalidOperationException("planned market-data failure");
        }

        public Task<IReadOnlyList<InstrumentMarketState>> GetFullMarketStatesAsync(
            IReadOnlyList<InstrumentOptions> instruments,
            int timeframeMinutes,
            IReadOnlyList<InstrumentMarketState> lightStates,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }
}
