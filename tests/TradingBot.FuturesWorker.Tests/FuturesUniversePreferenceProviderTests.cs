using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class FuturesUniversePreferenceProviderTests
{
    [Fact]
    public async Task Database_preferences_replace_only_universe_selection_settings()
    {
        var config = new FuturesBotConfiguration
        {
            BotInstance = new BotInstanceOptions { Id = "futures-live" },
            Trading = new TradingOptions { MaxActiveInstruments = 78 },
            UniverseDiscovery = new UniverseDiscoveryOptions
            {
                ForceInclude = ["ETH/USD"],
                Blacklist = []
            },
            TpSl = new TpSlOptions { StopLossPercent = 1.75m, TakeProfitPercent = 3.5m }
        };
        var store = new MutableStore
        {
            Preferences = new BotUniversePreferences(50, ["xbt/usd", "ETH/USD"], ["DOGE/USD"])
        };

        await new FuturesUniversePreferenceProvider(config, store).RefreshAsync(CancellationToken.None);

        Assert.Equal(50, config.Trading.MaxActiveInstruments);
        Assert.Equal(["ETH/USD", "XBT/USD"], config.UniverseDiscovery.ForceInclude);
        Assert.Equal(["DOGE/USD"], config.UniverseDiscovery.Blacklist);
        Assert.Equal(1.75m, config.TpSl.StopLossPercent);
        Assert.Equal(3.5m, config.TpSl.TakeProfitPercent);
    }

    [Fact]
    public void Pair_normalization_accepts_delimited_ui_input()
    {
        var pairs = PostgresBotUniversePreferenceStore.NormalizePairs([" eth/usd, XBT/USD ", "ETH/USD;sol/usd\n"]);

        Assert.Equal(["ETH/USD", "SOL/USD", "XBT/USD"], pairs);
    }

    [Fact]
    public async Task Deleting_preferences_restores_appsettings_include_and_exclude_lists()
    {
        var config = new FuturesBotConfiguration
        {
            BotInstance = new BotInstanceOptions { Id = "futures-live" },
            UniverseDiscovery = new UniverseDiscoveryOptions
            {
                ForceInclude = ["XBT/USD"],
                Blacklist = ["DOGE/USD"]
            }
        };
        var store = new MutableStore
        {
            Preferences = new BotUniversePreferences(10, ["ETH/USD"], ["SOL/USD"])
        };
        var provider = new FuturesUniversePreferenceProvider(config, store);

        await provider.RefreshAsync(CancellationToken.None);
        store.Preferences = null;
        await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal(["XBT/USD"], config.UniverseDiscovery.ForceInclude);
        Assert.Equal(["DOGE/USD"], config.UniverseDiscovery.Blacklist);
    }

    [Fact]
    public void Excluded_pair_is_removed_but_an_open_position_stays_managed()
    {
        var instruments = new[]
        {
            Instrument("DOGE/USD", "PF_DOGEUSD"),
            Instrument("ETH/USD", "PF_ETHUSD"),
            Instrument("SOL/USD", "PF_SOLUSD")
        };

        var selected = FuturesDecisionWorker.ApplyUniverseExclusions(
            instruments,
            ["DOGE/USD"],
            ["DOGE/USD", "PF_ETHUSD"]);

        Assert.Equal(["DOGE/USD", "SOL/USD"], selected.Select(instrument => instrument.Pair));
    }

    private static InstrumentOptions Instrument(string pair, string krakenPair) => new()
    {
        Pair = pair,
        KrakenPair = krakenPair,
        Venue = "KrakenFutures",
        Enabled = true
    };

    private sealed class MutableStore : IBotUniversePreferenceStore
    {
        public BotUniversePreferences? Preferences { get; set; }

        public Task<BotUniversePreferences?> LoadAsync(string botInstanceId, CancellationToken cancellationToken) =>
            Task.FromResult(Preferences);
    }
}
