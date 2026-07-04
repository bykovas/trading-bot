using System.Globalization;
using TradingBot.Worker;
using Xunit;

namespace TradingBot.Worker.Tests;

// Replay validation against the real dry-run export captured on 2026-07-04
// (TestData/replay-cycles-snapshots.csv: 49 cycles, 50 pairs, ~2450 light market
// snapshots from Kraken). These tests replay the ACTUAL snapshot series into the
// new price-action layer and assert the behavior the export showed was wrong is
// now corrected:
//
//   - OP/EUR:  score 0.95 buy at 06:04 UTC into visibly falling prices, ridden to a
//              -2.6% stop-loss. Must now be rejected at entry (anti-lag guard +
//              missing-volume cap) and, if entered anyway, exited early on decay.
//   - TON/EUR: repeated 0.80-0.85 scores while rising; must be admissible as an
//              exploratory candidate when its recent price action is positive.
//   - SOL/EUR: 0.85 score early in the window that later faded; shows why a flat
//              threshold cut to 0.85 is unsafe without price-action confirmation.
//   - M/EUR:   0.95 score with a 0.827% spread; must carry the explicit
//              REJECT_SPREAD_TOO_WIDE reason.
public class CsvReplayValidationTests
{
    private static readonly string CsvPath = Path.Combine(AppContext.BaseDirectory, "TestData", "replay-cycles-snapshots.csv");

    private static StrategyOptions ReplayStrategy() => new()
    {
        MinimumEmaGapPercent = 0.35m,
        MinimumLongScore = 0.90m,
        MaxEntrySpreadPercent = 0.5m,
        PriceActionLookbackSnapshots = 6,
        PriceActionMinSnapshots = 4,
        PriceActionMaxDeclinePercent = 0.5m,
        PriceActionMaxNonRisingSnapshots = 3,
        NegativePriceActionPenalty = 0.05m,
        MissingVolumeScoreCap = 0.85m,
        ExploratoryEntriesEnabled = true,
        ExploratoryMinimumLongScore = 0.85m,
        ExploratoryMaxRank = 2
    };

