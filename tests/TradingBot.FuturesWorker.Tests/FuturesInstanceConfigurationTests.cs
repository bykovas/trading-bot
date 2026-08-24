using System.Text.Json.Nodes;
using TradingBot.Core.Indicators;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class FuturesInstanceConfigurationTests
{
    [Fact]
    public void Lukas_profile_matches_primary_profile_except_identity_and_mirror_role()
    {
        var root = FindRepositoryRoot();
        var primary = LoadObject(Path.Combine(root, "src", "TradingBot.FuturesWorker", "appsettings.json"));
        var lukas = LoadObject(Path.Combine(root, "src", "TradingBot.FuturesWorker", "appsettings.lukas.json"));

        // FlipLongEntries flips a bot's OWN long signals. futures-live has no own
        // entries, so it stays off on both; the mirror's InvertSide below is what
        // makes the two accounts trade against each other.
        Assert.False(primary["Futures"]?["FlipLongEntries"]?.GetValue<bool>());
        Assert.False(lukas["Futures"]?["FlipLongEntries"]?.GetValue<bool>());

        Assert.Equal("futures-lukas-live", primary["EntryMirror"]?["FollowSourceBotInstanceId"]?.GetValue<string>());
        Assert.Equal("futures-live", lukas["EntryMirror"]?["PublishToBotInstanceId"]?.GetValue<string>());
        Assert.Equal("futures-lukas-live", lukas["BotInstance"]?["Id"]?.GetValue<string>());
        Assert.Equal("Lukas live futures worker", lukas["BotInstance"]?["Name"]?.GetValue<string>());

        // InvertSide is read on both sides of the mirror - the publisher decides the
        // side it writes into the command, the follower decides the side it expects.
        // Disagreeing does not half-work: every command is rejected as
        // MIRROR_SIDE_MISMATCH and the mirror goes quiet instead of loud.
        var publisherInverts = lukas["EntryMirror"]?["InvertSide"]?.GetValue<bool>();
        var followerInverts = primary["EntryMirror"]?["InvertSide"]?.GetValue<bool>();
        Assert.Equal(publisherInverts, followerInverts);
        // Same direction again: futures-live repeats lukas. Only the size differs -
        // 15 USD at 10x against 150 at 1x, which is the same exposure either way.
        Assert.False(followerInverts);

        // futures-live is mirror-only: it opens nothing from its own signals and takes
        // every entry from lukas. lukas trades his own and publishes them.
        Assert.False(primary["Futures"]?["OwnSignalEntriesEnabled"]?.GetValue<bool>());
        Assert.True(lukas["Futures"]?["OwnSignalEntriesEnabled"]?.GetValue<bool>());

        // What the two accounts are allowed to disagree about: who they are, their
        // role in the mirror, whether they act on their own signals, and how much
        // they stake. Everything else - exits, gates, leverage, cooldowns - must
        // match, because a difference there is drift rather than a decision.
        var mayDiffer = new[]
        {
            ("Futures", "OwnSignalEntriesEnabled"),
            ("Futures", "TargetMarginUsd"),
            ("Futures", "MaxMarginPerPositionUsd"),
            ("Futures", "MaxNotionalUsd"),
            ("Futures", "MaxTotalNotionalUsd"),
            ("Futures", "MaxPositions"),
            // Same exposure, different collateral: futures-live holds 150 USD of its
            // own against a 150 USD position, futures-lukas-live holds 15 and borrows
            // the rest. The notional caps and the risk budget are identical because
            // the money at risk does not depend on the leverage under it.
            ("Futures", "MaxLeverage"),
            ("Futures", "DefaultLeverage"),
            // Staking lives in two sections: the notional caps under Futures and the
            // money-at-risk that sizes into them under Risk. Allowing one without the
            // other fails the moment an account is actually resized.
            ("Risk", "TargetRiskUsd"),
            ("Risk", "MaxConcurrentOpenRiskUsd"),
        };

        var normalizedLukas = lukas.DeepClone().AsObject();
        normalizedLukas["BotInstance"] = primary["BotInstance"]?.DeepClone();
        normalizedLukas["EntryMirror"] = primary["EntryMirror"]?.DeepClone();
        foreach (var (section, key) in mayDiffer)
        {
            if (primary[section]?[key] is not null)
            {
                normalizedLukas[section]![key] = primary[section]![key]!.DeepClone();
            }
        }

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
