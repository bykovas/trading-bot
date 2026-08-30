using System.Text.Json.Nodes;
using TradingBot.Core.Indicators;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class FuturesInstanceConfigurationTests
{
    // Slot counts and the two caps derived from them are pinned together: a slot count
    // the caps cannot fund is the mismatch that hid the clamped MaxPositions for two days.
    // Per position is 15 USD margin at 10x = 150 notional, 4.5 USD of stop risk.
    [Fact]
    public void Slot_counts_and_their_linked_caps_agree_in_both_instances()
    {
        var root = FindRepositoryRoot();
        var primary = LoadObject(Path.Combine(root, "src", "TradingBot.FuturesWorker", "appsettings.json"));
        var lukas = LoadObject(Path.Combine(root, "src", "TradingBot.FuturesWorker", "appsettings.lukas.json"));

        AssertSlotsAreFunded(primary, expectedSlots: 6);
        AssertSlotsAreFunded(lukas, expectedSlots: 3);
    }

    private static void AssertSlotsAreFunded(JsonObject config, int expectedSlots)
    {
        var futures = config["Futures"]!;
        var slots = futures["MaxPositions"]!.GetValue<int>();
        Assert.Equal(expectedSlots, slots);

        var perPositionNotional =
            futures["TargetMarginUsd"]!.GetValue<decimal>() * futures["DefaultLeverage"]!.GetValue<decimal>();
        Assert.Equal(perPositionNotional * slots, futures["MaxTotalNotionalUsd"]!.GetValue<decimal>());

        var risk = config["Risk"]!;
        Assert.Equal(
            risk["TargetRiskUsd"]!.GetValue<decimal>() * slots,
            risk["MaxConcurrentOpenRiskUsd"]!.GetValue<decimal>());
    }

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

        // The mirror is OFF since the own-strategy experiment of 2026-08-24: neither a
        // follower source nor a publish target anywhere. Re-linking is a decision, not a
        // leftover, so its absence is pinned.
        Assert.Null(primary["EntryMirror"]?["FollowSourceBotInstanceId"]);
        Assert.Null(lukas["EntryMirror"]?["PublishToBotInstanceId"]);
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

        // Both trade their own signals since 2026-08-24. lukas is the unmodified control;
        // futures-live is the experiment arm and carries the two held-out-validated
        // subtractions plus the tighter spread gate.
        Assert.True(primary["Futures"]?["OwnSignalEntriesEnabled"]?.GetValue<bool>());
        Assert.True(lukas["Futures"]?["OwnSignalEntriesEnabled"]?.GetValue<bool>());
        Assert.Equal(
            new[] { "Continuation", "Reclaim" },
            primary["Futures"]?["DisabledLongEntryChannels"]?.AsArray().Select(node => node?.GetValue<string>()).ToArray());
        Assert.Equal(0m, primary["Shorts"]?["MaxBtc24hRisePercentForShort"]?.GetValue<decimal>());
        // The spread ceiling is back to the control's 0.25 after a day at 0.08 showed
        // it was cutting four fifths of the universe: the median decision carries a
        // 0.308% spread. It was the arm's only change with no held-out evidence, so it
        // is also the only one worth undoing on a single day's observation.
        Assert.Equal(
            lukas["Strategy"]?["MaxEntrySpreadPercent"]?.GetValue<decimal>(),
            primary["Strategy"]?["MaxEntrySpreadPercent"]?.GetValue<decimal>());
        // The whole exit triple diverges again since 2026-08-28, at the owner's
        // direction: the arm runs 3.5/1.75/0.5 (activation/stop/trail) against the
        // control's 4/2/0.75 for the next comparison. All three are pinned so any
        // further drift remains an explicit decision.
        Assert.Equal(3.5m, primary["TpSl"]?["TakeProfitPercent"]?.GetValue<decimal>());
        Assert.Equal(1.75m, primary["TpSl"]?["StopLossPercent"]?.GetValue<decimal>());
        Assert.Equal(0.5m, primary["TpSl"]?["TrailingStopPercent"]?.GetValue<decimal>());
        Assert.Equal(4m, lukas["TpSl"]?["TakeProfitPercent"]?.GetValue<decimal>());
        Assert.Equal(2m, lukas["TpSl"]?["StopLossPercent"]?.GetValue<decimal>());
        // The arm floors its 0.5% trail at twice the live spread, because at that
        // distance the bid-ask bounce closes the position instead of the reversal.
        // The control has no such floor - its 0.75% trail is far clear of the book.
        Assert.Equal(2m, primary["TpSl"]?["TrailingStopMinSpreadMultiple"]?.GetValue<decimal>());
        Assert.Null(lukas["TpSl"]?["TrailingStopMinSpreadMultiple"]);
        // The arm now runs the LUKO-style exit: hold until the entry signal fades,
        // and NO max-hold. Measured 2026-08-30 as the best exit on the arm's own
        // 45-day entries (+0.04%/coin-day, the only policy positive in both halves).
        Assert.True(primary["Exits"]?["SignalReversalExitEnabled"]?.GetValue<bool>());
        Assert.Equal(0m, primary["Exits"]?["MaxHoldTrailingStopPercent"]?.GetValue<decimal>());
        Assert.Null(lukas["Exits"]?["SignalReversalExitEnabled"]);
        // The control carries neither knob: the experiment must never leak into it.
        Assert.Null(lukas["Futures"]?["DisabledLongEntryChannels"]);
        Assert.Null(lukas["Shorts"]?["MaxBtc24hRisePercentForShort"]);

        // The Reversal book runs on the arm only, since 2026-08-28: the section is
        // pinned PRESENT and enabled on the arm and pinned ABSENT on the control,
        // whose binder default is disabled. A section appearing on the control is an
        // experiment leak, not a tidy-up.
        Assert.True(primary["Reversal"]?["Enabled"]?.GetValue<bool>());
        Assert.Null(lukas["Reversal"]);

        // Both announce since 2026-08-24, each under its own label - the label is what
        // keeps two voices in one channel from reading as one bot contradicting itself.
        Assert.True(lukas["Telegram"]?["Enabled"]?.GetValue<bool>());
        Assert.True(primary["Telegram"]?["Enabled"]?.GetValue<bool>());
        Assert.Equal("LUKO", lukas["Telegram"]?["Label"]?.GetValue<string>());
        Assert.Equal("BYKO", primary["Telegram"]?["Label"]?.GetValue<string>());
        Assert.Equal(
            primary["Telegram"]?["ChatId"]?.GetValue<string>(),
            lukas["Telegram"]?["ChatId"]?.GetValue<string>());

        // The token is a credential and never appears in a tracked file.
        Assert.Null(primary["Telegram"]?["BotToken"]);
        Assert.Null(lukas["Telegram"]?["BotToken"]);

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
            ("Telegram", "Label"),
            // The face at the head of every post, one per instance, same reason as the label.
            ("Telegram", "Emoji"),
            // The experiment arm's own knobs; absent on the control by design.
            ("Futures", "DisabledLongEntryChannels"),
            ("Shorts", "MaxBtc24hRisePercentForShort"),
            ("Shorts", "RequireBtc4hDropPercent"),
            // The whole exit triple diverges since 2026-08-28 at the owner's direction:
            // the arm runs 3.5/1.75/0.5 (activation/stop/trail) against the control's
            // 4/2/0.75 for the next comparison.
            ("TpSl", "TakeProfitPercent"),
            ("TpSl", "StopLossPercent"),
            ("TpSl", "TrailingStopPercent"),
            ("TpSl", "TrailingStopMinSpreadMultiple"),
            ("Exits", "SignalReversalExitEnabled"),
            // Max hold judged by whether the position is still leading, not by where the
            // price sits relative to entry. Arm only; the control keeps the old rule, and
            // the knob at zero is what leaves it untouched.
            ("Exits", "MaxHoldTrailingStopPercent"),
            // The arm runs wider: five slots, two per correlated sector, the full
            // universe scanned. The totals above already scale per-position x slots.
            ("Trading", "MaxActiveInstruments"),
            ("CorrelationRisk", "MaxOpenPositionsPerGroup"),
            ("CorrelationRisk", "MaxExposureUsdPerGroup"),
        };

        // What each account actually puts in the market, pinned. The sizer takes notional
        // from the RISK budget - TargetRiskUsd / stopPct - and only then applies the caps,
        // so raising leverage on its own moves nothing: it changes the margin behind a
        // notional the risk budget already decided. These three have to agree, or one of
        // them is dead config that reads like a setting.
        foreach (var (profile, name) in new[] { (primary, "futures-live"), (lukas, "futures-lukas-live") })
        {
            var stopPct = profile["TpSl"]!["StopLossPercent"]!.GetValue<decimal>();
            var risk = profile["Risk"]!["TargetRiskUsd"]!.GetValue<decimal>();
            var maxNotional = profile["Futures"]!["MaxNotionalUsd"]!.GetValue<decimal>();
            var marginCap = profile["Futures"]!["MaxMarginPerPositionUsd"]!.GetValue<decimal>();
            var leverage = profile["Futures"]!["MaxLeverage"]!.GetValue<decimal>();

            // What the sizer actually produces at the floor stop, which is the size the
            // account carries in practice. MaxNotionalUsd must equal it: higher and the
            // ceiling never engages, lower and it silently overrides both other knobs.
            var fromRisk = risk / (stopPct / 100m);
            var fromMargin = marginCap * leverage;
            Assert.True(Math.Min(fromRisk, fromMargin) == maxNotional,
                $"{name}: sizes to {Math.Min(fromRisk, fromMargin)} (risk {fromRisk}, margin {fromMargin}) "
                + $"but MaxNotionalUsd says {maxNotional}");
            Assert.Equal(
                maxNotional * profile["Futures"]!["MaxPositions"]!.GetValue<decimal>(),
                profile["Futures"]!["MaxTotalNotionalUsd"]!.GetValue<decimal>());
            Assert.Equal(
                risk * profile["Futures"]!["MaxPositions"]!.GetValue<decimal>(),
                profile["Risk"]!["MaxConcurrentOpenRiskUsd"]!.GetValue<decimal>());
        }

        // Back to the publisher's stake exactly: 15 at 10x on both, 150 of exposure.
        // The 4x afternoon lasted two mirrored entries and cost 24 USD in stop-outs.
        // The arm carries 20 USD margin at 10x = 200 notional across 6 slots; the
        // control keeps the publisher stake of 15 at 10x = 150.
        Assert.Equal(200m, primary["Futures"]?["MaxNotionalUsd"]?.GetValue<decimal>());
        Assert.Equal(150m, lukas["Futures"]?["MaxNotionalUsd"]?.GetValue<decimal>());

        var normalizedLukas = lukas.DeepClone().AsObject();
        normalizedLukas["BotInstance"] = primary["BotInstance"]?.DeepClone();
        normalizedLukas["EntryMirror"] = primary["EntryMirror"]?.DeepClone();
        // Whole-section experiment knob, asserted above; normalized here so the
        // remainder of the two profiles must still match key for key.
        normalizedLukas["Reversal"] = primary["Reversal"]?.DeepClone();
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
