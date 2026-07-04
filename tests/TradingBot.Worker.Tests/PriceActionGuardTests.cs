using TradingBot.Worker;
using Xunit;

namespace TradingBot.Worker.Tests;

// Unit tests for the snapshot price history, the anti-lag LONG entry guard, and the
// layered entry gate (hard filters / quality filters / exploratory admission).
public class PriceActionGuardTests
{
    private static StrategyOptions Strategy() => new()
    {
        MinimumEmaGapPercent = 0.05m,
        MinimumLongScore = 0.90m,
        PriceActionLookbackSnapshots = 6,
        PriceActionMinSnapshots = 4,
        PriceActionMaxDeclinePercent = 0.5m,
        PriceActionMaxNonRisingSnapshots = 3,
        NegativePriceActionPenalty = 0.05m,
        MissingVolumeScoreCap = 0.85m,
        MaxEntrySpreadPercent = 0.5m,
        ExploratoryEntriesEnabled = false,
        ExploratoryMinimumLongScore = 0.85m,
        ExploratoryMaxRank = 2
    };

    private static SnapshotPriceHistory History(params decimal[] lastPrices)
    {
        var history = new SnapshotPriceHistory();
        var start = DateTimeOffset.UtcNow.AddMinutes(-5 * lastPrices.Length);
        for (var i = 0; i < lastPrices.Length; i++)
        {
            history.Record("X/EUR", new PriceObservation(start.AddMinutes(5 * i), lastPrices[i], lastPrices[i] - 0.0001m, lastPrices[i] + 0.0001m));
        }

        return history;
    }

    [Fact]
    public void Assessment_reports_declining_trend()
    {
        var history = History(0.0958m, 0.0952m, 0.0952m, 0.0947m, 0.0947m, 0.0947m, 0.0944m);
        var assessment = history.Assess("X/EUR", lookbackSnapshots: 6, minSnapshots: 4);

        Assert.NotNull(assessment);
        Assert.True(assessment!.DataSufficient);
        Assert.Equal("FALLING", assessment.Direction);
        Assert.True(assessment.TrendPercent < -1m, $"expected < -1%, got {assessment.TrendPercent}");
    }

    [Fact]
    public void Guard_abstains_when_history_is_too_short()
    {
        var history = History(0.0958m, 0.0944m);
        var assessment = history.Assess("X/EUR", lookbackSnapshots: 6, minSnapshots: 4);

        Assert.NotNull(assessment);
        Assert.False(assessment!.DataSufficient);
        Assert.Null(PriceActionGuard.RejectLongEntryReason(assessment, Strategy()));
    }

    [Fact]
    public void Guard_rejects_a_decline_beyond_the_limit()
    {
        var history = History(0.0958m, 0.0952m, 0.0952m, 0.0947m, 0.0947m, 0.0947m, 0.0944m);
        var assessment = history.Assess("X/EUR", lookbackSnapshots: 6, minSnapshots: 4);

        var reason = PriceActionGuard.RejectLongEntryReason(assessment, Strategy());
        Assert.NotNull(reason);
        Assert.Contains("declined", reason);
    }

    [Fact]
    public void Guard_rejects_persistent_stagnation_with_flat_or_negative_trend()
    {
        // Price stalls for 4 consecutive snapshots after a slide: not a fresh breakout.
        var history = History(0.0950m, 0.0949m, 0.0948m, 0.0947m, 0.0947m, 0.0947m, 0.0947m);
        var assessment = history.Assess("X/EUR", lookbackSnapshots: 6, minSnapshots: 4);

        var reason = PriceActionGuard.RejectLongEntryReason(assessment, Strategy());
        Assert.NotNull(reason);
    }

    [Fact]
    public void Guard_allows_a_rising_series()
    {
        var history = History(1.559m, 1.565m, 1.571m, 1.574m, 1.577m, 1.580m, 1.585m);
        var assessment = history.Assess("X/EUR", lookbackSnapshots: 6, minSnapshots: 4);

        Assert.True(assessment!.IsPositive);
        Assert.Null(PriceActionGuard.RejectLongEntryReason(assessment, Strategy()));
    }

    // ---- Entry gate layering ----

    private static InstrumentMarketState MarketState(decimal bid, decimal ask, decimal last, string status = "online") => new()
    {
        Instrument = new InstrumentOptions { Pair = "X/EUR", KrakenPair = "XEUR", Venue = "Kraken", Enabled = true },
        Candles = Enumerable.Range(0, 60)
            .Select(index => new Candle(DateTimeOffset.UtcNow.AddMinutes(-(60 - index)), last, last, last, last, 100m, 10))
            .ToArray(),
        Quote = new Quote(bid, ask, last, 100_000m),
        PairRules = new PairRules("X/EUR", status, 0.001m, 0.5m, 8, 4)
    };

    private static TechnicalSignal Signal(decimal score, bool allowsLong = true, decimal uncapped = 0m, bool volumeConfirmed = true) =>
        new(score, "LONG_BIAS", allowsLong, 0.4m, Array.Empty<SignalContribution>(), uncapped == 0m ? score : uncapped, volumeConfirmed);

    [Fact]
    public void Gate_rejects_wide_spread_as_hard_filter()
    {
        // M/EUR case from the replay: spread 0.827% > max 0.5% must be an explicit
        // hard-filter rejection even with a 0.95 score.
        var result = EntryGate.Evaluate(
            Signal(0.95m),
            MarketState(bid: 1.40896m, ask: 1.42066m, last: 1.41055m),
            priceAction: null,
            Strategy());

        Assert.Equal("NONE", result.DesiredPosition);
        Assert.Equal(EntryRejection.SpreadTooWide, result.RejectionReason);
        Assert.Contains(result.Notes, note => note.Name == "Friction");
    }

