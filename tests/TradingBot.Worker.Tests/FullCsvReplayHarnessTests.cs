using System.Globalization;
using System.Text.Json;
using TradingBot.Worker;
using Xunit;

namespace TradingBot.Worker.Tests;

// END-TO-END replay of the full captured export (TestData/replay-cycles-snapshots.csv:
// 49 cycles, ~50 pairs, ~2450 light snapshots, including the real OP/EUR buy and its
// stop-loss). Unlike the targeted CsvReplayValidationTests, this harness replays EVERY
// cycle in chronological order through the REAL components:
//
//   snapshots -> SnapshotPriceHistory -> PriceActionAssessment
//   recorded per-pair signals (score + contributions) -> EntryGate.Evaluate
//   eligible candidates -> EntryRanking.Rank + exploratory top-N + per-cycle cap
//   the recorded OP/EUR position -> PositionExitPolicy.EvaluateHeldPosition per cycle
//
// The recorded scores come from the OLD engine (no volume cap, no price-action
// penalty), so the harness re-applies exactly the two scoring adjustments the new
// engine adds before gating. Everything downstream is production code.
public class FullCsvReplayHarnessTests
{
    private static readonly string CsvPath = Path.Combine(AppContext.BaseDirectory, "TestData", "replay-cycles-snapshots.csv");

