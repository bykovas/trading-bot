using TradingBot.MarketDataWorker;

using var sentry = SentryReliability.Initialize();
var config = MarketDataWorkerConfiguration.Load();

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

var store = new PostgresMarketDataStore(config.Database.ConnectionString);
var spotUniverse = new KrakenSpotUniverseProvider(
    httpClient,
    config.SpotKraken,
    config.UniverseDiscovery,
    config.CandidateUniverse);
var futuresUniverse = new KrakenFuturesUniverseProvider(
    httpClient,
    config.FuturesKraken,
    config.UniverseDiscovery,
    config.CandidateUniverse);
var spotMarketData = new KrakenMarketDataSource(httpClient, config.SpotKraken);
var futuresMarketData = new KrakenFuturesMarketDataSource(httpClient, config.FuturesKraken);

var worker = new MarketDataIngestionWorker(
    config,
    store,
    spotUniverse,
    futuresUniverse,
    spotMarketData,
    futuresMarketData);

await worker.RunAsync(cancellation.Token);
