using TradingBot.FuturesWorker;

var config = FuturesBotConfiguration.Load();
if (args.Contains("--check-execution-contracts", StringComparer.Ordinal))
{
    using var metadataHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(config.Http.TimeoutSeconds) };
    var metadata = new KrakenExecutionUniverseProvider(metadataHttp, config.Kraken, config.CandidateUniverse, config.BykoV2.Universe);
    var selection = await metadata.GetUniverseAsync(CancellationToken.None);
    var missing = 0;
    foreach (var pair in config.BykoV2.Universe)
    {
        var instrument = FuturesDecisionWorker.ResolveDebykoInstrument(DebykoMarketClient.ToInstrument(pair), selection.Instruments);
        if (instrument is null || !metadata.TryGetRules(instrument.KrakenPair, out var rules))
        {
            Console.WriteLine($"contract-check pair={pair} status=UNAVAILABLE");
            missing++;
        }
        else Console.WriteLine(FormattableString.Invariant(
            $"contract-check pair={pair} symbol={rules.Symbol} tick={rules.PriceTick} quantityStep={rules.QuantityStep} status=OK"));
    }
    Environment.ExitCode = missing == 0 && metadata.HasFreshMetadata ? 0 : 1;
    return;
}
using var sentry = SentryReliability.Initialize(Environment.GetEnvironmentVariable("TRADINGBOT_BOT_INSTANCE_ID"));
SentryReliability.SetInstance(config.BotInstance.Id);
using var logging = FileLogging.Configure(config.Logging.Directory);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

using var httpClient = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(config.Http.TimeoutSeconds)
};

var apiCredentialProvider = new KrakenApiCredentialProvider(
    config.BotInstance.Id,
    BotApiCredentialScope.KrakenFutures,
    config.Kraken,
    CreateBotApiCredentialStore(config));
await apiCredentialProvider.RefreshAsync(cancellation.Token);

var (marketDataSource, universeProvider) = MarketDataSourceFactory.Create(
    httpClient,
    config.Kraken,
    config.UniverseDiscovery,
    config.CandidateUniverse,
    config.Database,
    config.MarketDataConsumer,
    config.Trading.TimeframeMinutes);

IDebykoMarketClient? executionMarketClient = null;
KrakenExecutionUniverseProvider? executionUniverse = null;
DebykoPositionMarketDataSource? positionMarketData = null;
if (config.Futures.UsesDebykoEntrySignals || config.Futures.UsesBykoV2EntrySignals)
{
    var client = new DebykoMarketClient(new HttpClient
    {
        BaseAddress = new Uri(config.DebykoData.BaseUrl),
        Timeout = TimeSpan.FromSeconds(config.Http.TimeoutSeconds)
    }, config.DebykoData.ApiKey);
    positionMarketData = new DebykoPositionMarketDataSource(client, config.PositionManagementData);
    if (config.Futures.UsesDebykoEntrySignals)
        executionMarketClient = client;
}
if (config.Futures.UsesDebykoEntrySignals)
{
    marketDataSource = positionMarketData!;
    executionUniverse = new KrakenExecutionUniverseProvider(httpClient, config.Kraken,
        config.CandidateUniverse, config.BykoV2.Universe);
    universeProvider = executionUniverse;
}

var store = CreatePortfolioStore(config);
Console.WriteLine($"futures persistence: state={store.StateDescription} events={store.EventsDescription}");

ITelegramNotifier telegramNotifier = config.Telegram.IsConfigured
    ? new TelegramNotifier(config.Telegram)
    : new NullTelegramNotifier();
var pukoReadOnly = PukoReadOnlyChecks.Applies(config)
    ? new PukoReadOnlyChecks(httpClient, config, telegramNotifier) : null;
var portfolio = new FuturesVirtualPortfolio(config, store, readOnlyChecks: pukoReadOnly);
if (config.Futures.UsesBykoV2EntrySignals)
    marketDataSource = new HeldPositionMarketDataSource(marketDataSource, positionMarketData!,
        () => portfolio.Load().Positions.Select(x => x.Pair).ToHashSet(StringComparer.OrdinalIgnoreCase));
var entryMirrorStore = CreateEntryMirrorStore(config);
var debykoSignalInbox = CreateDebykoSignalInbox(config);
var bykoV2VerdictStore = CreateBykoV2VerdictStore(config);
var bykoV2LocalSignalRunner = CreateBykoV2LocalSignalRunner(config, bykoV2VerdictStore);
var strategyProfileProvider = new FuturesStrategyProfileProvider(config, CreateBotStrategyProfileStore(config));
var runtimeLimitProvider = new FuturesRuntimeLimitProvider(config, CreateBotConfigOverrideStore(config));
var universePreferenceProvider = new FuturesUniversePreferenceProvider(config, CreateBotUniversePreferenceStore(config));
var capacitySummaryReporter = new FuturesCapacitySummaryReporter(
    CreateFuturesCapacitySummaryStore(config),
    telegramNotifier,
    config.BotInstance.Id);
