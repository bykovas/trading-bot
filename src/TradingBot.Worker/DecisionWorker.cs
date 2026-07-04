namespace TradingBot.Worker;

internal sealed class DecisionWorker(
    BotConfiguration config,
    IMarketDataSource marketDataSource,
    IWatchlistAdvisor watchlistAdvisor,
    IndicatorEngine indicatorEngine,
    TechnicalDecisionEngine decisionEngine,
    RiskManager riskManager,
    DryRunPortfolio dryRunPortfolio,
    KrakenBroker? broker,
    WorkerBuildInfo? buildInfo = null)
{
    private readonly WorkerBuildInfo _buildInfo = buildInfo ?? WorkerBuildInfo.FromEnvironment();

    // Rolling per-pair history of light ticker snapshots feeding the anti-lag
    // price-action guard. In-memory only: after a restart the guard abstains until
    // enough fresh snapshots accumulate.
    private readonly SnapshotPriceHistory _priceHistory = new();

    // Number of consecutive failed cycles that auto-trips the kill switch. A crash
    // loop with unmonitored stop-losses is far more dangerous than pausing, so after
    // this many back-to-back failures we halt new orders and let the operator look.
    private const int MaxConsecutiveCycleFailures = 5;
    private int _consecutiveCycleFailures;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        PrintStartup();
        HydratePriceHistory();
        await PrintBrokerStartupAsync(cancellationToken);

        do
        {
            // A single transient market-data / AI error must never kill the process:
            // with docker restart:unless-stopped that becomes a crash loop that stops
            // managing open positions. Log the error, skip the cycle, keep looping.
            try
            {
                await RunCycleAsync(cancellationToken);
                _consecutiveCycleFailures = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _consecutiveCycleFailures++;
                Console.WriteLine($"cycle-error: {ex.Message} (consecutive failures {_consecutiveCycleFailures}/{MaxConsecutiveCycleFailures})");
                if (_consecutiveCycleFailures >= MaxConsecutiveCycleFailures && !config.Risk.KillSwitch)
                {
                    config.Risk.KillSwitch = true;
                    Console.WriteLine($"!!! KILL SWITCH AUTO-TRIPPED after {MaxConsecutiveCycleFailures} consecutive failed cycles: new orders halted; open positions will be flattened as data allows !!!");
                }
            }

            if (config.Worker.RunOnce)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(config.Worker.LoopIntervalSeconds), cancellationToken);
        }
        while (!cancellationToken.IsCancellationRequested);
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        var utc = DateTimeOffset.UtcNow;
        var cycleId = utc.ToString("yyyyMMddHHmmss");
        Console.WriteLine();
        Console.WriteLine($"cycle={cycleId} utc={utc:O}");
        Console.WriteLine($"worker-version={_buildInfo.Version} commit={_buildInfo.Commit} strategy={_buildInfo.StrategyVersion} changeSet={_buildInfo.ChangeSet}");

        var lightCandidates = await marketDataSource.GetLightMarketStatesAsync(
            config.CandidateUniverse,
            cancellationToken);

        var loadedPortfolio = dryRunPortfolio.Load();
        PrintCandidates("candidate-universe light snapshot:", lightCandidates);

        // Persist the light snapshot of every universe pair right after it is fetched:
        // bid/ask/spread cannot be rebuilt from candles later. This must never block or
        // delay trading, so it is best-effort — a store failure is logged and ignored.
        PersistMarketSnapshots(cycleId, utc, lightCandidates);

        // Feed the rolling snapshot history used by the anti-lag price-action guard.
        _priceHistory.Record(utc, lightCandidates);

        var maxRecommendations = Math.Min(config.Trading.MaxActiveInstruments, config.Ai.MaxRecommendations);
        var advice = await watchlistAdvisor.SelectAsync(lightCandidates, maxRecommendations, cancellationToken);
        PrintWatchlistAdvice(advice);

        var activeInstruments = BuildActiveInstruments(advice, lightCandidates, loadedPortfolio);
        var selected = (await marketDataSource.GetFullMarketStatesAsync(
                activeInstruments,
                config.Trading.TimeframeMinutes,
                lightCandidates,
                cancellationToken))
            .ToList();

        PrintCandidates("active full-data set:", selected);

        var workingPortfolio = dryRunPortfolio.CloneAndMark(loadedPortfolio, selected);
        var portfolioBefore = workingPortfolio.Clone();
        PrintPortfolio("portfolio-before", portfolioBefore);

        var decisionRecords = new List<DryRunDecisionRecord>();
        var newPositionsThisCycle = 0;

        // PHASE 1 — held positions. Exit/hold logic always runs first and is never
        // delayed or displaced by new-entry ranking; a sell here also frees cash
        // that phase 2 sizing can legitimately use.
        var heldStates = new List<InstrumentMarketState>();
        var entryStates = new List<InstrumentMarketState>();
        foreach (var marketState in selected)
        {
            var hasPosition = workingPortfolio.Positions.Any(position =>
                position.Pair.Equals(marketState.Instrument.Pair, StringComparison.OrdinalIgnoreCase));
            (hasPosition ? heldStates : entryStates).Add(marketState);
        }

        foreach (var marketState in heldStates)
        {
            var prepared = PrepareDecision(marketState, workingPortfolio);
            if (prepared is null)
            {
                continue;
            }

            var record = await ExecuteDecisionAsync(prepared, workingPortfolio, newPositionsThisCycle, cancellationToken);
            decisionRecords.Add(record);
        }

        // PHASE 2 — new entries. Evaluate all no-position instruments WITHOUT
        // applying fills, rank the BUY candidates (score → EMA gap → RSI quality →
        // target notional → stable input order), then execute in ranked order so the
        // per-cycle and max-open limits go to the best candidates, not to whichever
        // pairs happen to come first in CandidateUniverse.
        var buyCandidates = new List<(PreparedDecision Prepared, EntryCandidate Rank)>();
        var entryEvaluations = new List<PreparedDecision>();
        var entryIndex = 0;
        foreach (var marketState in entryStates)
        {
            var prepared = PrepareDecision(marketState, workingPortfolio);
            if (prepared is null)
            {
                entryIndex++;
                continue;
            }

            entryEvaluations.Add(prepared);
            if (prepared.Proposal.DesiredPosition == "LONG_MICRO" && prepared.Risk.Approved)
            {
                buyCandidates.Add((prepared, new EntryCandidate(
                    prepared.Proposal.Pair,
                    prepared.Proposal.Score,
                    BullishEmaGapPercent(prepared.Indicators),
                    prepared.Indicators.Rsi,
                    prepared.Proposal.TargetNotionalEur,
                    entryIndex)));
            }
            else
            {
                // NO_ORDER / risk-rejected paths do not compete for entry slots;
                // record them immediately.
                var record = await ExecuteDecisionAsync(prepared, workingPortfolio, newPositionsThisCycle, cancellationToken);
                decisionRecords.Add(record);
            }

            entryIndex++;
        }

        var rankPosition = 0;
        foreach (var ranked in EntryRanking.Rank(buyCandidates.Select(candidate => candidate.Rank)))
        {
            rankPosition++;
            var prepared = buyCandidates.First(candidate => ReferenceEquals(candidate.Rank, ranked)).Prepared;

            // Exploratory candidates (admitted below the firm score threshold) only
            // trade from a top-N ranking slot; lower slots are recorded and skipped.
            if (prepared.Proposal.ExploratoryCandidate && rankPosition > config.Strategy.ExploratoryMaxRank)
            {
                decisionRecords.Add(BuildSkippedBuyRecord(
                    prepared,
                    workingPortfolio,
                    "EXPLORATORY_RANK",
                    $"exploratory candidate skipped: ranked #{rankPosition}, only top {config.Strategy.ExploratoryMaxRank} exploratory candidates may enter"));
                continue;
            }

            if (config.ExecutionPolicy.MaxNewPositionsPerCycle > 0
                && newPositionsThisCycle >= config.ExecutionPolicy.MaxNewPositionsPerCycle)
            {
                decisionRecords.Add(BuildSkippedBuyRecord(
                    prepared,
                    workingPortfolio,
                    "CYCLE_POSITION_LIMIT",
                    "buy candidate skipped because higher-ranked candidates consumed max new positions per cycle"));
                continue;
            }

            // Re-evaluate with the portfolio as it is NOW (after phase-1 exits and
            // higher-ranked buys) so position sizing / cash reserve / exposure see
            // reality — same semantics the old sequential flow had.
            var refreshed = PrepareDecision(prepared.MarketState, workingPortfolio);
            if (refreshed is null)
            {
                continue;
            }

            var record = await ExecuteDecisionAsync(refreshed, workingPortfolio, newPositionsThisCycle, cancellationToken);
            decisionRecords.Add(record);
            if (record.DryRunAction.Action == "WOULD_BUY")
            {
                newPositionsThisCycle++;
            }
        }

        if (selected.Count == 0)
        {
            Console.WriteLine("decision-cycle: skipped because active watchlist is empty");
        }

        var entryDiagnostics = BuildEntryDiagnostics(
            lightCandidates,
            selected,
            entryEvaluations,
            buyCandidates.Count,
            decisionRecords,
            advice);
        PrintEntryDiagnostics(entryDiagnostics);

        var portfolioAfter = workingPortfolio.Clone();
        PrintPortfolio("portfolio-after", portfolioAfter);

        if (config.DryRun.Enabled)
        {
            dryRunPortfolio.Save(portfolioAfter);
            dryRunPortfolio.AppendCycle(new DryRunCycleRecord
            {
                CycleId = cycleId,
                Utc = utc,
                MarketDataMode = config.Kraken.MarketDataMode,
                AiProvider = config.Ai.Provider,
                Worker = _buildInfo,
                ActivePairs = selected.Select(candidate => candidate.Instrument.Pair).ToList(),
                Decisions = decisionRecords,
                PortfolioBefore = portfolioBefore,
                PortfolioAfter = portfolioAfter,
                EntryDiagnostics = entryDiagnostics
            });
            Console.WriteLine($"dry-run-written state={dryRunPortfolio.GetStatePath()} events={dryRunPortfolio.GetEventsPath()}");
        }
    }

    // ---- Per-cycle entry funnel diagnostics ----
    // Explains every no-trade cycle: how many pairs were snapshotted vs evaluated,
    // where candidates fell out of the funnel (hard filters, quality filters,
    // ranking, portfolio gates), and the final chosen entry or explicit reason.
    private CycleEntryDiagnostics BuildEntryDiagnostics(
        IReadOnlyList<InstrumentMarketState> lightCandidates,
        IReadOnlyList<InstrumentMarketState> selected,
        IReadOnlyList<PreparedDecision> entryEvaluations,
        int eligibleEntryCandidates,
        IReadOnlyList<DryRunDecisionRecord> decisionRecords,
        WatchlistAdvice? advice = null)
    {
        var recordsByPair = decisionRecords
            .GroupBy(record => record.Pair, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        var candidates = new List<CandidateDiagnostic>();
        var rejectionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var prepared in entryEvaluations)
        {
            recordsByPair.TryGetValue(prepared.Proposal.Pair, out var record);
            var diagnostic = BuildCandidateDiagnostic(prepared, record);
            candidates.Add(diagnostic);
            if (diagnostic.RejectionReason is { } reason)
            {
                rejectionCounts[reason] = rejectionCounts.GetValueOrDefault(reason) + 1;
            }
        }

        var allScores = decisionRecords.Select(record => record.Score).ToList();
        var chosenPairs = decisionRecords
            .Where(record => record.DryRunAction.Action == "WOULD_BUY")
            .Select(record => record.Pair)
            .ToList();

        var excluded = BuildExcludedPairDiagnostics(lightCandidates, selected, advice);

        // Cycle-level guard readiness: how many snapshot pairs currently have enough
        // recent history for the anti-lag price-action guard to actually judge them.
        var priceActionReadyCount = lightCandidates.Count(candidate =>
            _priceHistory.Assess(
                candidate.Instrument.Pair,
                config.Strategy.PriceActionLookbackSnapshots,
                config.Strategy.PriceActionMinSnapshots,
                DateTimeOffset.UtcNow,
                config.Strategy.PriceActionMaxSampleAgeMinutes) is { DataSufficient: true });

        var chosenPair = chosenPairs.Count > 0 ? string.Join(", ", chosenPairs) : null;
        return new CycleEntryDiagnostics(
            SnapshotPairsAvailable: lightCandidates.Count,
            ActivePairsEvaluated: selected.Count,
            EntryPairsEvaluated: entryEvaluations.Count,
            PriceActionReadyCount: priceActionReadyCount,
            ScoreAtLeast075: allScores.Count(score => score >= 0.75m),
            ScoreAtLeast080: allScores.Count(score => score >= 0.80m),
            ScoreAtLeast085: allScores.Count(score => score >= 0.85m),
            ScoreAtLeast090: allScores.Count(score => score >= 0.90m),
            HardFilterPassCount: candidates.Count(candidate => candidate.HardFiltersPassed),
            EligibleEntryCandidates: eligibleEntryCandidates,
            ChosenPair: chosenPair,
            NoTradeReason: chosenPair is null
                ? BuildNoTradeReason(candidates, eligibleEntryCandidates, rejectionCounts, decisionRecords)
                : null,
            RejectionCounts: rejectionCounts,
            TopCandidates: candidates
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.SpreadPercent)
                .Take(5)
                .ToList(),
            ExcludedPairs: excluded);
    }

    // Excluded-pair diagnostics with a CONCRETE reason instead of a generic "not
    // selected" line: the pair's volume rank in the snapshot universe (what the
    // heuristic advisor sorts by), its estimated 24h EUR volume, its spread, and the
    // advisor's own rank/reason when the advisor did recommend it.
    private ExcludedPairDiagnostic BuildExcludedPairDiagnostic(
        InstrumentMarketState candidate,
        int? volumeRank,
        int totalPairs,
        int advisorPickCount,
        WatchlistRecommendation? recommendation)
    {
        var pair = candidate.Instrument.Pair;
        var spreadPercent = decimal.Round(EntryGate.SpreadPercentOf(candidate), 3);
        var volumeEur = decimal.Round(candidate.LastVolume * candidate.LastPrice, 0);

        string reason;
        if (!string.IsNullOrWhiteSpace(candidate.DataWarning))
        {
            reason = $"unusable data: {candidate.DataWarning}";
        }
        else if (recommendation is not null)
        {
            reason = $"recommended by advisor (rank #{recommendation.Priority}) but did not enter the active set ({EntryRejection.ActivePairFilter})";
        }
        else
        {
            var rankText = volumeRank is { } rank
                ? $"24h EUR volume rank #{rank} of {totalPairs}, advisor takes top {advisorPickCount}"
                : "no usable volume ranking";
            var spreadText = config.Strategy.MaxEntrySpreadPercent > 0m && spreadPercent > config.Strategy.MaxEntrySpreadPercent
                ? $"; spread {spreadPercent:0.###}% also exceeds entry max {config.Strategy.MaxEntrySpreadPercent:0.###}%"
                : string.Empty;
            reason = $"not selected by watchlist advisor ({EntryRejection.ActivePairFilter}): {rankText} (est. 24h volume EUR {volumeEur:0}){spreadText}";
        }

        return new ExcludedPairDiagnostic(
            pair,
            reason,
            candidate.LastPrice,
            candidate.ChangePercent,
            VolumeRank: volumeRank,
            Est24hVolumeEur: volumeEur,
            SpreadPercent: spreadPercent,
            AdvisorRank: recommendation?.Priority);
    }

    private List<ExcludedPairDiagnostic> BuildExcludedPairDiagnostics(
        IReadOnlyList<InstrumentMarketState> lightCandidates,
        IReadOnlyList<InstrumentMarketState> selected,
        WatchlistAdvice? advice)
    {
        // Rank every usable snapshot pair by estimated 24h EUR volume — the metric
        // the heuristic watchlist advisor sorts by — so an excluded pair's reason
        // can say WHERE it ranked instead of just "not selected".
        var volumeRanks = lightCandidates
            .Where(candidate => candidate.LastPrice > 0m && string.IsNullOrWhiteSpace(candidate.DataWarning))
            .OrderByDescending(candidate => candidate.LastVolume * candidate.LastPrice)
            .Select((candidate, index) => (candidate.Instrument.Pair, Rank: index + 1))
            .ToDictionary(item => item.Pair, item => item.Rank, StringComparer.OrdinalIgnoreCase);

        var recommendationsByPair = (advice?.Recommendations ?? [])
            .GroupBy(recommendation => recommendation.Pair, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var advisorPickCount = Math.Min(config.Trading.MaxActiveInstruments, config.Ai.MaxRecommendations);

        return lightCandidates
            .Where(candidate => !selected.Any(state => state.Instrument.Pair.Equals(candidate.Instrument.Pair, StringComparison.OrdinalIgnoreCase)))
            .Select(candidate => BuildExcludedPairDiagnostic(
                candidate,
                volumeRanks.TryGetValue(candidate.Instrument.Pair, out var rank) ? rank : null,
                volumeRanks.Count,
                advisorPickCount,
                recommendationsByPair.GetValueOrDefault(candidate.Instrument.Pair)))
            .ToList();
    }

    private CandidateDiagnostic BuildCandidateDiagnostic(PreparedDecision prepared, DryRunDecisionRecord? record)
    {
        var proposal = prepared.Proposal;
        var marketState = prepared.MarketState;

        // The gate-level rejection wins; if the gate passed but the portfolio layer
        // blocked the buy, map its hold-reason code onto the same vocabulary.
        var rejection = proposal.EntryRejectionReason;
        if (rejection is null && record is { DryRunAction.Action: not "WOULD_BUY" })
        {
            rejection = EntryRejection.FromHoldReasonCode(record.DryRunAction.HoldReasonCode)
                ?? (record.DryRunAction.Action == "WOULD_BUY_BLOCKED" ? EntryRejection.RiskLimits : null);
        }

        var missing = new List<string>();
        foreach (var contribution in proposal.Contributions)
        {
            switch (contribution.Name)
            {
                case "Momentum" when contribution.Value <= 0m:
                    missing.Add("MOMENTUM");
                    break;
                case "Volume" when contribution.Value <= 0m:
                    missing.Add("VOLUME");
                    break;
                case "Trend" when contribution.Value <= 0m:
                    missing.Add("TREND");
                    break;
                case "RSI" when contribution.Value <= 0m:
                    missing.Add("RSI");
                    break;
                case "PriceAction" when contribution.Value < 0m:
                    missing.Add("PRICE_ACTION");
                    break;
            }
        }

        if (rejection == EntryRejection.SpreadTooWide)
        {
            missing.Add("SPREAD");
        }

        // A guard that cannot judge the pair is itself a missing confirmation: an
        // UNKNOWN price action must show up explicitly, not read as "nothing wrong".
        var priceAction = prepared.PriceAction;
        if (priceAction is not { DataSufficient: true })
        {
            missing.Add("PRICE_ACTION_UNKNOWN");
        }

        var hardFiltersPassed = rejection is not (EntryRejection.SpreadTooWide or EntryRejection.PairUnavailable or EntryRejection.LowLiquidity);
        return new CandidateDiagnostic(
            proposal.Pair,
            proposal.Score,
            proposal.DesiredPosition,
            decimal.Round(proposal.SpreadPercent, 3),
            marketState.LastPrice,
            marketState.BestBid,
            marketState.BestAsk,
            priceAction?.Direction ?? "UNKNOWN",
            priceAction?.TrendPercent,
            PriceActionAssessment.WarmupStateOf(priceAction),
            priceAction?.SnapshotCount ?? 0,
            priceAction is { SamplesRequired: > 0 } ? priceAction.SamplesRequired : Math.Max(2, config.Strategy.PriceActionMinSnapshots),
            priceAction?.OldestSampleUtc,
            priceAction?.NewestSampleUtc,
            hardFiltersPassed,
            QualityFiltersPassed: proposal.DesiredPosition == "LONG_MICRO",
            missing,
            rejection,
            proposal.ExploratoryCandidate);
    }

    private static string BuildNoTradeReason(
        IReadOnlyList<CandidateDiagnostic> candidates,
        int eligibleEntryCandidates,
        IReadOnlyDictionary<string, int> rejectionCounts,
        IReadOnlyList<DryRunDecisionRecord> decisionRecords)
    {
        if (candidates.Count == 0)
        {
            return "no evaluable entry pairs this cycle (active watchlist empty, all pairs held, or data unusable)";
        }

        if (eligibleEntryCandidates > 0)
        {
            var blocked = decisionRecords
                .Where(record => record.DryRunAction.Action == "WOULD_BUY_BLOCKED")
                .Select(record => $"{record.Pair}:{record.DryRunAction.HoldReasonCode ?? "RISK_LIMITS"}")
                .ToList();
            return blocked.Count > 0
                ? $"eligible candidates were blocked by portfolio gates: {string.Join(", ", blocked)}"
                : "eligible candidates existed but none produced a buy (see decision records)";
        }

        var best = candidates.OrderByDescending(candidate => candidate.Score).First();
        var topRejections = string.Join(
            ", ",
            rejectionCounts.OrderByDescending(item => item.Value).Take(3).Select(item => $"{item.Key} x{item.Value}"));
        return $"no eligible entry candidates: best score {best.Score:0.##} on {best.Pair} ({best.RejectionReason ?? "no rejection recorded"}); rejections: {topRejections}";
    }

    private static void PrintEntryDiagnostics(CycleEntryDiagnostics diagnostics)
    {
        Console.WriteLine("cycle-entry-diagnostics:");
        Console.WriteLine(
            $"  snapshots={diagnostics.SnapshotPairsAvailable} evaluated={diagnostics.ActivePairsEvaluated} entryPairs={diagnostics.EntryPairsEvaluated} " +
            $"priceActionReady={diagnostics.PriceActionReadyCount}/{diagnostics.SnapshotPairsAvailable} " +
            $"score>=0.75:{diagnostics.ScoreAtLeast075} >=0.80:{diagnostics.ScoreAtLeast080} >=0.85:{diagnostics.ScoreAtLeast085} >=0.90:{diagnostics.ScoreAtLeast090} " +
            $"hardFilterPass={diagnostics.HardFilterPassCount} eligible={diagnostics.EligibleEntryCandidates}");
        foreach (var candidate in diagnostics.TopCandidates)
        {
            Console.WriteLine(
                $"  top {candidate.Pair}: score={candidate.Score:0.##} desired={candidate.DesiredPosition} spread={candidate.SpreadPercent:0.###}% " +
                $"price={candidate.Price:0.######} bid={candidate.Bid:0.######} ask={candidate.Ask:0.######} " +
                $"priceAction={candidate.PriceActionDirection}({FormatTrend(candidate.PriceActionTrendPercent)}) " +
                $"paState={candidate.PriceActionState}({candidate.PriceActionSamplesAvailable}/{candidate.PriceActionSamplesRequired}) " +
                $"hard={(candidate.HardFiltersPassed ? "pass" : "FAIL")} quality={(candidate.QualityFiltersPassed ? "pass" : "FAIL")} " +
                $"missing=[{string.Join(",", candidate.MissingConfirmations)}] " +
                $"reject={candidate.RejectionReason ?? "-"}{(candidate.Exploratory ? " (exploratory)" : string.Empty)}");
        }

        if (diagnostics.ExcludedPairs.Count > 0)
        {
            Console.WriteLine($"  excluded pairs ({diagnostics.ExcludedPairs.Count} of {diagnostics.SnapshotPairsAvailable} snapshot pairs):");
            foreach (var excluded in diagnostics.ExcludedPairs)
            {
                Console.WriteLine($"    {excluded.Pair}: {excluded.Reason} (last={excluded.Last:0.######} change={excluded.ChangePercent:+0.##;-0.##;0}%)");
            }
        }

        Console.WriteLine(diagnostics.ChosenPair is { } chosen
            ? $"  chosen: {chosen}"
            : $"  no-trade: {diagnostics.NoTradeReason}");
    }

    private static string FormatTrend(decimal? trendPercent) =>
        trendPercent is { } trend ? $"{trend:+0.###;-0.###;0}%" : "n/a";

    // Best-effort per-cycle persistence of the light market snapshot. Failures here
    // must NEVER block or delay trading, so everything is wrapped and only logged.
    private void PersistMarketSnapshots(string cycleId, DateTimeOffset utc, IReadOnlyList<InstrumentMarketState> lightStates)
    {
        try
        {
            var snapshots = lightStates
                .Select(state => new MarketSnapshotRecord(
                    cycleId,
                    utc,
                    state.Instrument.Pair,
                    state.BestBid,
                    state.BestAsk,
                    state.LastPrice,
                    state.LastVolume,
                    state.ChangePercent))
                .ToList();

            dryRunPortfolio.AppendMarketSnapshots(snapshots);
            Console.WriteLine($"market-snapshots: persisted {snapshots.Count} rows for cycle {cycleId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"market-snapshots: FAILED to persist for cycle {cycleId} ({ex.Message}); continuing cycle");
        }
    }

    // Best-effort warm-up hydration: reload the recent persisted market snapshots so
    // the anti-lag price-action guard is READY on the very first cycle after a
    // restart instead of being blind for PriceActionMinSnapshots cycles. Only rows
    // from the configured recency window are loaded, so a long downtime gap results
    // in a normal warm-up rather than a stitched-together fake trend. A store
    // failure only skips hydration; it must never prevent the worker from starting.
    internal void HydratePriceHistory()
    {
        if (config.Strategy.PriceActionHydrationMinutes <= 0)
        {
            return;
        }

        try
        {
            var since = DateTimeOffset.UtcNow.AddMinutes(-config.Strategy.PriceActionHydrationMinutes);
            var snapshots = dryRunPortfolio.LoadRecentMarketSnapshots(since);
            var loaded = _priceHistory.Hydrate(snapshots);
            Console.WriteLine(loaded > 0
                ? $"price-action-hydration: loaded {loaded} persisted snapshots from the last {config.Strategy.PriceActionHydrationMinutes} minutes ({snapshots.Select(snapshot => snapshot.Pair).Distinct(StringComparer.OrdinalIgnoreCase).Count()} pairs)"
                : $"price-action-hydration: no persisted snapshots in the last {config.Strategy.PriceActionHydrationMinutes} minutes; guard warms up normally");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"price-action-hydration: FAILED ({ex.Message}); guard warms up normally");
        }
    }

    private void PrintStartup()
    {
        Console.WriteLine("Blynai Capital worker");
        Console.WriteLine($"marketDataMode={config.Kraken.MarketDataMode}");
        Console.WriteLine($"aiProvider={config.Ai.Provider}");
        Console.WriteLine($"liveTradingEnabled={config.Trading.LiveTradingEnabled}");
        Console.WriteLine($"timeframe={config.Trading.TimeframeMinutes}m maxActive={config.Trading.MaxActiveInstruments}");
        Console.WriteLine("aiInTradeDecision=false");
        Console.WriteLine($"dryRunEnabled={config.DryRun.Enabled} applyVirtualFills={config.DryRun.ApplyVirtualFills}");
        Console.WriteLine($"dryRunState={dryRunPortfolio.GetStatePath()}");
        Console.WriteLine($"dryRunEvents={dryRunPortfolio.GetEventsPath()}");
        PrintCorrelationDrift();
    }

    // Config-drift guard: any universe pair missing from every correlation group is
    // treated as a high-beta singleton by the risk layer. Warn once at startup so a
    // silently-ungrouped pair can never slip past the correlation caps unnoticed.
    private void PrintCorrelationDrift()
    {
        if (config.CorrelationRisk.Groups.Count == 0)
        {
            return;
        }

        var ungrouped = CorrelationRiskResolver.UngroupedPairs(
            config.CorrelationRisk,
            config.CandidateUniverse.Select(instrument => instrument.Pair));
        if (ungrouped.Count > 0)
        {
            Console.WriteLine($"warning: {ungrouped.Count} universe pair(s) are not in any correlation group and are treated as high-beta singletons: {string.Join(", ", ungrouped)}");
        }
    }

    private bool LiveOrdersActive => config.Trading.LiveTradingEnabled && !config.Risk.KillSwitch;

    private async Task PrintBrokerStartupAsync(CancellationToken cancellationToken)
    {
        if (broker is null)
        {
            Console.WriteLine("broker=disabled (no Kraken API keys or market data mode != kraken; virtual dry-run only)");
            return;
        }

        Console.WriteLine($"broker=kraken-private mode={(LiveOrdersActive ? "LIVE (validate=false, REAL ORDERS)" : "validate-only (validate=true, no execution)")}");
        if (LiveOrdersActive)
        {
            Console.WriteLine("!!! LIVE TRADING ENABLED: approved decisions will place REAL market orders on Kraken with real money !!!");
        }

        try
        {
            var balances = await broker.GetBalanceAsync(cancellationToken);
            var eur = balances.TryGetValue("ZEUR", out var zeur)
                ? zeur
                : balances.TryGetValue("EUR", out var eurBalance) ? eurBalance : 0m;
            Console.WriteLine($"broker-balance: EUR {eur:0.####} (auth OK, {balances.Count} assets)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"broker-balance: FAILED to fetch ({ex.Message}) — check API key/secret/permissions");
        }
    }

    private static void PrintCandidates(string heading, IReadOnlyList<InstrumentMarketState> candidates)
    {
        Console.WriteLine(heading);
        foreach (var candidate in candidates)
        {
            var warning = string.IsNullOrWhiteSpace(candidate.DataWarning) ? "ok" : candidate.DataWarning;
            Console.WriteLine(
                $"  {candidate.Instrument.Pair} price={candidate.LastPrice:0.####} bid={candidate.BestBid:0.####} ask={candidate.BestAsk:0.####} change={candidate.ChangePercent:0.##}% vol={candidate.VolatilityPercent:0.##}% status={candidate.PairRules?.Status ?? "unknown"} data={warning}");
        }
    }

    private static void PrintWatchlistAdvice(WatchlistAdvice advice)
    {
        Console.WriteLine($"watchlist-advisor provider={advice.Provider}:");
        foreach (var warning in advice.Warnings)
        {
            Console.WriteLine($"  warning: {warning}");
        }

        foreach (var recommendation in advice.Recommendations)
        {
            Console.WriteLine($"  #{recommendation.Priority} {recommendation.Pair}: {recommendation.Reason}");
        }
    }

    private static IReadOnlyList<InstrumentOptions> BuildActiveInstruments(
        WatchlistAdvice advice,
        IReadOnlyList<InstrumentMarketState> candidates,
        PortfolioState portfolio)
    {
        var selected = advice.Recommendations
            .Select(recommendation => candidates.FirstOrDefault(candidate => candidate.Instrument.Pair.Equals(recommendation.Pair, StringComparison.OrdinalIgnoreCase)))
            .Where(candidate => candidate is not null)
            .Cast<InstrumentMarketState>()
            .Select(candidate => candidate.Instrument)
            .ToList();

        foreach (var position in portfolio.Positions)
        {
            if (selected.Any(instrument => instrument.Pair.Equals(position.Pair, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var candidate = candidates.FirstOrDefault(item => item.Instrument.Pair.Equals(position.Pair, StringComparison.OrdinalIgnoreCase));
            if (candidate is not null)
            {
                selected.Add(candidate.Instrument);
                Console.WriteLine($"watchlist-forced {position.Pair}: open position must be evaluated even if advisor did not select it");
            }
            else
            {
                Console.WriteLine($"warning: open position {position.Pair} is not present in CandidateUniverse; cannot evaluate exit");
            }
        }

        return selected;
    }

    // Immutable snapshot of a decision BEFORE it is applied to the portfolio. Lets
    // phase 2 evaluate and rank all entry candidates without mutating state.
    private sealed record PreparedDecision(
        InstrumentMarketState MarketState,
        IndicatorSnapshot Indicators,
        DecisionProposal Proposal,
        RiskEvaluation Risk,
        PriceActionAssessment? PriceAction);

    private PreparedDecision? PrepareDecision(InstrumentMarketState marketState, PortfolioState portfolio)
    {
        if (!marketState.IsUsable)
        {
            Console.WriteLine($"decision {marketState.Instrument.Pair}: skipped unusable data");
            return null;
        }

        var indicators = indicatorEngine.Calculate(marketState.Candles, config.Strategy);
        var currentExposureEur = portfolio.Positions.Sum(position => position.EntryNotionalEur);
        var hasOpenPosition = portfolio.Positions.Any(position =>
            position.Pair.Equals(marketState.Instrument.Pair, StringComparison.OrdinalIgnoreCase));
        var priceAction = _priceHistory.Assess(
            marketState.Instrument.Pair,
            config.Strategy.PriceActionLookbackSnapshots,
            config.Strategy.PriceActionMinSnapshots,
            DateTimeOffset.UtcNow,
            config.Strategy.PriceActionMaxSampleAgeMinutes);
        var proposal = decisionEngine.Decide(marketState, indicators, config.Trading, config.Strategy, config.PositionSizing, config.Risk, portfolio.CashEur, currentExposureEur, hasOpenPosition, priceAction);
        var risk = riskManager.Evaluate(proposal, config.Risk, hasOpenPosition);
        return new PreparedDecision(marketState, indicators, proposal, risk, priceAction);
    }

    private static decimal BullishEmaGapPercent(IndicatorSnapshot indicators)
    {
        if (indicators.FastEma is not { } fast || indicators.SlowEma is not { } slow || slow == 0m || fast <= slow)
        {
            return 0m;
        }

        return (fast - slow) / slow * 100m;
    }

    // Record for a ranked BUY candidate that lost the per-cycle entry race (cycle
    // position limit or exploratory rank cut). No portfolio mutation, no broker call.
    private DryRunDecisionRecord BuildSkippedBuyRecord(
        PreparedDecision prepared,
        PortfolioState portfolio,
        string holdReasonCode,
        string reason)
    {
        Console.WriteLine($"decision {prepared.Proposal.Pair}:");
        Console.WriteLine($"  desired={prepared.Proposal.DesiredPosition} score={prepared.Proposal.Score:0.##} targetEur={prepared.Proposal.TargetNotionalEur:0.##}");
        Console.WriteLine("  execution=WOULD_BUY_BLOCKED");
        Console.WriteLine($"  execution-hold-reason-code: {holdReasonCode}");
        Console.WriteLine($"  execution-reason: {reason}");

        return new DryRunDecisionRecord
        {
            Pair = prepared.Proposal.Pair,
            Price = prepared.MarketState.LastPrice,
            FastEma = prepared.Indicators.FastEma,
            SlowEma = prepared.Indicators.SlowEma,
            Rsi = prepared.Indicators.Rsi,
            DesiredPosition = prepared.Proposal.DesiredPosition,
            Score = prepared.Proposal.Score,
            RiskApproved = prepared.Risk.Approved,
            RiskReasons = prepared.Risk.Reasons,
            Contributions = prepared.Proposal.Contributions,
            DryRunAction = new DryRunAction
            {
                Pair = prepared.Proposal.Pair,
                Action = "WOULD_BUY_BLOCKED",
                Reason = reason,
                HoldReasonCode = holdReasonCode,
                DesiredPosition = prepared.Proposal.DesiredPosition,
                TargetNotionalEur = prepared.Proposal.TargetNotionalEur,
                CashBeforeEur = portfolio.CashEur,
                CashAfterEur = portfolio.CashEur,
                PortfolioValueBeforeEur = portfolio.TotalValueEur,
                PortfolioValueAfterEur = portfolio.TotalValueEur
            },
            Broker = null,
            EntryRejectionReason = holdReasonCode == "EXPLORATORY_RANK" ? EntryRejection.ExploratoryRank : EntryRejection.CyclePositionLimit,
            SpreadPercent = decimal.Round(prepared.Proposal.SpreadPercent, 3),
            PriceActionDirection = prepared.PriceAction?.Direction,
            PriceActionTrendPercent = prepared.PriceAction?.TrendPercent,
            Exploratory = prepared.Proposal.ExploratoryCandidate
        };
    }

    // A live order the exchange did NOT execute (error or pre-flight skip). The
    // virtual portfolio stays untouched, so state and exchange remain consistent:
    // a failed BUY leaves no phantom position, a failed SELL keeps the position
    // (the asset is in fact still held) and the exit retries next cycle.
    private static DryRunAction BuildLiveOrderNotExecutedAction(
        DryRunAction previewAction,
        PortfolioState portfolio,
        string brokerVerdict)
    {
        Console.WriteLine($"  !!! live order NOT executed, virtual portfolio left unchanged: {brokerVerdict}");
        return new DryRunAction
        {
            Pair = previewAction.Pair,
            Action = "LIVE_ORDER_FAILED",
            Reason = $"intended {previewAction.Action} was not executed by the exchange: {brokerVerdict}",
            HoldReasonCode = "LIVE_ORDER_FAILED",
            ExitReasonCode = null,
            DesiredPosition = previewAction.DesiredPosition,
            TargetNotionalEur = previewAction.TargetNotionalEur,
            CashBeforeEur = portfolio.CashEur,
            CashAfterEur = portfolio.CashEur,
            PortfolioValueBeforeEur = portfolio.TotalValueEur,
            PortfolioValueAfterEur = portfolio.TotalValueEur,
            CorrelationGroup = previewAction.CorrelationGroup,
            CorrelationGroupOpenPositions = previewAction.CorrelationGroupOpenPositions,
            CorrelationGroupExposureEur = previewAction.CorrelationGroupExposureEur
        };
    }

    private async Task<DryRunDecisionRecord> ExecuteDecisionAsync(
        PreparedDecision prepared,
        PortfolioState portfolio,
        int newPositionsThisCycle,
        CancellationToken cancellationToken)
    {
        var marketState = prepared.MarketState;
        var indicators = prepared.Indicators;
        var proposal = prepared.Proposal;
        var risk = prepared.Risk;
        var currentPositionBeforeAction = portfolio.Positions.FirstOrDefault(position => position.Pair.Equals(proposal.Pair, StringComparison.OrdinalIgnoreCase))?.Clone();

        // Execution ordering depends on the mode:
        //   dry-run / validate-only: virtual fill first, then an informational
        //     validate-only broker call (its outcome never changes the portfolio).
        //   LIVE: the intended action is computed on a CLONE (no mutation), the real
        //     order goes to the broker FIRST, and the virtual portfolio is committed
        //     only after the exchange accepted the order. A failed/skipped live order
        //     therefore never creates phantom positions or phantom exits.
        DryRunAction dryRunAction;
        string? brokerVerdict;
        if (LiveOrdersActive)
        {
            var previewAction = dryRunPortfolio.Apply(portfolio.Clone(), marketState, proposal, risk, config.Risk, newPositionsThisCycle, prepared.PriceAction);
            if (previewAction.Action is "WOULD_BUY" or "WOULD_SELL")
            {
                brokerVerdict = await RunBrokerAsync(marketState, previewAction, cancellationToken);
                if (brokerVerdict is null || brokerVerdict.StartsWith("LIVE_SUBMITTED", StringComparison.Ordinal))
                {
                    // Exchange accepted (or no broker is wired at all): commit the same
                    // deterministic action to the real portfolio state.
                    dryRunAction = dryRunPortfolio.Apply(portfolio, marketState, proposal, risk, config.Risk, newPositionsThisCycle, prepared.PriceAction);
                }
                else
                {
                    dryRunAction = BuildLiveOrderNotExecutedAction(previewAction, portfolio, brokerVerdict);
                }
            }
            else
            {
                // No order intended: applying to the real state is pure bookkeeping
                // (mark-to-market, hold counters); nothing was sent to the exchange.
                dryRunAction = dryRunPortfolio.Apply(portfolio, marketState, proposal, risk, config.Risk, newPositionsThisCycle, prepared.PriceAction);
                brokerVerdict = null;
            }
        }
        else
        {
            dryRunAction = dryRunPortfolio.Apply(portfolio, marketState, proposal, risk, config.Risk, newPositionsThisCycle, prepared.PriceAction);
            brokerVerdict = await RunBrokerAsync(marketState, dryRunAction, cancellationToken);
        }

        Console.WriteLine($"decision {proposal.Pair}:");
        Console.WriteLine($"  price={marketState.LastPrice:0.####} ema{config.Strategy.FastEmaPeriod}={Format(indicators.FastEma)} ema{config.Strategy.SlowEmaPeriod}={Format(indicators.SlowEma)} rsi{config.Strategy.RsiPeriod}={Format(indicators.Rsi)}");
        Console.WriteLine($"  position={FormatPosition(currentPositionBeforeAction)}");
        Console.WriteLine($"  desired={proposal.DesiredPosition} score={proposal.Score:0.##} targetEur={proposal.TargetNotionalEur:0.##}");
        foreach (var contribution in proposal.Contributions)
        {
            Console.WriteLine($"  signal {contribution.Name}: {contribution.Value:+0.##;-0.##;0} {contribution.Reason}");
        }

        Console.WriteLine($"  risk={(risk.Approved ? "APPROVED" : "REJECTED")}");
        foreach (var reason in risk.Reasons)
        {
            Console.WriteLine($"  risk-reason: {reason}");
        }

        Console.WriteLine($"  execution={dryRunAction.Action}");
        if (!string.IsNullOrEmpty(dryRunAction.HoldReasonCode))
        {
            Console.WriteLine($"  execution-hold-reason-code: {dryRunAction.HoldReasonCode}");
        }
        if (!string.IsNullOrEmpty(dryRunAction.ExitReasonCode))
        {
            Console.WriteLine($"  execution-exit-reason-code: {dryRunAction.ExitReasonCode}");
        }
        Console.WriteLine($"  execution-reason: {dryRunAction.Reason}");
        if (!string.IsNullOrEmpty(dryRunAction.CorrelationGroup))
        {
            Console.WriteLine($"  correlation-group={dryRunAction.CorrelationGroup} open={dryRunAction.CorrelationGroupOpenPositions} exposure={dryRunAction.CorrelationGroupExposureEur:0.##} EUR");
        }
        if (!string.IsNullOrEmpty(dryRunAction.CorrelationRejectedReason))
        {
            Console.WriteLine($"  correlation-rejected: {dryRunAction.CorrelationRejectedReason}");
        }
        if (dryRunAction.FillPrice > 0m || dryRunAction.FeeEur > 0m)
        {
            Console.WriteLine($"  fill-price={dryRunAction.FillPrice:0.####} fee={dryRunAction.FeeEur:0.####} gross={dryRunAction.GrossNotionalEur:0.####} net={dryRunAction.NetNotionalEur:0.####}");
        }
        Console.WriteLine($"  portfolio-cash: {dryRunAction.CashBeforeEur:0.##} -> {dryRunAction.CashAfterEur:0.##} EUR");
        Console.WriteLine($"  portfolio-value: {dryRunAction.PortfolioValueBeforeEur:0.##} -> {dryRunAction.PortfolioValueAfterEur:0.##} EUR");

        if (brokerVerdict is not null)
        {
            Console.WriteLine($"  broker={brokerVerdict}");
        }

        return new DryRunDecisionRecord
        {
            Pair = proposal.Pair,
            Price = marketState.LastPrice,
            FastEma = indicators.FastEma,
            SlowEma = indicators.SlowEma,
            Rsi = indicators.Rsi,
            DesiredPosition = proposal.DesiredPosition,
            Score = proposal.Score,
            RiskApproved = risk.Approved,
            RiskReasons = risk.Reasons,
            Contributions = proposal.Contributions,
            DryRunAction = dryRunAction,
            Broker = brokerVerdict,
            EntryRejectionReason = proposal.EntryRejectionReason
                ?? (dryRunAction.Action == "WOULD_BUY_BLOCKED"
                    ? EntryRejection.FromHoldReasonCode(dryRunAction.HoldReasonCode) ?? EntryRejection.RiskLimits
                    : null),
            SpreadPercent = decimal.Round(proposal.SpreadPercent, 3),
            PriceActionDirection = prepared.PriceAction?.Direction,
            PriceActionTrendPercent = prepared.PriceAction?.TrendPercent,
            Exploratory = proposal.ExploratoryCandidate
        };
    }

    // Sends the order to Kraken for the two actionable outcomes only. The validate
    // flag is derived from the live gate: validate=true (exchange checks the order
    // without executing) unless live trading is explicitly enabled and the kill
    // switch is off, in which case validate=false places a real market order.
    private async Task<string?> RunBrokerAsync(
        InstrumentMarketState marketState,
        DryRunAction action,
        CancellationToken cancellationToken)
    {
        if (broker is null)
        {
            return null;
        }

        if (action.Action != "WOULD_BUY" && action.Action != "WOULD_SELL")
        {
            return null;
        }

        var lotDecimals = marketState.PairRules?.LotDecimals ?? 8;
        var orderMin = marketState.PairRules?.OrderMinimum ?? 0m;
        var volume = TruncateTo(action.Quantity, lotDecimals);

        if (volume <= 0m)
        {
            return "SKIPPED: computed volume is zero";
        }

        if (orderMin > 0m && volume < orderMin)
        {
            return $"SKIPPED: volume {volume} below pair ordermin {orderMin}";
        }

        var side = action.Action == "WOULD_BUY" ? "buy" : "sell";
        var validate = !LiveOrdersActive;

        // Belt-and-suspenders: never let a live BUY exceed the hard per-order cap,
        // even though the risk gate already approved it upstream.
        if (!validate && side == "buy" && action.TargetNotionalEur > config.Risk.MaxOrderEur)
        {
            return $"SKIPPED: live buy notional {action.TargetNotionalEur:0.##} exceeds MaxOrderEur {config.Risk.MaxOrderEur:0.##}";
        }

        var result = await broker.AddOrderAsync(marketState.Instrument.KrakenPair, side, volume, validate, cancellationToken);

        if (!result.Success)
        {
            return validate ? $"VALIDATE_REJECTED: {result.Error}" : $"LIVE_ERROR: {result.Error}";
        }

        if (validate)
        {
            var descr = string.IsNullOrWhiteSpace(result.Description) ? string.Empty : $" descr=\"{result.Description}\"";
            return $"VALIDATED_OK side={side} vol={volume}{descr}";
        }

        var txids = result.TxIds.Count > 0 ? string.Join(",", result.TxIds) : "(none)";
        return $"LIVE_SUBMITTED side={side} vol={volume} txid={txids}";
    }

    private static decimal TruncateTo(decimal value, int decimals)
    {
        if (decimals < 0)
        {
            decimals = 0;
        }

        var factor = 1m;
        for (var i = 0; i < decimals; i++)
        {
            factor *= 10m;
        }

        return Math.Truncate(value * factor) / factor;
    }

    private static void PrintPortfolio(string label, PortfolioState portfolio)
    {
        Console.WriteLine($"{label}: cash={portfolio.CashEur:0.##} positionsValue={portfolio.PositionsValueEur:0.##} total={portfolio.TotalValueEur:0.##}");
        if (portfolio.Positions.Count == 0)
        {
            Console.WriteLine("  positions: none");
            return;
        }

        foreach (var position in portfolio.Positions)
        {
            Console.WriteLine(
                $"  {position.Pair} {position.Side} qty={position.Quantity:0.##########} entry={position.EntryPrice:0.####} last={position.LastPrice:0.####} value={position.MarketValueEur:0.##} pnl={position.UnrealizedPnlEur:+0.##;-0.##;0} EUR ({position.UnrealizedPnlPercent:+0.##;-0.##;0}%)");
        }
    }

    private static string Format(decimal? value) => value is null ? "n/a" : value.Value.ToString("0.####");

    private static string FormatPosition(PortfolioPosition? position)
    {
        if (position is null)
        {
            return "NONE";
        }

        return $"{position.Side} qty={position.Quantity:0.##########} entry={position.EntryPrice:0.####} value={position.MarketValueEur:0.##} pnl={position.UnrealizedPnlPercent:+0.##;-0.##;0}%";
    }
}
