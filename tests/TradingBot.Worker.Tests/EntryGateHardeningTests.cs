using TradingBot.Worker;
using Xunit;

namespace TradingBot.Worker.Tests;

// Live-mode metadata hardening and the price-action warm-up block:
//   - live entries require pair rules, an explicit "online" status, and a valid
//     bid/ask book; anything missing or invalid is a hard rejection.
//   - dry-run stays lenient for synthetic sources but still rejects KNOWN
//     non-tradable Kraken statuses.
//   - RequirePriceActionData blocks entries until the anti-lag guard has enough
//     snapshots to judge them (forced on in live mode by Normalize()).
public class EntryGateHardeningTests
{
    private static StrategyOptions Strategy() => new()
    {
        MinimumLongScore = 0.90m,
        MaxEntrySpreadPercent = 0.5m,
        PriceActionLookbackSnapshots = 6,
        PriceActionMinSnapshots = 4,
        PriceActionMaxDeclinePercent = 0.5m,
        PriceActionMaxNonRisingSnapshots = 3
    };

    private static TechnicalSignal FirmSignal() =>
        new(0.95m, "LONG_BIAS", true, true, true, 0.5m, null, Array.Empty<SignalContribution>(), UncappedScore: 0.95m, VolumeConfirmed: true);

    private static PriceActionAssessment Rising(int snapshots = 6) => new(
        "TEST/EUR",
        SnapshotCount: snapshots,
        DataSufficient: snapshots >= 4,
        LastPrice: 1.01m,
        TrendPercent: 0.5m,
        RollingAveragePrice: 1.00m,
        ConsecutiveNonRisingSnapshots: 0);

    private static InstrumentMarketState MarketState(
        PairRules? rules,
        Quote? quote,
        decimal last = 1.0m) => new()
    {
        Instrument = new InstrumentOptions { Pair = "TEST/EUR", KrakenPair = "TESTEUR", Venue = "Kraken", Enabled = true },
        Candles = new[] { new Candle(DateTimeOffset.UtcNow, last, last, last, last, 100m, 10) },
        Quote = quote,
        PairRules = rules
    };

    private static PairRules Rules(string status) => new("TEST/EUR", status, 0.001m, 0.5m, 8, 5);
    private static Quote GoodQuote() => new(0.999m, 1.001m, 1.0m, 1_000_000m);

    // ---------------------------------------------------------------- live metadata

    [Fact]
    public void Live_entry_is_rejected_when_pair_rules_are_missing()
    {
        var gate = EntryGate.Evaluate(FirmSignal(), MarketState(rules: null, GoodQuote()), Rising(), Strategy(), liveMode: true);

        Assert.Equal("NONE", gate.DesiredPosition);
        Assert.Equal(EntryRejection.PairUnavailable, gate.RejectionReason);
    }

    [Theory]
    [InlineData("sample")]
    [InlineData("cancel_only")]
    [InlineData("maintenance")]
    [InlineData("unknown_new_status")]
    public void Live_entry_is_rejected_for_any_status_other_than_online(string status)
    {
        var gate = EntryGate.Evaluate(FirmSignal(), MarketState(Rules(status), GoodQuote()), Rising(), Strategy(), liveMode: true);

        Assert.Equal("NONE", gate.DesiredPosition);
        Assert.Equal(EntryRejection.PairUnavailable, gate.RejectionReason);
    }

    [Fact]
    public void Live_entry_is_rejected_when_the_quote_is_missing()
    {
        var gate = EntryGate.Evaluate(FirmSignal(), MarketState(Rules("online"), quote: null), Rising(), Strategy(), liveMode: true);

        Assert.Equal("NONE", gate.DesiredPosition);
        Assert.Equal(EntryRejection.InvalidMarketData, gate.RejectionReason);
    }

