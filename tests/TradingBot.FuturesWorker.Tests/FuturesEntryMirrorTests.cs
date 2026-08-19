using System.Reflection;
using TradingBot.Core.Indicators;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class FuturesEntryMirrorTests
{
    [Theory]
    [InlineData("LONG", "SHORT")]
    [InlineData("SHORT", "LONG")]
    public void Opposite_side_is_deterministic(string sourceSide, string expectedTargetSide)
    {
        Assert.Equal(expectedTargetSide, FuturesDecisionWorker.OppositeSide(sourceSide));
    }

    [Fact]
    public async Task Lukas_short_is_published_and_executed_as_primary_long_without_exceeding_the_sized_notional()
    {
        var publishedStore = new StubMirrorStore();
        var sourceConfig = CreateConfig("futures-lukas-live");
        sourceConfig.EntryMirror.PublishToBotInstanceId = "futures-live";
        var sourceWorker = CreateWorker(sourceConfig, new StubBroker(), publishedStore);
        var sourceFill = new FuturesFillResult(
            new DryRunAction
            {
                Pair = "BOME/USD",
                Action = "WOULD_OPEN_SHORT",
                Reason = "source entry",
                FilledNotionalEur = 150.25m,
                SizedNotionalEur = 150m,
                AverageFillPrice = 2.02m,
                EffectiveLeverage = 10m
            },
            PositionOpened: true,
            PositionClosed: false);
        var instrument = new InstrumentOptions
        {
            Pair = "BOME/USD",
            KrakenPair = "PF_BOMEUSD",
            QuantityDecimals = 8,
            PriceDecimals = 4
        };

        await InvokePublishAsync(
            sourceWorker,
            "futures-lukas-live-20260819120000",
            instrument,
            FuturesDesiredExposure.Short,
            sourceFill);

        Assert.NotNull(publishedStore.Published);
        var command = publishedStore.Published!;
        Assert.Equal("SHORT", command.SourceSide);
        Assert.Equal("LONG", command.TargetSide);
        Assert.Equal(150m, command.TargetNotionalUsd);
        Assert.Equal(10m, command.Leverage);

        var followerStore = new StubMirrorStore
        {
            Next = command with { Id = 1, AttemptCount = 1, CreatedAtUtc = DateTimeOffset.UtcNow }
        };
        var followerBroker = new StubBroker();
        var followerConfig = CreateConfig("futures-live");
        followerConfig.EntryMirror.FollowSourceBotInstanceId = "futures-lukas-live";
        var followerWorker = CreateWorker(followerConfig, followerBroker, followerStore);
        var state = new PortfolioState
        {
            CashEur = 100m,
            CashQuoteValue = 100m,
            CashQuoteCurrency = "USD"
        };
        var decisions = new List<DryRunDecisionRecord>();

        await InvokeProcessAsync(followerWorker, state, [instrument], decisions);

        var opened = Assert.Single(state.Positions);
        Assert.Equal("LONG", opened.Side);
        Assert.Equal("Mirror", opened.EntryChannel);
        Assert.True(opened.FlippedEntry);
        Assert.InRange(opened.EntryNotionalEur, 149.999m, 150m);
        Assert.Equal(10m, opened.Leverage);
        Assert.Equal("buy", followerBroker.EntrySide);
        Assert.Equal(1, followerBroker.EntryCalls);
        Assert.Equal(1, followerStore.CompletedId);
        Assert.Null(followerStore.FailedId);
        Assert.Single(decisions);
    }

    [Theory]
    [InlineData("LONG", "SHORT", "LONG")]
    [InlineData("SHORT", "LONG", "SHORT")]
    public void Mirrored_position_ignores_local_reversal_signal(
        string heldSide,
        string signalDirection,
        string expectedExposure)
    {
        var strategy = new LongShortStrategy(CreateConfig("futures-live"));
        var position = new PortfolioPosition
        {
            Pair = "BOME/USD",
            Side = heldSide,
            EntryChannel = "Mirror"
        };
        var signal = new TechnicalSignal(
            Score: signalDirection == "LONG" ? 0.9m : 0.1m,
            Direction: signalDirection,
            AllowsLong: signalDirection == "LONG",
            HasBullishStructure: signalDirection == "LONG",
            EmaFullyConfirmed: signalDirection == "LONG",
            BullishEmaGapPercent: signalDirection == "LONG" ? 0.5m : null,
            EmaGapVelocityPercent: null,
            Contributions: [],
            AllowsShort: signalDirection == "SHORT",
            HasBearishStructure: signalDirection == "SHORT",
            BearishEmaGapPercent: signalDirection == "SHORT" ? 0.5m : null,
            ShortScore: signalDirection == "SHORT" ? 0.9m : 0.1m);

        var actual = strategy.DecideHeld(position, signal);

        Assert.Equal(expectedExposure, actual.ToString().ToUpperInvariant());
    }

    private static FuturesBotConfiguration CreateConfig(string instanceId) => new()
    {
        BotInstance = new BotInstanceOptions { Id = instanceId },
        Futures = new FuturesOptions
        {
            LiveTradingEnabled = true,
            AllowShorts = true,
            DefaultLeverage = 10m,
            MaxLeverage = 10m,
            MaxPositions = 3,
            TargetMarginUsd = 15m,
            MaxNotionalUsd = 150m,
            MaxTotalNotionalUsd = 450m,
            MaxMarginPerPositionUsd = 15m
        },
        Entry = new FuturesEntryOptions { MaxEntryPriceDeviationPct = 0.35m },
        Fees = new FuturesFeesOptions { MakerPct = 0.02m, TakerPct = 0.05m },
        Margin = new MarginOptions { MaxAccountMarginUtilizationPercent = 80m },
        Portfolio = new FuturesPortfolioOptions { StartingCashUsd = 100m },
        TpSl = new TpSlOptions
        {
            Enabled = true,
            TakeProfitPercent = 4m,
            StopLossPercent = 2m,
            ExchangeProtectionMultiplierPercent = 200m
        }
    };

    private static FuturesDecisionWorker CreateWorker(
        FuturesBotConfiguration config,
        IFuturesBroker broker,
        IFuturesEntryMirrorStore mirrorStore)
    {
        var portfolio = new FuturesVirtualPortfolio(config, new NullStore());
        return new FuturesDecisionWorker(
            config,
            new SampleMarketDataSource(),
            new IndicatorEngine(),
            new LongShortStrategy(config),
            new MarginRiskManager(config),
            portfolio,
            new TpSlOrchestrator(config),
            broker,
            entryMirrorStore: mirrorStore);
    }

    private static async Task InvokePublishAsync(
        FuturesDecisionWorker worker,
        string cycleId,
        InstrumentOptions instrument,
        FuturesDesiredExposure sourceSide,
        FuturesFillResult fill)
    {
        var method = typeof(FuturesDecisionWorker).GetMethod(
            "PublishMirrorEntryAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(FuturesDecisionWorker), "PublishMirrorEntryAsync");
        var task = (Task)method.Invoke(worker, [cycleId, instrument, sourceSide, fill, CancellationToken.None])!;
        await task;
    }

    private static async Task InvokeProcessAsync(
        FuturesDecisionWorker worker,
        PortfolioState state,
        IReadOnlyList<InstrumentOptions> universe,
        List<DryRunDecisionRecord> decisions)
    {
        var method = typeof(FuturesDecisionWorker).GetMethod(
            "ProcessMirrorEntriesAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(FuturesDecisionWorker), "ProcessMirrorEntriesAsync");
        var task = (Task)method.Invoke(worker, [state, universe, decisions, CancellationToken.None])!;
        await task;
    }

    private sealed class StubMirrorStore : IFuturesEntryMirrorStore
    {
        public FuturesEntryMirrorCommand? Published { get; private set; }
        public FuturesEntryMirrorCommand? Next { get; set; }
        public long? CompletedId { get; private set; }
        public long? FailedId { get; private set; }

        public Task PublishAsync(FuturesEntryMirrorCommand command, CancellationToken cancellationToken)
        {
            Published = command;
            return Task.CompletedTask;
        }

        public Task<FuturesEntryMirrorCommand?> ClaimNextAsync(
            string sourceBotInstanceId,
            string targetBotInstanceId,
            TimeSpan staleClaimAfter,
            CancellationToken cancellationToken)
        {
            var next = Next;
            Next = null;
            return Task.FromResult(next);
        }

        public Task MarkCompletedAsync(long id, string detail, CancellationToken cancellationToken)
        {
            CompletedId = id;
            return Task.CompletedTask;
        }

        public Task MarkForRetryAsync(long id, string error, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task MarkFailedAsync(long id, string error, CancellationToken cancellationToken)
        {
            FailedId = id;
            return Task.CompletedTask;
        }
    }

    private sealed class StubBroker : IFuturesBroker
    {
        public bool IsConfigured => true;
        public int EntryCalls { get; private set; }
        public string? EntrySide { get; private set; }

        public Task<IReadOnlyList<FuturesAccountBalance>> GetAccountsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FuturesAccountBalance>>([new FuturesAccountBalance("USD", 100m, 100m)]);

        public Task<IReadOnlyList<FuturesOpenPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FuturesOpenPosition>>([]);

        public Task<IReadOnlyList<FuturesOpenOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FuturesOpenOrder>>([]);

        public Task<FuturesTickerQuote?> GetTickerAsync(string symbol, CancellationToken cancellationToken) =>
            Task.FromResult<FuturesTickerQuote?>(new FuturesTickerQuote(
                symbol,
                2.019m,
                2.02m,
                2.02m,
                2.02m,
                DateTimeOffset.UtcNow));

        public Task<FuturesOrderResult> SendOrderAsync(
            string symbol,
            string side,
            decimal size,
            bool reduceOnly,
            decimal leverage,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FuturesOrderResult("placed", "market-1", null));

        public Task<FuturesOrderResult> SendFillOrKillLimitOrderAsync(
            string symbol,
            string side,
            decimal size,
            decimal limitPrice,
            bool reduceOnly,
            CancellationToken cancellationToken)
        {
            EntryCalls++;
            EntrySide = side;
            return Task.FromResult(new FuturesOrderResult(
                "filled",
                "entry-1",
                null,
                new FuturesOrderFill(size, 2.02m, 0.075m, DateTimeOffset.UtcNow)));
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

        public Task<bool> SetLeveragePreferenceAsync(
            string symbol,
            decimal maxLeverage,
            CancellationToken cancellationToken) =>
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
        public IReadOnlyList<MarketSnapshotRecord> LoadRecentMarketSnapshots(DateTimeOffset sinceUtc) => [];
    }
}