    // Mirrors src/TradingBot.Worker/appsettings.json (dry-run profile).
    private static StrategyOptions ReplayStrategy() => new()
    {
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

    private static PositionExitOptions ReplayExitOptions() => new()
    {
        StopLossPercent = 2.5m,
        TakeProfitPercent = 3.0m,
        MaxHoldMinutes = 360,
        MinProfitToExitOnSignalFlipPercent = 1.0m,
        MaxSignalFlipLossExitPercent = -1.2m,
        ScoreDecayMinEntryScore = 0.90m,
        ScoreDecayDefensiveScore = 0.50m,
        ScoreDecayDefensiveCycles = 2,
        ScoreDecayImmediateScore = 0.40m,
        PostEntryAdverseWindowMinutes = 30,
        PostEntryAdverseLossPercent = 2.0m
    };

    private const int MaxNewPositionsPerCycle = 1;
    private const decimal FeeRate = 26m / 10_000m;      // DryRun.TakerFeeBps
    private const decimal SlippageRate = 10m / 10_000m; // DryRun.SlippageBps

    // ---------------------------------------------------------------- fixture model

    private sealed record ReplaySnapshot(string Pair, decimal Bid, decimal Ask, decimal Last, decimal Volume24h);

    private sealed record ReplayDecision(
        string Pair,
        decimal Score,
        decimal? Rsi,
        decimal? FastEma,
        decimal? SlowEma,
        bool AllowsLong,
        bool VolumeConfirmed,
        string RecordedAction,
        decimal RecordedFillPrice,
        decimal RecordedQuantity);

    private sealed record ReplayCycle(
        DateTimeOffset Utc,
        string CycleId,
        IReadOnlyList<ReplayDecision> Decisions,
        IReadOnlyDictionary<string, ReplaySnapshot> Snapshots);

    // Outcome of one pair's entry evaluation in one replayed cycle.
    private sealed record GateOutcome(
        DateTimeOffset Utc,
        string Pair,
        decimal AdjustedScore,
        string DesiredPosition,
        bool Exploratory,
        string? RejectionReason,
        bool Admitted);

    // ---------------------------------------------------------------- fixture parsing

    private static List<ReplayCycle> LoadCycles()
    {
        Assert.True(File.Exists(CsvPath), $"replay fixture missing at {CsvPath}");

        var cycleRecords = new List<(DateTimeOffset Utc, string CycleId, JsonElement Record)>();
        var snapshotsByCycle = new Dictionary<string, Dictionary<string, ReplaySnapshot>>();

        foreach (var line in File.ReadLines(CsvPath).Skip(1))
        {
            var fields = ParseCsvLine(line);
            switch (fields[0])
            {
                case "cycle":
                {
                    var record = JsonDocument.Parse(fields[9]).RootElement.Clone();
                    cycleRecords.Add((
                        DateTimeOffset.Parse(fields[2], CultureInfo.InvariantCulture),
                        fields[1],
                        record));
                    break;
                }
                case "market_snapshot":
                {
                    if (!snapshotsByCycle.TryGetValue(fields[1], out var snapshots))
                    {
                        snapshots = new Dictionary<string, ReplaySnapshot>(StringComparer.OrdinalIgnoreCase);
                        snapshotsByCycle[fields[1]] = snapshots;
                    }

                    snapshots[fields[3]] = new ReplaySnapshot(
                        fields[3],
                        decimal.Parse(fields[4], CultureInfo.InvariantCulture),
                        decimal.Parse(fields[5], CultureInfo.InvariantCulture),
                        decimal.Parse(fields[6], CultureInfo.InvariantCulture),
                        decimal.Parse(fields[7], CultureInfo.InvariantCulture));
                    break;
                }
            }
        }

        var cycles = new List<ReplayCycle>();
        foreach (var (utc, cycleId, record) in cycleRecords.OrderBy(item => item.Utc))
        {
            var decisions = new List<ReplayDecision>();
            foreach (var decision in record.GetProperty("decisions").EnumerateArray())
            {
                var contributions = decision.GetProperty("contributions").EnumerateArray().ToList();
                var action = decision.GetProperty("dryRunAction");
                decisions.Add(new ReplayDecision(
                    decision.GetProperty("pair").GetString()!,
                    decision.GetProperty("score").GetDecimal(),
                    NullableDecimal(decision, "rsi"),
                    NullableDecimal(decision, "fastEma"),
                    NullableDecimal(decision, "slowEma"),
                    AllowsLong: contributions.Any(c => c.GetProperty("name").GetString() == "EMA" && c.GetProperty("value").GetDecimal() > 0m),
                    VolumeConfirmed: contributions.Any(c => c.GetProperty("name").GetString() == "Volume" && c.GetProperty("value").GetDecimal() > 0m),
                    RecordedAction: action.GetProperty("action").GetString()!,
                    RecordedFillPrice: action.GetProperty("fillPrice").GetDecimal(),
                    RecordedQuantity: action.GetProperty("quantity").GetDecimal()));
            }

            cycles.Add(new ReplayCycle(
                utc,
                cycleId,
                decisions,
                snapshotsByCycle.TryGetValue(cycleId, out var snapshots)
                    ? snapshots
                    : new Dictionary<string, ReplaySnapshot>(StringComparer.OrdinalIgnoreCase)));
        }

        return cycles;
    }

    private static decimal? NullableDecimal(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDecimal()
            : null;

    // Minimal RFC-4180 line parser (quoted fields, "" escapes; records are one line).
    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(ch);
                }
            }
            else if (ch == '"')
            {
                inQuotes = true;
            }
            else if (ch == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }

    // ---------------------------------------------------------------- replay engine