    // Parses market_snapshot rows for one pair, in file (chronological) order.
    private static List<(DateTimeOffset Utc, decimal Bid, decimal Ask, decimal Last)> SnapshotsFor(string pair)
    {
        Assert.True(File.Exists(CsvPath), $"replay fixture missing at {CsvPath}");
        var rows = new List<(DateTimeOffset, decimal, decimal, decimal)>();
        foreach (var line in File.ReadLines(CsvPath))
        {
            if (!line.StartsWith("\"market_snapshot\"", StringComparison.Ordinal))
            {
                continue;
            }

            // Snapshot rows contain no embedded commas: safe to split directly.
            var fields = line.Split(',').Select(field => field.Trim('"')).ToArray();
            if (!fields[3].Equals(pair, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rows.Add((
                DateTimeOffset.Parse(fields[2], CultureInfo.InvariantCulture),
                decimal.Parse(fields[4], CultureInfo.InvariantCulture),
                decimal.Parse(fields[5], CultureInfo.InvariantCulture),
                decimal.Parse(fields[6], CultureInfo.InvariantCulture)));
        }

        return rows.OrderBy(row => row.Item1).ToList();
    }

    // Replays a pair's snapshots into the history up to (and including) the snapshot
    // nearest the given UTC time, then returns the price-action assessment there.
    // CSV timestamps carry fractional seconds, so comparisons use a 1-minute window.
    private static PriceActionAssessment? AssessAt(string pair, DateTimeOffset atUtc, StrategyOptions strategy)
    {
        var history = new SnapshotPriceHistory();
        foreach (var (utc, bid, ask, last) in SnapshotsFor(pair))
        {
            if (utc > atUtc.AddMinutes(1))
            {
                break;
            }

            history.Record(pair, new PriceObservation(utc, last, bid, ask));
        }

        return history.Assess(pair, strategy.PriceActionLookbackSnapshots, strategy.PriceActionMinSnapshots);
    }

    private static (DateTimeOffset Utc, decimal Bid, decimal Ask, decimal Last) SnapshotNear(string pair, DateTimeOffset atUtc) =>
        SnapshotsFor(pair).First(row => Math.Abs((row.Utc - atUtc).TotalSeconds) < 60);

    private static InstrumentMarketState MarketState(string pair, decimal bid, decimal ask, decimal last) => new()
    {
        Instrument = new InstrumentOptions { Pair = pair, KrakenPair = pair.Replace("/", string.Empty), Venue = "Kraken", Enabled = true },
        Candles = Enumerable.Range(0, 60)
            .Select(index => new Candle(DateTimeOffset.UtcNow.AddMinutes(-(60 - index) * 15), last, last, last, last, 100m, 10))
            .ToArray(),
        Quote = new Quote(bid, ask, last, 1_000_000m),
        PairRules = new PairRules(pair, "online", 1m, 0.5m, 8, 5)
    };

    [Fact]
    public void OP_EUR_entry_is_rejected_by_the_anti_lag_price_action_guard()
    {
        var strategy = ReplayStrategy();
        var buyTime = DateTimeOffset.Parse("2026-07-04T06:04:44Z", CultureInfo.InvariantCulture);
        var assessment = AssessAt("OP/EUR", buyTime, strategy);

        // The replayed series (0.0958 -> 0.0944) must read as falling.
        Assert.NotNull(assessment);
        Assert.True(assessment!.DataSufficient);
        Assert.True(assessment.TrendPercent < 0m, $"expected falling trend, got {assessment.TrendPercent}%");

        // The guard alone must reject the entry.
        Assert.NotNull(PriceActionGuard.RejectLongEntryReason(assessment, strategy));

        // And the full gate: even granting the original (lagging) 0.95-style signal,
        // OP/EUR is not a firm entry any more. With the missing-volume cap the score
        // is 0.85 (exploratory band), and the gate must name the real blocker: the
        // known-negative price action fails the positive-price-action requirement.
        var signal = new TechnicalSignal(0.85m, "LONG_BIAS", true, true, true, 0.4m, null, Array.Empty<SignalContribution>(), UncappedScore: 0.95m, VolumeConfirmed: false);
        var buySnapshot = SnapshotNear("OP/EUR", buyTime);
        var gate = EntryGate.Evaluate(signal, MarketState("OP/EUR", buySnapshot.Bid, buySnapshot.Ask, buySnapshot.Last), assessment, strategy);

        Assert.Equal("NONE", gate.DesiredPosition);
        Assert.Equal(EntryRejection.ExploratoryRequiresPositivePriceAction, gate.RejectionReason);
    }

    [Fact]
    public void OP_EUR_would_have_exited_on_score_decay_instead_of_riding_to_stop_loss()
    {
        // The export shows the score falling 0.95 -> 0.50 -> 0.40 after entry while
        // the position sank. Under the decay rules the position is cut at the second
        // low-score cycle (or immediately at 0.40), far above the -2.5% stop.
        var exit = new PositionExitOptions
        {
            StopLossPercent = 2.5m,
            TakeProfitPercent = 3.0m,
            MaxHoldMinutes = 360,
            ScoreDecayMinEntryScore = 0.90m,
            ScoreDecayDefensiveScore = 0.50m,
            ScoreDecayDefensiveCycles = 2,
            ScoreDecayImmediateScore = 0.40m
        };

        var atScore040 = PositionExitPolicy.EvaluateHeldPosition(
            desiredLong: true,
            positionAgeSeconds: 3600,
            conservativeUnrealizedPnlPercent: -1.1m,
            canValuePosition: true,
            killSwitchActive: false,
            new ExecutionPolicyOptions { MinHoldSeconds = 1800 },
            exit,
            exitHysteresisEnabled: true,
            scoreDecay: new ScoreDecaySnapshot(0.95m, 0.40m, 1, ScoreConfirmsEntry: false));

        Assert.True(atScore040.ShouldSell);
        Assert.Equal(ExitReason.ScoreDecay, atScore040.ExitReason);
    }

    [Fact]
    public void TON_EUR_is_an_admissible_exploratory_candidate_while_rising()
    {
        var strategy = ReplayStrategy();

        // 07:49 UTC: TON/EUR had been scoring 0.80-0.85, its snapshots were rising
        // (1.571 -> 1.588 over the prior half hour) and the spread (0.126%) is inside
        // the stricter exploratory limit. (At 07:46 the spread was 0.316%, which the
        // exploratory spread cap now correctly refuses.)
        var atUtc = DateTimeOffset.Parse("2026-07-04T07:49:43Z", CultureInfo.InvariantCulture);
        var assessment = AssessAt("TON/EUR", atUtc, strategy);

        Assert.NotNull(assessment);
        Assert.True(assessment!.IsPositive, $"expected positive TON/EUR price action, got {assessment.TrendPercent}% ({assessment.Direction})");

        var signal = new TechnicalSignal(0.85m, "LONG_BIAS", true, true, true, 0.4m, null, Array.Empty<SignalContribution>(), 0.85m, VolumeConfirmed: true);
        var snapshot = SnapshotNear("TON/EUR", atUtc);
        var gate = EntryGate.Evaluate(signal, MarketState("TON/EUR", snapshot.Bid, snapshot.Ask, snapshot.Last), assessment, strategy);

        Assert.Equal("LONG_MICRO", gate.DesiredPosition);
        Assert.True(gate.Exploratory, "TON/EUR should enter through the exploratory (not firm) path");
    }

    [Fact]
    public void SOL_EUR_shows_that_a_flat_0_85_threshold_would_be_unsafe()
    {
        var strategy = ReplayStrategy();

        // First cycle of the window: SOL/EUR scored 0.85 (no volume confirmation) but
        // there is no price-action evidence yet -> exploratory admission must refuse.
        var firstCycle = DateTimeOffset.Parse("2026-07-04T05:33:35Z", CultureInfo.InvariantCulture);
        var early = AssessAt("SOL/EUR", firstCycle.AddSeconds(1), strategy);
        var signal = new TechnicalSignal(0.85m, "LONG_BIAS", true, true, true, 0.4m, null, Array.Empty<SignalContribution>(), 0.85m, VolumeConfirmed: false);
        var earlySnapshot = SnapshotsFor("SOL/EUR").First();
        var earlyGate = EntryGate.Evaluate(signal, MarketState("SOL/EUR", earlySnapshot.Bid, earlySnapshot.Ask, earlySnapshot.Last), early, strategy);

        Assert.Equal("NONE", earlyGate.DesiredPosition);

        // The full window confirms the danger: SOL/EUR ended LOWER than it started,
        // so a naive "score >= 0.85 buys" rule would have entered a fading market.
        var series = SnapshotsFor("SOL/EUR");
        Assert.True(series[^1].Last < series[0].Last, $"expected SOL/EUR to fade over the window ({series[0].Last} -> {series[^1].Last})");
    }

    [Fact]
    public void M_EUR_is_rejected_explicitly_for_its_wide_spread()
    {
        var strategy = ReplayStrategy();

        // 07:38 UTC: M/EUR scored 0.95 but its book was 1.40896 / 1.42066 -> 0.827%.
        var atUtc = DateTimeOffset.Parse("2026-07-04T07:38:41Z", CultureInfo.InvariantCulture);
        var snapshot = SnapshotNear("M/EUR", atUtc);
        var spread = (snapshot.Ask - snapshot.Bid) / ((snapshot.Ask + snapshot.Bid) / 2m) * 100m;
        Assert.True(spread > strategy.MaxEntrySpreadPercent, $"fixture sanity: expected wide spread, got {spread}%");

        var signal = new TechnicalSignal(0.95m, "LONG_BIAS", true, true, true, 0.6m, null, Array.Empty<SignalContribution>(), 0.95m, VolumeConfirmed: true);
        var gate = EntryGate.Evaluate(
            signal,
            MarketState("M/EUR", snapshot.Bid, snapshot.Ask, snapshot.Last),
            AssessAt("M/EUR", atUtc, strategy),
            strategy);

        Assert.Equal("NONE", gate.DesiredPosition);
        Assert.Equal(EntryRejection.SpreadTooWide, gate.RejectionReason);
    }

    [Fact]
    public void Replay_fixture_matches_the_reported_shape()
    {
        // Guard against fixture drift: ~49 cycles and ~50 snapshot pairs per cycle.
        var lines = File.ReadLines(CsvPath).ToList();
        var cycleCount = lines.Count(line => line.StartsWith("\"cycle\"", StringComparison.Ordinal));
        var snapshotCount = lines.Count(line => line.StartsWith("\"market_snapshot\"", StringComparison.Ordinal));

        Assert.Equal(49, cycleCount);
        Assert.Equal(2450, snapshotCount);
    }
}