var krakenFuturesBroker = new KrakenFuturesBroker(httpClient, config.Kraken);
var worker = new FuturesDecisionWorker(
    config,
    marketDataSource,
    new IndicatorEngine(),
    new LongShortStrategy(config),
    new MarginRiskManager(config),
    portfolio,
    new TpSlOrchestrator(config),
    krakenFuturesBroker,
    universeProvider: universeProvider,
    entryMirrorStore: entryMirrorStore,
    telegramNotifier: telegramNotifier,
    strategyProfileProvider: strategyProfileProvider,
    runtimeLimitProvider: runtimeLimitProvider,
    universePreferenceProvider: universePreferenceProvider,
    apiCredentialProvider: apiCredentialProvider,
    capacitySummaryReporter: capacitySummaryReporter,
    debykoSignalInbox: debykoSignalInbox,
    bykoV2LocalSignalRunner: bykoV2LocalSignalRunner,
    bykoV2VerdictStore: bykoV2VerdictStore,
    debykoMarketClient: executionMarketClient,
    executionUniverse: executionUniverse,
    pukoReadOnly: pukoReadOnly);

var retentionTask = store is PostgresDryRunPortfolioStore postgresStore
    ? postgresStore.RunNoOrderRetentionLoopAsync(TimeSpan.FromHours(4), cancellation.Token)
    : Task.CompletedTask;
var pukoMaintenanceTask = pukoReadOnly?.RunMaintenanceAsync(cancellation.Token) ?? Task.CompletedTask;

try
{
    await worker.RunAsync(cancellation.Token);
}
finally
{
    cancellation.Cancel();
    try
    {
        await Task.WhenAll(retentionTask, pukoMaintenanceTask);
    }
    catch (OperationCanceledException)
    {
    }
}

static IDebykoSignalInbox CreateDebykoSignalInbox(FuturesBotConfiguration config) =>
    config.Database.Enabled && !string.IsNullOrWhiteSpace(config.Database.ConnectionString)
        ? new PostgresDebykoSignalInbox(config.Database.ConnectionString)
        : new NullDebykoSignalInbox();

static IBykoV2VerdictStore CreateBykoV2VerdictStore(FuturesBotConfiguration config) =>
    config.Database.Enabled && !string.IsNullOrWhiteSpace(config.Database.ConnectionString)
        ? new PostgresBykoV2VerdictStore(config.Database.ConnectionString)
        : new NullBykoV2VerdictStore();

static BykoV2LocalSignalRunner? CreateBykoV2LocalSignalRunner(
    FuturesBotConfiguration config,
    IBykoV2VerdictStore verdictStore) =>
    config.Database.Enabled
    && !string.IsNullOrWhiteSpace(config.Database.ConnectionString)
    && config.BykoV2.Enabled
        ? new BykoV2LocalSignalRunner(
            config,
            new PostgresMarketDataStore(config.Database.ConnectionString),
            verdictStore,
            alignedRunner: string.IsNullOrWhiteSpace(config.DebykoData.ApiKey) ? null : new BykoV2AlignedRunner(
                config,
                new DebykoMarketClient(new HttpClient
                {
                    BaseAddress = new Uri(config.DebykoData.BaseUrl),
                    Timeout = TimeSpan.FromSeconds(config.Http.TimeoutSeconds)
                }, config.DebykoData.ApiKey),
                verdictStore,
                new PostgresBykoV2AlignedStore(config.Database.ConnectionString)))
        : null;

static IDryRunPortfolioStore CreatePortfolioStore(FuturesBotConfiguration config) =>
    config.Database.Enabled && !string.IsNullOrWhiteSpace(config.Database.ConnectionString)
        ? new PostgresDryRunPortfolioStore(config.Database.ConnectionString, config.BotInstance.Id)
        : new FileDryRunPortfolioStore(config.DryRun);

static IFuturesEntryMirrorStore CreateEntryMirrorStore(FuturesBotConfiguration config) =>
    config.Database.Enabled && !string.IsNullOrWhiteSpace(config.Database.ConnectionString)
        ? new PostgresFuturesEntryMirrorStore(config.Database.ConnectionString)
        : new NullFuturesEntryMirrorStore();

static IBotConfigOverrideStore CreateBotConfigOverrideStore(FuturesBotConfiguration config) =>
    config.Database.Enabled && !string.IsNullOrWhiteSpace(config.Database.ConnectionString)
        ? new PostgresBotConfigOverrideStore(config.Database.ConnectionString)
        : new NullBotConfigOverrideStore();

static IBotUniversePreferenceStore CreateBotUniversePreferenceStore(FuturesBotConfiguration config) =>
    config.Database.Enabled && !string.IsNullOrWhiteSpace(config.Database.ConnectionString)
        ? new PostgresBotUniversePreferenceStore(config.Database.ConnectionString)
        : new NullBotUniversePreferenceStore();

static IBotApiCredentialStore CreateBotApiCredentialStore(FuturesBotConfiguration config) =>
    config.Database.Enabled && !string.IsNullOrWhiteSpace(config.Database.ConnectionString)
        ? new PostgresBotApiCredentialStore(config.Database.ConnectionString)
        : new NullBotApiCredentialStore();

static IFuturesCapacitySummaryStore CreateFuturesCapacitySummaryStore(FuturesBotConfiguration config) =>
    config.Database.Enabled && !string.IsNullOrWhiteSpace(config.Database.ConnectionString)
        ? new PostgresFuturesCapacitySummaryStore(config.Database.ConnectionString)
        : new NullFuturesCapacitySummaryStore();

static IBotStrategyProfileStore CreateBotStrategyProfileStore(FuturesBotConfiguration config) =>
    config.Database.Enabled && !string.IsNullOrWhiteSpace(config.Database.ConnectionString)
        ? new PostgresBotStrategyProfileStore(config.Database.ConnectionString)
        : new NullBotStrategyProfileStore();