    // Replays every cycle through the real gate/ranking pipeline and returns each
    // pair's per-cycle outcome plus the ordered list of admitted entries.
    private static (List<GateOutcome> Outcomes, List<GateOutcome> Admissions) RunFullReplay(StrategyOptions strategy)
    {
        var cycles = LoadCycles();
        var history = new SnapshotPriceHistory();
        var outcomes = new List<GateOutcome>();
        var admissions = new List<GateOutcome>();

        foreach (var cycle in cycles)
        {
            // Same order as DecisionWorker: snapshots feed the history first.
            foreach (var snapshot in cycle.Snapshots.Values)
            {
                if (snapshot.Last > 0m)
                {
                    history.Record(snapshot.Pair, new PriceObservation(cycle.Utc, snapshot.Last, snapshot.Bid, snapshot.Ask));
                }
            }

            var eligible = new List<(GateOutcome Outcome, EntryCandidate Candidate)>();
            var index = 0;
            foreach (var decision in cycle.Decisions)
            {
                // Held-position rows (hold/sell) are not entry evaluations.
                if (decision.RecordedAction is "WOULD_HOLD" or "WOULD_SELL")
                {
                    continue;
                }

                if (!cycle.Snapshots.TryGetValue(decision.Pair, out var snapshot))
                {
                    continue;
                }

                var assessment = history.Assess(decision.Pair, strategy.PriceActionLookbackSnapshots, strategy.PriceActionMinSnapshots);
                var signal = ReconstructSignal(decision, assessment, strategy);
                var gate = EntryGate.Evaluate(signal, MarketState(snapshot), assessment, strategy);

                var outcome = new GateOutcome(
                    cycle.Utc,
                    decision.Pair,
                    signal.Score,
                    gate.DesiredPosition,
                    gate.Exploratory,
                    gate.RejectionReason,
                    Admitted: false);
                outcomes.Add(outcome);

                if (gate.DesiredPosition == "LONG_MICRO")
                {
                    var emaGap = decision.FastEma is { } fast && decision.SlowEma is { } slow && slow > 0m && fast > slow
                        ? (fast - slow) / slow * 100m
                        : 0m;
                    eligible.Add((outcome, new EntryCandidate(decision.Pair, signal.Score, emaGap, decision.Rsi, 10m, index)));
                }

                index++;
            }

            // Real ranking + the same limits DecisionWorker applies.
            var rankPosition = 0;
            var admittedThisCycle = 0;
            foreach (var ranked in EntryRanking.Rank(eligible.Select(item => item.Candidate)))
            {
                rankPosition++;
                var (outcome, _) = eligible.First(item => ReferenceEquals(item.Candidate, ranked));
                if (outcome.Exploratory && rankPosition > strategy.ExploratoryMaxRank)
                {
                    continue;
                }

                if (admittedThisCycle >= MaxNewPositionsPerCycle)
                {
                    continue;
                }

                admissions.Add(outcome with { Admitted = true });
                admittedThisCycle++;
            }
        }

        return (outcomes, admissions);
    }

    // Re-applies the two scoring adjustments the NEW engine adds on top of the
    // recorded (old-engine) score: the negative-price-action penalty and the
    // missing-volume cap. Everything else about the signal is taken as recorded.
    private static TechnicalSignal ReconstructSignal(ReplayDecision decision, PriceActionAssessment? assessment, StrategyOptions strategy)
    {
        var score = decision.Score;
        if (decision.AllowsLong
            && assessment is { DataSufficient: true, TrendPercent: < 0m }
            && strategy.NegativePriceActionPenalty > 0m)
        {
            score -= strategy.NegativePriceActionPenalty;
        }

        score = Math.Clamp(score, 0m, 1m);
        var uncapped = decimal.Round(score, 2);
        if (decision.AllowsLong && !decision.VolumeConfirmed && strategy.MissingVolumeScoreCap > 0m && uncapped > strategy.MissingVolumeScoreCap)
        {
            score = strategy.MissingVolumeScoreCap;
        }

        return new TechnicalSignal(
            decimal.Round(score, 2),
            decision.AllowsLong ? "LONG_BIAS" : "NEUTRAL",
            decision.AllowsLong,
            decision.AllowsLong,
            decision.AllowsLong,
            null,
            null,
            Array.Empty<SignalContribution>(),
            UncappedScore: uncapped,
            VolumeConfirmed: decision.VolumeConfirmed);
    }

    private static InstrumentMarketState MarketState(ReplaySnapshot snapshot) => new()
    {
        Instrument = new InstrumentOptions
        {
            Pair = snapshot.Pair,
            KrakenPair = snapshot.Pair.Replace("/", string.Empty),
            Venue = "Kraken",
            Enabled = true
        },
        Candles = Array.Empty<Candle>(),
        Quote = new Quote(snapshot.Bid, snapshot.Ask, snapshot.Last, snapshot.Volume24h),
        PairRules = new PairRules(snapshot.Pair, "online", 1m, 0.5m, 8, 5)
    };