    [Fact]
    public void Gate_rejects_offline_pair()
    {
        var result = EntryGate.Evaluate(
            Signal(0.95m),
            MarketState(bid: 1.0m, ask: 1.001m, last: 1.0m, status: "maintenance"),
            priceAction: null,
            Strategy());

        Assert.Equal(EntryRejection.PairUnavailable, result.RejectionReason);
    }

    [Fact]
    public void Gate_rejects_low_liquidity_when_configured()
    {
        var strategy = Strategy();
        strategy.MinDailyVolumeEur = 1_000_000m;
        var result = EntryGate.Evaluate(
            Signal(0.95m),
            MarketState(bid: 1.0m, ask: 1.001m, last: 1.0m),
            priceAction: null,
            strategy);

        Assert.Equal(EntryRejection.LowLiquidity, result.RejectionReason);
    }

    [Fact]
    public void Gate_rejects_firm_score_with_falling_recent_price_action()
    {
        // The OP/EUR failure mode: high (lagging) score while snapshots already fall.
        var priceAction = new PriceActionAssessment("X/EUR", 7, true, 0.0944m, -1.46m, 0.0949m, 4);
        var result = EntryGate.Evaluate(
            Signal(0.95m),
            MarketState(bid: 0.0947m, ask: 0.0949m, last: 0.0944m),
            priceAction,
            Strategy());

        Assert.Equal("NONE", result.DesiredPosition);
        Assert.Equal(EntryRejection.NegativeRecentPriceAction, result.RejectionReason);
    }

    [Fact]
    public void Gate_labels_capped_missing_volume_score_with_explicit_reason()
    {
        // Uncapped 0.95 but volume missing -> capped to 0.85 -> not a firm entry, and
        // the rejection says WHY: no volume confirmation (not a generic low score).
        var result = EntryGate.Evaluate(
            Signal(score: 0.85m, uncapped: 0.95m, volumeConfirmed: false),
            MarketState(bid: 1.0m, ask: 1.001m, last: 1.0m),
            priceAction: null,
            Strategy());

        Assert.Equal("NONE", result.DesiredPosition);
        Assert.Equal(EntryRejection.NoVolumeConfirmation, result.RejectionReason);
    }

    [Fact]
    public void Exploratory_mode_admits_near_threshold_score_only_with_positive_price_action()
    {
        var strategy = Strategy();
        strategy.ExploratoryEntriesEnabled = true;

        var rising = new PriceActionAssessment("X/EUR", 7, true, 1.585m, 0.9m, 1.575m, 0);
        var admitted = EntryGate.Evaluate(
            Signal(0.85m, volumeConfirmed: false),
            MarketState(bid: 1.584m, ask: 1.586m, last: 1.585m),
            rising,
            strategy);

        Assert.Equal("LONG_MICRO", admitted.DesiredPosition);
        Assert.True(admitted.Exploratory);

        var falling = new PriceActionAssessment("X/EUR", 7, true, 1.560m, -0.8m, 1.575m, 3);
        var refused = EntryGate.Evaluate(
            Signal(0.85m, volumeConfirmed: false),
            MarketState(bid: 1.559m, ask: 1.561m, last: 1.560m),
            falling,
            strategy);

        Assert.Equal("NONE", refused.DesiredPosition);
        Assert.Equal(EntryRejection.ExploratoryRequiresPositivePriceAction, refused.RejectionReason);
    }

    [Fact]
    public void Exploratory_mode_refuses_when_price_action_history_is_insufficient()
    {
        // SOL/EUR lesson: a bare 0.85 score with no positive price-action evidence
        // must NOT be admitted just because the exploratory threshold is 0.85 — and
        // the rejection must name the real blocker: the price action is UNKNOWN.
        var strategy = Strategy();
        strategy.ExploratoryEntriesEnabled = true;

        var insufficient = new PriceActionAssessment("X/EUR", 1, false, 72.65m, null, null, 0);
        var result = EntryGate.Evaluate(
            Signal(0.85m, volumeConfirmed: false),
            MarketState(bid: 72.62m, ask: 72.63m, last: 72.65m),
            insufficient,
            strategy);

        Assert.Equal("NONE", result.DesiredPosition);
        Assert.Equal(EntryRejection.PriceActionUnknown, result.RejectionReason);
    }

    [Fact]
    public void Live_mode_disables_exploratory_entries_unless_explicitly_allowed()
    {
        var config = new BotConfiguration
        {
            Trading = new TradingOptions { LiveTradingEnabled = true },
            Strategy = new StrategyOptions { ExploratoryEntriesEnabled = true, ExploratoryAllowedInLive = false }
        };

        InvokeNormalize(config);
        Assert.False(config.Strategy.ExploratoryEntriesEnabled);

        var optIn = new BotConfiguration
        {
            Trading = new TradingOptions { LiveTradingEnabled = true },
            Strategy = new StrategyOptions { ExploratoryEntriesEnabled = true, ExploratoryAllowedInLive = true }
        };

        InvokeNormalize(optIn);
        Assert.True(optIn.Strategy.ExploratoryEntriesEnabled);
    }

    private static void InvokeNormalize(BotConfiguration config)
    {
        var method = typeof(BotConfiguration).GetMethod(
            "Normalize",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(config, null);
    }
}
