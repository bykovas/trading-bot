using System.Text.Json.Nodes;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class FuturesStrategyProfileProviderTests
{
    [Fact]
    public async Task Database_profile_and_instance_override_replace_strategy_values()
    {
        var config = NewConfiguration();
        var store = new MutableStore
        {
            Assignment = Assignment(
                """
                {
                  "Trading": { "MaxActiveInstruments": 64 },
                  "Strategy": { "MinimumLongScore": 0.91 },
                  "Freshness": { "FreshTapeSnapshotCount": 5 },
                  "Exits": { "MaxHoldMinutes": 240 }
                }
                """,
                """
                {
                  "Strategy": { "MinimumLongScore": 0.93 },
                  "Freshness": { "FreshTapeSnapshotCount": 6 }
                }
                """)
        };

        var provider = new FuturesStrategyProfileProvider(config, store);

        await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal(64, config.Trading.MaxActiveInstruments);
        Assert.Equal(0.93m, config.Strategy.MinimumLongScore);
        Assert.Equal(6, config.Freshness.FreshTapeSnapshotCount);
        Assert.Equal(240, config.Exits.MaxHoldMinutes);
    }

    [Fact]
    public async Task Removing_database_assignment_restores_appsettings_values()
    {
        var config = NewConfiguration();
        var store = new MutableStore
        {
            Assignment = Assignment(
                """{ "Strategy": { "MinimumLongScore": 0.95 } }""",
                "{}")
        };
        var provider = new FuturesStrategyProfileProvider(config, store);

        await provider.RefreshAsync(CancellationToken.None);
        Assert.Equal(0.95m, config.Strategy.MinimumLongScore);

        store.Assignment = null;
        await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal(0.80m, config.Strategy.MinimumLongScore);
    }

    [Fact]
    public async Task Infrastructure_sections_in_database_profile_are_ignored()
    {
        var config = NewConfiguration();
        config.Futures.LiveTradingEnabled = true;
        config.Kraken.ApiKey = "real-key";
        var store = new MutableStore
        {
            Assignment = Assignment(
                """
                {
                  "Futures": { "LiveTradingEnabled": false },
                  "Kraken": { "ApiKey": "database-key" },
                  "Strategy": { "MinimumLongScore": 0.88 }
                }
                """,
                "{}")
        };
        var provider = new FuturesStrategyProfileProvider(config, store);

        await provider.RefreshAsync(CancellationToken.None);

        Assert.True(config.Futures.LiveTradingEnabled);
        Assert.Equal("real-key", config.Kraken.ApiKey);
        Assert.Equal(0.88m, config.Strategy.MinimumLongScore);
    }

    private static FuturesBotConfiguration NewConfiguration() => new()
    {
        BotInstance = new BotInstanceOptions { Id = "futures-live", Name = "BYKO" },
        Strategy = new StrategyOptions { MinimumLongScore = 0.80m },
        Trading = new TradingOptions { MaxActiveInstruments = 78, TimeframeMinutes = 15 },
        Freshness = new FuturesFreshnessOptions { FreshTapeSnapshotCount = 4 },
        Exits = new FuturesExitOptions { MaxHoldMinutes = 360 }
    };

    private static BotStrategyProfileAssignment Assignment(string values, string overrides) => new(
        Guid.Parse("f4e1db7b-34df-42b3-8a9e-7ae2b1b88052"),
        "BYKO Momentum",
        1,
        JsonNode.Parse(values)!.AsObject(),
        JsonNode.Parse(overrides)!.AsObject());

    private sealed class MutableStore : IBotStrategyProfileStore
    {
        public BotStrategyProfileAssignment? Assignment { get; set; }

        public Task<BotStrategyProfileAssignment?> LoadAsync(
            string botInstanceId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Assignment);
    }
}