    // ---------------------------------------------------------------- assertions

    [Fact]
    public void OP_EUR_is_rejected_at_the_exact_cycle_where_the_recorded_buy_happened()
    {
        var (outcomes, admissions) = RunFullReplay(ReplayStrategy());

        var buyCycle = outcomes.Single(o => o.Pair == "OP/EUR" && o.Utc.ToString("HH:mm:ss") == "06:04:44");
        Assert.Equal("NONE", buyCycle.DesiredPosition);
        // The volume-capped 0.85 score puts OP in the exploratory band, and the
        // FALLING price action fails its positive-price-action requirement.
        Assert.Equal(EntryRejection.ExploratoryRequiresPositivePriceAction, buyCycle.RejectionReason);

        // And across the whole 4-hour window, no OP/EUR entry is ever admitted.
        Assert.DoesNotContain(admissions, a => a.Pair == "OP/EUR");
    }

    [Fact]
    public void TON_EUR_is_admitted_as_an_exploratory_entry_while_its_price_action_is_positive()
    {
        var (_, admissions) = RunFullReplay(ReplayStrategy());

        var tonEntries = admissions.Where(a => a.Pair == "TON/EUR").ToList();
        Assert.NotEmpty(tonEntries);
        Assert.All(tonEntries, a => Assert.True(a.Exploratory, "TON/EUR only qualifies through the exploratory path"));

        // The first admission is at 07:29 UTC, when TON's snapshots were rising —
        // NOT during the earlier 0.80-0.85 cycles where its price action was falling.
        Assert.Equal("07:29:21", tonEntries[0].Utc.ToString("HH:mm:ss"));
    }

    [Fact]
    public void SOL_EUR_is_never_admitted_which_shows_a_flat_0_85_threshold_cut_would_be_unsafe()
    {
        var (outcomes, admissions) = RunFullReplay(ReplayStrategy());

        Assert.DoesNotContain(admissions, a => a.Pair == "SOL/EUR");

        // A naive "score >= 0.85 buys" rule WOULD have entered in the very first
        // cycles (recorded score 0.85), into a market that faded over the window.
        var earlyHighScores = outcomes.Where(o => o.Pair == "SOL/EUR" && o.Utc.Hour == 5).ToList();
        Assert.NotEmpty(earlyHighScores);
        Assert.All(earlyHighScores, o => Assert.Equal("NONE", o.DesiredPosition));
    }

    [Fact]
    public void M_EUR_high_score_cycle_is_rejected_explicitly_for_spread()
    {
        var (outcomes, _) = RunFullReplay(ReplayStrategy());

        var mCycle = outcomes.Single(o => o.Pair == "M/EUR" && o.Utc.ToString("HH:mm:ss") == "07:38:41");
        Assert.Equal("NONE", mCycle.DesiredPosition);
        Assert.Equal(EntryRejection.SpreadTooWide, mCycle.RejectionReason);
    }

    [Fact]
    public void Whole_replay_admits_only_a_handful_of_entries_so_no_fee_burning()
    {
        var (outcomes, admissions) = RunFullReplay(ReplayStrategy());

        // 49 cycles / ~4 hours: a few TON entries instead of total silence, and far
        // away from an entry-per-cycle churn.
        Assert.InRange(admissions.Count, 1, 6);

        // Every evaluated non-admitted pair carries an explicit rejection reason.
        Assert.All(
            outcomes.Where(o => o.DesiredPosition == "NONE"),
            o => Assert.False(string.IsNullOrEmpty(o.RejectionReason)));
    }

