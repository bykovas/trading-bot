using System.Text.Json.Nodes;
using TradingBot.Core.Indicators;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class FuturesInstanceConfigurationTests
{
    [Fact]
    public void Lukas_profile_matches_primary_profile_except_identity_and_flip_policy()
    {
        var root = FindRepositoryRoot();
        var primary = LoadObject(Path.Combine(root, "src", "TradingBot.FuturesWorker", "appsettings.json"));
        var lukas = LoadObject(Path.Combine(root, "src", "TradingBot.FuturesWorker", "appsettings.lukas.json"));

        Assert.True(primary["Futures"]?["FlipLongEntries"]?.GetValue<bool>());
        Assert.False(lukas["Futures"]?["FlipLongEntries"]?.GetValue<bool>());
        Assert.Equal("futures-lukas-live", lukas["BotInstance"]?["Id"]?.GetValue<string>());
        Assert.Equal("Lukas live futures worker", lukas["BotInstance"]?["Name"]?.GetValue<string>());

        var normalizedLukas = lukas.DeepClone().AsObject();
        normalizedLukas["BotInstance"] = primary["BotInstance"]?.DeepClone();
        normalizedLukas["Futures"]!["FlipLongEntries"] = true;

        Assert.True(JsonNode.DeepEquals(primary, normalizedLukas));
    }

    [Fact]
    public void Lukas_deployment_uses_an_isolated_container_runtime_and_secret_mapping()
    {
        var root = FindRepositoryRoot();
        var compose = File.ReadAllText(Path.Combine(root, "infra", "docker-compose.prod.yml"));
        var deploy = File.ReadAllText(Path.Combine(root, "infra", "deploy.sh"));
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "static-site.yml"));

        Assert.Contains("container_name: trading-bot-lukas-futures-worker-live", compose);
        Assert.Contains("/opt/trading-bot/futures/lukas-live/.env", compose);
        Assert.Contains("/opt/trading-bot/futures/lukas-live/appsettings.json", compose);
        Assert.Contains("TRADINGBOT_BOT_INSTANCE_ID=futures-lukas-live", deploy);
        Assert.Contains("${TRADINGBOT_LUKAS_KRAKEN_FUTURES_API_KEY:-}", deploy);
        Assert.Contains("${TRADINGBOT_LUKAS_KRAKEN_FUTURES_API_SECRET:-}", deploy);
        Assert.DoesNotContain("TRADINGBOT_FUTURES_FLIP_LONG_ENTRIES", deploy);
        Assert.Contains("secrets.TRADINGBOT_LUKAS_KRAKEN_FUTURES_API_KEY", workflow);
        Assert.Contains("secrets.TRADINGBOT_LUKAS_KRAKEN_FUTURES_API_SECRET", workflow);
    }

    [Fact]
    public async Task Lukas_live_instance_refuses_to_run_when_live_trading_is_disabled()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "trading-bot-tests", Guid.NewGuid().ToString("N"));
        var config = new FuturesBotConfiguration
        {
            BotInstance = new BotInstanceOptions { Id = "futures-lukas-live", Name = "Lukas live futures worker" },
            Futures = new FuturesOptions { LiveTradingEnabled = false },
            Kraken = new KrakenOptions { MarketDataMode = "sample" },
            DryRun = new DryRunOptions { OutputDirectory = outputDirectory }
        };
        var worker = new FuturesDecisionWorker(
            config,
            new SampleMarketDataSource(),
            new IndicatorEngine(),
            new LongShortStrategy(config),
            new MarginRiskManager(config),
            new FuturesVirtualPortfolio(config, new FileDryRunPortfolioStore(config.DryRun)),
            new TpSlOrchestrator(config));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RunAsync(CancellationToken.None));

        Assert.Contains("futures-lukas-live", exception.Message);
        Assert.Contains("refusing to create virtual positions", exception.Message);
    }

    private static JsonObject LoadObject(string path) =>
        JsonNode.Parse(File.ReadAllText(path))?.AsObject()
        ?? throw new InvalidOperationException($"Unable to parse {path}.");

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TradingBot.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate the trading-bot repository root.");
    }
}
