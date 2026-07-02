using System.Text;
using TradingBot.Worker;

Console.OutputEncoding = Encoding.UTF8;

try
{
    var config = BotConfiguration.Load();
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

    var fallbackAdvisor = new HeuristicWatchlistAdvisor();
    IWatchlistAdvisor watchlistAdvisor = config.Ai.Provider.Equals("openai-compatible", StringComparison.OrdinalIgnoreCase)
        ? new OpenAiCompatibleWatchlistAdvisor(httpClient, config.Ai, fallbackAdvisor)
        : fallbackAdvisor;

    var krakenBroker = new KrakenBroker(httpClient, config.Kraken);
    var broker = config.Kraken.MarketDataMode.Equals("kraken", StringComparison.OrdinalIgnoreCase) && krakenBroker.IsConfigured
        ? krakenBroker
        : null;

    var worker = new DecisionWorker(
        config,
        marketDataSource,
        watchlistAdvisor,
        new IndicatorEngine(),
        new TechnicalDecisionEngine(),
        new RiskManager(),
        new DryRunPortfolio(config.DryRun, config.Portfolio, config.ExecutionPolicy, config.PositionExit),
        broker);

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
