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

// Only "kraken-futures" selects the public venue source; anything else runs on
// deterministic sample data so the dry-run loop works end-to-end without network.
IMarketDataSource marketDataSource = config.Kraken.MarketDataMode.Equals("kraken-futures", StringComparison.OrdinalIgnoreCase)
    ? new KrakenFuturesMarketDataSource(httpClient, config.Kraken)
    : new SampleMarketDataSource();

var store = CreatePortfolioStore(config);
Console.WriteLine($"futures persistence: state={store.StateDescription} events={store.EventsDescription}");

var portfolio = new FuturesVirtualPortfolio(config, store);
var worker = new FuturesDecisionWorker(
    config,
    marketDataSource,
    new IndicatorEngine(),
    new LongShortStrategy(config),
    new MarginRiskManager(config),
    portfolio,
    new TpSlOrchestrator(config));

await worker.RunAsync(cancellation.Token);

static IDryRunPortfolioStore CreatePortfolioStore(FuturesBotConfiguration config) =>
    config.Database.Enabled && !string.IsNullOrWhiteSpace(config.Database.ConnectionString)
        ? new PostgresDryRunPortfolioStore(config.Database.ConnectionString, config.BotInstance.Id)
        : new FileDryRunPortfolioStore(config.DryRun);