    [Fact]
    public void Recorded_OP_position_replayed_through_the_exit_policy_is_cut_by_score_decay_near_minus_one_percent()
    {
        var cycles = LoadCycles();
        var strategy = ReplayStrategy();
        var exitOptions = ReplayExitOptions();
        var executionPolicy = new ExecutionPolicyOptions { MinHoldSeconds = 1800 };
        var history = new SnapshotPriceHistory();

        // The REAL recorded entry: 06:04:44 UTC, fill 0.0949949, quantity from record.
        var entryCycle = cycles.Single(c => c.Utc.ToString("HH:mm:ss") == "06:04:44");
        var entryDecision = entryCycle.Decisions.Single(d => d.Pair == "OP/EUR" && d.RecordedAction == "WOULD_BUY");
        var entryFill = entryDecision.RecordedFillPrice;
        var quantity = entryDecision.RecordedQuantity;
        Assert.True(entryFill > 0m && quantity > 0m, "fixture sanity: recorded OP/EUR buy must carry fill and quantity");
        var entryNotional = quantity * entryFill * (1m + FeeRate);
        var openedAt = entryCycle.Utc;

        var lowScoreCycles = 0;
        ExitEvaluation? exitEvaluation = null;
        DateTimeOffset exitUtc = default;
        decimal exitPnlPercent = 0m;

        foreach (var cycle in cycles)
        {
            foreach (var snapshot in cycle.Snapshots.Values)
            {
                if (snapshot.Last > 0m)
                {
                    history.Record(snapshot.Pair, new PriceObservation(cycle.Utc, snapshot.Last, snapshot.Bid, snapshot.Ask));
                }
            }

            if (cycle.Utc <= openedAt)
            {
                continue;
            }

            var decision = cycle.Decisions.FirstOrDefault(d => d.Pair == "OP/EUR");
            if (decision is null || !cycle.Snapshots.TryGetValue("OP/EUR", out var opSnapshot))
            {
                continue;
            }

            // Conservative PnL exactly as DryRunPortfolio values it: liquidation at
            // bid - slippage - fee against the fee-inclusive entry notional.
            var liquidation = quantity * opSnapshot.Bid * (1m - SlippageRate) * (1m - FeeRate);
            var pnlPercent = (liquidation - entryNotional) / entryNotional * 100m;

            lowScoreCycles = decision.Score <= exitOptions.ScoreDecayDefensiveScore ? lowScoreCycles + 1 : 0;
            var scoreDecay = new ScoreDecaySnapshot(
                EntryScore: 0.95m,
                CurrentScore: decision.Score,
                ConsecutiveLowScoreCycles: lowScoreCycles,
                ScoreConfirmsEntry: decision.Score >= strategy.MinimumLongScore);
            var assessment = history.Assess("OP/EUR", strategy.PriceActionLookbackSnapshots, strategy.PriceActionMinSnapshots);

            var evaluation = PositionExitPolicy.EvaluateHeldPosition(
                desiredLong: true,
                positionAgeSeconds: (cycle.Utc - openedAt).TotalSeconds,
                conservativeUnrealizedPnlPercent: pnlPercent,
                canValuePosition: true,
                killSwitchActive: false,
                executionPolicy,
                exitOptions,
                exitHysteresisEnabled: true,
                scoreDecay: scoreDecay,
                recentPriceActionNegative: assessment is { DataSufficient: true, TrendPercent: < 0m });

            if (evaluation.ShouldSell)
            {
                exitEvaluation = evaluation;
                exitUtc = cycle.Utc;
                exitPnlPercent = pnlPercent;
                break;
            }
        }

        Assert.NotNull(exitEvaluation);
        Assert.Equal(ExitReason.ScoreDecay, exitEvaluation!.ExitReason);

        // The recorded run held until the 08:07:48 stop-loss at about -2.6%. The
        // decay exit must fire on the second consecutive low-score cycle (06:35:50),
        // roughly 90 minutes earlier, with less than half the loss.
        Assert.Equal("06:35:50", exitUtc.ToString("HH:mm:ss"));
        Assert.True(exitPnlPercent > -1.5m, $"expected a shallow defensive exit, got {exitPnlPercent:0.##}%");
        Assert.True(exitPnlPercent < 0m, "fixture sanity: the position was under water at the defensive exit");
    }
}
