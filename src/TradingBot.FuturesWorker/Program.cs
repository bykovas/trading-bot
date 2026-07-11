using TradingBot.FuturesWorker;

var config = FuturesBotConfiguration.Load();
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

var (marketDataSource, universeProvider) = MarketDataSourceFactory.Create(
    httpClient,
    config.Kraken,
    config.UniverseDiscovery,
    config.CandidateUniverse,
    config.Database,
    config.MarketDataConsumer,
    config.Trading.TimeframeMinutes);

var store = CreatePortfolioStore(config);
Console.WriteLine($"futures persistence: state={store.StateDescription} events={store.EventsDescription}");

var portfolio = new FuturesVirtualPortfolio(config, store);
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
    universeProvider: universeProvider);

await worker.RunAsync(cancellation.Token);

static IDryRunPortfolioStore CreatePortfolioStore(FuturesBotConfiguration config) =>
    config.Database.Enabled && !string.IsNullOrWhiteSpace(config.Database.ConnectionString)
        ? new PostgresDryRunPortfolioStore(config.Database.ConnectionString, config.BotInstance.Id)
        : new FileDryRunPortfolioStore(config.DryRun);
