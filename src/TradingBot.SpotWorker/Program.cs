using System.Text;
using TradingBot.SpotWorker;

Console.OutputEncoding = Encoding.UTF8;

try
{
    var config = BotConfiguration.Load();
    using var fileLogging = FileLogging.Configure(config.Logging.Directory);
    Console.WriteLine($"fileLogging=enabled directory={config.Logging.Directory}");
    using var cancellation = new CancellationTokenSource();

    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };

    using var handler = new SocketsHttpHandler
    {
        UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
    };

    using var httpClient = new HttpClient(handler)
    {
        Timeout = TimeSpan.FromSeconds(config.Http.TimeoutSeconds)
    };

    IMarketDataSource marketDataSource = config.Kraken.MarketDataMode.Equals("kraken", StringComparison.OrdinalIgnoreCase)
        ? new KrakenMarketDataSource(httpClient, config.Kraken)
        : new SampleMarketDataSource();
    IUniverseProvider universeProvider = config.Kraken.MarketDataMode.Equals("kraken", StringComparison.OrdinalIgnoreCase)
        ? new KrakenSpotUniverseProvider(httpClient, config.Kraken, config.UniverseDiscovery, config.CandidateUniverse)
        : new ConfiguredUniverseProvider(config.CandidateUniverse);

    var fallbackAdvisor = new HeuristicWatchlistAdvisor();
    IWatchlistAdvisor watchlistAdvisor = config.Ai.Provider.Equals("openai-compatible", StringComparison.OrdinalIgnoreCase)
        ? new CachedWatchlistAdvisor(
            new OpenAiCompatibleWatchlistAdvisor(httpClient, config.Ai),
            fallbackAdvisor,
            config.Ai.WatchlistRefreshSeconds,
            blackoutFromHour: config.ExecutionPolicy.EntryBlackoutUtcFromHour,
            blackoutMinutes: config.ExecutionPolicy.EntryBlackoutMinutes)
        : fallbackAdvisor;

    var krakenBroker = new KrakenBroker(httpClient, config.Kraken);
    var broker = config.Kraken.MarketDataMode.Equals("kraken", StringComparison.OrdinalIgnoreCase) && krakenBroker.IsConfigured
        ? krakenBroker
        : null;

    var portfolioStore = CreatePortfolioStore(config);
    var worker = new DecisionWorker(
        config,
        marketDataSource,
        watchlistAdvisor,
        new IndicatorEngine(),
        new TechnicalDecisionEngine(),
        new RiskManager(),
        new DryRunPortfolio(config.DryRun, config.Portfolio, config.ExecutionPolicy, config.PositionExit, config.PositionSizing, portfolioStore, strategy: config.Strategy, correlationRisk: config.CorrelationRisk, fullConfig: config),
        broker,
        universeProvider: universeProvider);

    await worker.RunAsync(cancellation.Token);
    return 0;
}
catch (OperationCanceledException)
{
    Console.WriteLine("Shutdown requested.");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Fatal error:");
    Console.Error.WriteLine(ex);
    return 1;
}

static IDryRunPortfolioStore CreatePortfolioStore(BotConfiguration config)
{
    if (config.Database.Enabled && !string.IsNullOrWhiteSpace(config.Database.ConnectionString))
    {
        return new PostgresDryRunPortfolioStore(config.Database.ConnectionString, config.BotInstance.Id);
    }

    if (config.Database.Enabled)
    {
        Console.WriteLine("database=disabled (enabled but connection string is missing; using file dry-run storage)");
    }

    return new FileDryRunPortfolioStore(config.DryRun);
}