    [Theory]
    [InlineData(0, 1.001)]   // no bid
    [InlineData(0.999, 0)]   // no ask
    [InlineData(1.002, 1.0)] // crossed book (ask < bid)
    public void Live_entry_is_rejected_when_bid_ask_is_invalid(decimal bid, decimal ask)
    {
        var gate = EntryGate.Evaluate(FirmSignal(), MarketState(Rules("online"), new Quote(bid, ask, 1.0m, 1_000_000m)), Rising(), Strategy(), liveMode: true);

        Assert.Equal("NONE", gate.DesiredPosition);
        Assert.Equal(EntryRejection.InvalidMarketData, gate.RejectionReason);
    }

    [Fact]
    public void Live_entry_passes_with_online_status_and_a_valid_book()
    {
        var gate = EntryGate.Evaluate(FirmSignal(), MarketState(Rules("online"), GoodQuote()), Rising(), Strategy(), liveMode: true);

        Assert.Equal("LONG_MICRO", gate.DesiredPosition);
        Assert.Null(gate.RejectionReason);
    }

    [Fact]
    public void Dry_run_accepts_the_synthetic_sample_status_but_still_rejects_known_bad_statuses()
    {
        var strategy = Strategy();

        var sample = EntryGate.Evaluate(FirmSignal(), MarketState(Rules("sample"), GoodQuote()), Rising(), strategy, liveMode: false);
        Assert.Equal("LONG_MICRO", sample.DesiredPosition);

        var cancelOnly = EntryGate.Evaluate(FirmSignal(), MarketState(Rules("cancel_only"), GoodQuote()), Rising(), strategy, liveMode: false);
        Assert.Equal(EntryRejection.PairUnavailable, cancelOnly.RejectionReason);
    }

    // ---------------------------------------------------------------- warm-up block

    [Fact]
    public void Warmup_blocks_a_firm_volume_confirmed_entry_until_history_is_sufficient()
    {
        var strategy = Strategy();
        strategy.RequirePriceActionData = true;

        // Fresh restart: no assessment at all.
        var noHistory = EntryGate.Evaluate(FirmSignal(), MarketState(Rules("online"), GoodQuote()), null, strategy);
        Assert.Equal(EntryRejection.InsufficientPriceHistory, noHistory.RejectionReason);

        // A couple of cycles in: some observations, still below the minimum.
        var partial = EntryGate.Evaluate(FirmSignal(), MarketState(Rules("online"), GoodQuote()), Rising(snapshots: 2), strategy);
        Assert.Equal(EntryRejection.InsufficientPriceHistory, partial.RejectionReason);

        // Warm-up complete: the same entry passes.
        var warmed = EntryGate.Evaluate(FirmSignal(), MarketState(Rules("online"), GoodQuote()), Rising(snapshots: 6), strategy);
        Assert.Equal("LONG_MICRO", warmed.DesiredPosition);
    }

    [Fact]
    public void Without_the_warmup_requirement_missing_history_abstains_as_before()
    {
        var strategy = Strategy();
        strategy.RequirePriceActionData = false;

        var gate = EntryGate.Evaluate(FirmSignal(), MarketState(Rules("online"), GoodQuote()), null, strategy);
        Assert.Equal("LONG_MICRO", gate.DesiredPosition);
    }

    [Fact]
    public void Normalize_forces_the_warmup_requirement_on_in_live_mode()
    {
        var live = new BotConfiguration
        {
            Trading = new TradingOptions { LiveTradingEnabled = true },
            Strategy = new StrategyOptions { RequirePriceActionData = false }
        };
        InvokeNormalize(live);
        Assert.True(live.Strategy.RequirePriceActionData);

        var dryRun = new BotConfiguration
        {
            Trading = new TradingOptions { LiveTradingEnabled = false },
            Strategy = new StrategyOptions { RequirePriceActionData = false }
        };
        InvokeNormalize(dryRun);
        Assert.False(dryRun.Strategy.RequirePriceActionData);
    }

    private static void InvokeNormalize(BotConfiguration config)
    {
        typeof(BotConfiguration).GetMethod(
            "Normalize",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(config, null);
    }
}
