namespace TradingBot.SpotWorker;

internal sealed class DecisionWorker(
    BotConfiguration config,
    IMarketDataSource marketDataSource,
    IWatchlistAdvisor watchlistAdvisor,
    IndicatorEngine indicatorEngine,
    TechnicalDecisionEngine decisionEngine,
    RiskManager riskManager,
    DryRunPortfolio dryRunPortfolio,
    ISpotBroker? broker,
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
        var cycleId = $"{config.BotInstance.Id}-{utc:yyyyMMddHHmmss}";
        Console.WriteLine();
        Console.WriteLine($"cycle={cycleId} utc={utc:O}");
        Console.WriteLine($"botInstance={config.BotInstance.Id} name={config.BotInstance.Name}");
        Console.WriteLine($"worker-version={_buildInfo.Version} commit={_buildInfo.Commit} strategy={_buildInfo.StrategyVersion} changeSet={_buildInfo.ChangeSet}");

        var lightCandidates = await marketDataSource.GetLightMarketStatesAsync(
            config.CandidateUniverse,
            cancellationToken);

        var loadedPortfolio = dryRunPortfolio.Load();

        if (LiveOrdersActive && broker is not null
            && string.Equals(config.Kraken.MarketDataMode, "kraken", StringComparison.OrdinalIgnoreCase))
        {
            await ReconcileWithKrakenAsync(loadedPortfolio, cancellationToken);
        }

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

        var regime = config.CandidateUniverse.Any(instrument =>
                instrument.Pair.Equals("XBT/EUR", StringComparison.OrdinalIgnoreCase)
                || instrument.Pair.Equals("BTC/EUR", StringComparison.OrdinalIgnoreCase))
            ? EvaluateBtcRegime(selected, config.Regime, config.Trading.TimeframeMinutes, DateTimeOffset.UtcNow)
            : new MarketRegime(false, "BTC regime unavailable in unnormalized test config");
        if (regime.BlockNewEntries)
        {
            Console.WriteLine($"btc-regime: BLOCKING new entries ({regime.Description})");
        }

        var rankPosition = 0;
        foreach (var ranked in EntryRanking.Rank(buyCandidates.Select(candidate => candidate.Rank)))
        {
            rankPosition++;
            var prepared = buyCandidates.First(candidate => ReferenceEquals(candidate.Rank, ranked)).Prepared;

            if (regime.BlockNewEntries)
            {
                decisionRecords.Add(BuildSkippedBuyRecord(
                    prepared,
                    workingPortfolio,
                    "MARKET_REGIME",
                    $"btc-regime block: {regime.Description}"));
                continue;
            }

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

            // Early entries (forming EMA cross) are capped at their own, stricter
            // ranking budget: the best candidate per cycle trades, the rest are
            // recorded and skipped.
            if (prepared.Proposal.EarlyEntryCandidate && rankPosition > config.Strategy.EarlyEntryMaxRank)
            {
                decisionRecords.Add(BuildSkippedBuyRecord(
                    prepared,
                    workingPortfolio,
                    "EARLY_ENTRY_RANK",
                    $"early-entry candidate skipped: ranked #{rankPosition}, only top {config.Strategy.EarlyEntryMaxRank} early entries may enter"));
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
            advice,
            regime,
            workingPortfolio);
        PrintEntryDiagnostics(entryDiagnostics);

        var portfolioAfter = workingPortfolio.Clone();
        PrintPortfolio("portfolio-after", portfolioAfter);

        if (config.DryRun.Enabled)
        {
            dryRunPortfolio.Save(portfolioAfter);
            dryRunPortfolio.AppendCycle(new DryRunCycleRecord
            {
                CycleId = cycleId,
                BotInstanceId = config.BotInstance.Id,
                BotInstanceName = config.BotInstance.Name,
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

    internal sealed record MarketRegime(bool BlockNewEntries, string Description);

    internal static MarketRegime EvaluateBtcRegime(
        IReadOnlyList<InstrumentMarketState> fullStates,
        BtcRegimeOptions regime,
        int timeframeMinutes,
        DateTimeOffset nowUtc)
    {
        var btc = fullStates.FirstOrDefault(state =>
            state.Instrument.Pair.Equals("XBT/EUR", StringComparison.OrdinalIgnoreCase)
            || state.Instrument.Pair.Equals("BTC/EUR", StringComparison.OrdinalIgnoreCase));
        if (btc is null || btc.Candles.Count < regime.BtcTrendMa + 1 || !string.IsNullOrWhiteSpace(btc.DataWarning))
        {
            return new MarketRegime(true, "BTC regime fail-closed: BTC/EUR candles missing or insufficient");
        }

        var newest = btc.Candles[^1];
        var timeframe = TimeSpan.FromMinutes(timeframeMinutes);
        if (newest.OpenTime + timeframe < nowUtc - timeframe)
        {
            return new MarketRegime(true, $"BTC regime fail-closed: newest BTC candle {newest.OpenTime:O} is stale");
        }

        var closes = btc.Candles.Select(candle => candle.Close).ToList();
        var close = closes[^1];
        var ma = closes.TakeLast(regime.BtcTrendMa).Average();
        var previousMa = closes.Skip(closes.Count - regime.BtcTrendMa - 1).Take(regime.BtcTrendMa).Average();
        var slope = ma - previousMa;
        var lookbackIndex = Math.Max(0, closes.Count - 1 - regime.BtcCrashLookback);
        var lookbackClose = closes[lookbackIndex];
        var drawdown = lookbackClose <= 0m ? 0m : (close - lookbackClose) / lookbackClose * 100m;
        var crash = regime.BtcCrashPct > 0m && drawdown <= -regime.BtcCrashPct;
        var downtrend = close < ma && slope < 0m;
        var state = crash ? "CRASH" : downtrend ? "DOWNTREND" : "OK";
        return new MarketRegime(
            crash || downtrend,
            $"state={state} close={close:0.##} ma{regime.BtcTrendMa}={ma:0.##} slope={slope:+0.####;-0.####;0} drawdown{regime.BtcCrashLookback}={drawdown:+0.##;-0.##;0}%");
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
        WatchlistAdvice? advice = null,
        MarketRegime? btcRegime = null,
        PortfolioState? portfolio = null)
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
        var filledMaker = decisionRecords.Count(record => record.DryRunAction.Action == "WOULD_BUY" && record.DryRunAction.FillSource == "REAL");
        var missedMaker = decisionRecords.Count(record => record.DryRunAction.Action == "LIVE_ORDER_FAILED" && record.DryRunAction.Reason.Contains("maker", StringComparison.OrdinalIgnoreCase));
        var makerAttempts = filledMaker + missedMaker;
        var openRisk = portfolio is null ? 0m : EntrySafety.CalculateOpenRiskEur(portfolio, config, out _);

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
            ExcludedPairs: excluded,
            ExecutionMode: config.Entry.UseMarketBuy ? "market" : "maker-post-only",
            FillRate: makerAttempts == 0 ? 0m : decimal.Round(filledMaker / (decimal)makerAttempts, 4),
            PairsPassedSpread: entryEvaluations.Count(item => item.Proposal.SpreadPercent <= config.Strategy.MaxEntrySpreadPercent),
            PairsPassedVolume: entryEvaluations.Count(item => item.MarketState.Quote is { } quote && quote.VolumeToday * item.MarketState.LastPrice >= config.Filters.MinQuoteVolume24h),
            PairsPassedDepth: entryEvaluations.Count(item => item.MarketState.OrderBook is not null),
            OpenRiskEur: openRisk,
            BtcRegimeState: btcRegime?.Description ?? "UNKNOWN");
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
            proposal.HasBullishStructure,
            proposal.EmaFullyConfirmed,
            proposal.BullishEmaGapPercent,
            proposal.EmaGapVelocityPercent,
            proposal.EarlyEntryEligible,
            proposal.EarlyEntryReason,
            proposal.EarlyEntryDiagnosticScore,
            proposal.EarlyEntrySuggestedNotionalEur,
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
        Console.WriteLine(
            $"  executionMode={diagnostics.ExecutionMode} fillRate={diagnostics.FillRate:0.####} " +
            $"pairsPassed(spread={diagnostics.PairsPassedSpread},volume={diagnostics.PairsPassedVolume},depth={diagnostics.PairsPassedDepth}) " +
            $"openRiskEur={diagnostics.OpenRiskEur:0.####} btcRegime={diagnostics.BtcRegimeState}");
        foreach (var candidate in diagnostics.TopCandidates)
        {
            Console.WriteLine(
                $"  top {candidate.Pair}: score={candidate.Score:0.##} desired={candidate.DesiredPosition} spread={candidate.SpreadPercent:0.###}% " +
                $"price={candidate.Price:0.######} bid={candidate.Bid:0.######} ask={candidate.Ask:0.######} " +
                $"priceAction={candidate.PriceActionDirection}({FormatTrend(candidate.PriceActionTrendPercent)}) " +
                $"paState={candidate.PriceActionState}({candidate.PriceActionSamplesAvailable}/{candidate.PriceActionSamplesRequired}) " +
                $"hard={(candidate.HardFiltersPassed ? "pass" : "FAIL")} quality={(candidate.QualityFiltersPassed ? "pass" : "FAIL")} " +
                $"emaGap={FormatTrend(candidate.BullishEmaGapPercent)} emaVelocity={FormatTrend(candidate.EmaGapVelocityPercent)} " +
                $"earlyEligible={(candidate.EarlyEntryEligible ? "yes" : "no")} earlyScore={candidate.EarlyEntryDiagnosticScore:0.##} " +
                $"earlySize={candidate.EarlyEntrySuggestedNotionalEur:0.##} " +
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
                    state.ChangePercent,
                    config.BotInstance.Id))
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

    private async Task ReconcileWithKrakenAsync(
        PortfolioState state,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, decimal> balances;
        try
        {
            balances = await broker!.GetBalanceAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"kraken-sync: FAILED to fetch balances ({ex.Message}); skipping reconciliation");
            return;
        }

        var krakenEur = ResolveKrakenBalance(balances, "EUR", "ZEUR");

        // Kraken asset quantities for every pair in the universe. Quantities only —
        // never a EUR valuation. Valuing Kraken assets at last price while the bot
        // values positions at conservative liquidation (bid - slippage - fee) would
        // make any total-vs-total comparison drift with prices and fees.
        var krakenQuantities = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var instrument in config.CandidateUniverse)
        {
            var baseAsset = instrument.Pair.Split('/')[0];
            var krakenQty = ResolveKrakenBalance(balances, baseAsset, $"X{baseAsset}");
            if (krakenQty > 0m)
            {
                krakenQuantities[instrument.Pair] = krakenQty;
            }
        }

        // External P&L = accumulated cash drift. All spot trades are the bot's own
        // (manual trading happens only on futures), and the bot commits REAL exchange
        // fills, so its CashEur mirrors Kraken EUR minus external activity: any drift
        // is a deposit, a withdrawal, or an internal EUR transfer.
        var cashDrift = krakenEur - state.CashEur;
        if (Math.Abs(cashDrift) > 0.01m)
        {
            state.ExternalPnlEur += cashDrift;
            Console.WriteLine(
                $"kraken-sync: cash {state.CashEur:0.##} → {krakenEur:0.##} (external {cashDrift:+0.##;-0.##} EUR, cumulative {state.ExternalPnlEur:+0.##;-0.##})");
            state.CashEur = krakenEur;
        }

        // Sync positions: adjust quantities to match Kraken, remove vanished positions
        // (a live sell the state file missed, or a failed/partial fill).
        for (var i = state.Positions.Count - 1; i >= 0; i--)
        {
            var position = state.Positions[i];
            if (!krakenQuantities.TryGetValue(position.Pair, out var krakenQty))
            {
                Console.WriteLine($"kraken-sync: position {position.Pair} not found on Kraken (qty was {position.Quantity:0.########}); removing");
                state.Positions.RemoveAt(i);
                continue;
            }

            var qtyDrift = krakenQty - position.Quantity;
            if (Math.Abs(qtyDrift) > position.Quantity * 0.001m)
            {
                Console.WriteLine($"kraken-sync: {position.Pair} qty {position.Quantity:0.########} → {krakenQty:0.########} (drift {qtyDrift:+0.########})");
                position.Quantity = krakenQty;
            }
        }

        Console.WriteLine($"kraken-sync: cash={state.CashEur:0.##} positions={state.Positions.Count} externalPnl={state.ExternalPnlEur:+0.##;-0.##}");
        dryRunPortfolio.Save(state);
    }

    private static decimal ResolveKrakenBalance(IReadOnlyDictionary<string, decimal> balances, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (balances.TryGetValue(key, out var value) && value > 0m)
            {
                return value;
            }
        }

        return 0m;
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

    private IReadOnlyList<InstrumentOptions> BuildActiveInstruments(
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

        var btcInstrument = config.CandidateUniverse.FirstOrDefault(instrument =>
            instrument.Pair.Equals("XBT/EUR", StringComparison.OrdinalIgnoreCase)
            || instrument.Pair.Equals("BTC/EUR", StringComparison.OrdinalIgnoreCase));
        if (btcInstrument is not null
            && !selected.Any(instrument => instrument.Pair.Equals(btcInstrument.Pair, StringComparison.OrdinalIgnoreCase)))
        {
            selected.Add(btcInstrument);
            Console.WriteLine($"watchlist-forced {btcInstrument.Pair}: BTC regime requires full 15m candles");
        }

        if (config.Trading.StrongMoverBackfillEnabled
            && config.Trading.StrongMoverMaxBackfillPairs > 0)
        {
            var backfill = candidates
                .Where(IsStrongMoverBackfillCandidate)
                .Where(candidate => !selected.Any(instrument => instrument.Pair.Equals(candidate.Instrument.Pair, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(candidate => candidate.ChangePercent)
                .ThenByDescending(candidate => candidate.LastVolume * candidate.LastPrice)
                .Take(config.Trading.StrongMoverMaxBackfillPairs)
                .ToList();

            foreach (var candidate in backfill)
            {
                selected.Add(candidate.Instrument);
                Console.WriteLine(
                    $"watchlist-backfill {candidate.Instrument.Pair}: strong clean mover change={candidate.ChangePercent:+0.##;-0.##;0}% " +
                    $"spread={EntryGate.SpreadPercentOf(candidate):0.###}% est24hEur={candidate.LastVolume * candidate.LastPrice:0}");
            }
        }

        return selected;
    }

    private bool IsStrongMoverBackfillCandidate(InstrumentMarketState candidate)
    {
        if (candidate.LastPrice <= 0m || !string.IsNullOrWhiteSpace(candidate.DataWarning))
        {
            return false;
        }

        var spreadPercent = EntryGate.SpreadPercentOf(candidate);
        if (config.Trading.StrongMoverMaxSpreadPercent > 0m && spreadPercent > config.Trading.StrongMoverMaxSpreadPercent)
        {
            return false;
        }

        var volumeEur = candidate.LastVolume * candidate.LastPrice;
        return candidate.ChangePercent >= config.Trading.StrongMoverMinChangePercent
            && volumeEur >= config.Trading.StrongMoverMinDailyVolumeEur;
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
            EntryRejectionReason = holdReasonCode switch
            {
                "EXPLORATORY_RANK" => EntryRejection.ExploratoryRank,
                "EARLY_ENTRY_RANK" => EntryRejection.EarlyEntryRank,
                "MARKET_REGIME" => EntryRejection.MarketRegime,
                _ => EntryRejection.CyclePositionLimit
            },
            SpreadPercent = decimal.Round(prepared.Proposal.SpreadPercent, 3),
            PriceActionDirection = prepared.PriceAction?.Direction,
            PriceActionTrendPercent = prepared.PriceAction?.TrendPercent,
            Exploratory = prepared.Proposal.ExploratoryCandidate,
            HasBullishStructure = prepared.Proposal.HasBullishStructure,
            EmaFullyConfirmed = prepared.Proposal.EmaFullyConfirmed,
            BullishEmaGapPercent = prepared.Proposal.BullishEmaGapPercent,
            EmaGapVelocityPercent = prepared.Proposal.EmaGapVelocityPercent,
            EarlyEntryEligible = prepared.Proposal.EarlyEntryEligible,
            EarlyEntryReason = prepared.Proposal.EarlyEntryReason,
            EarlyEntryDiagnosticScore = prepared.Proposal.EarlyEntryDiagnosticScore,
            EarlyEntrySuggestedNotionalEur = prepared.Proposal.EarlyEntrySuggestedNotionalEur
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
                var brokerOutcome = await RunBrokerAsync(marketState, previewAction, portfolio, cancellationToken);
                brokerVerdict = brokerOutcome.Verdict;
                if (brokerVerdict is null || brokerVerdict.StartsWith("LIVE_SUBMITTED", StringComparison.Ordinal))
                {
                    var liveFill = brokerOutcome.LiveFill;
                    if (brokerVerdict is not null && previewAction.Action == "WOULD_SELL" && liveFill is null)
                    {
                        liveFill = await TryFetchLiveFillAsync(proposal.Pair, brokerOutcome.TxIds, cancellationToken);
                    }

                    if (brokerVerdict is not null && previewAction.Action == "WOULD_BUY" && liveFill is null)
                    {
                        dryRunAction = BuildLiveOrderNotExecutedAction(previewAction, portfolio, $"{brokerVerdict}; no maker fill confirmed");
                        dryRunAction.EntryExecution = brokerOutcome.Diagnostics;
                        goto RecordDecision;
                    }

                    dryRunAction = dryRunPortfolio.Apply(portfolio, marketState, proposal, risk, config.Risk, newPositionsThisCycle, prepared.PriceAction, liveFill);
                    if (liveFill is not null && dryRunAction.Action == "WOULD_BUY")
                    {
                        dryRunAction.MakerOrderFilledEur = liveFill.CostEur;
                        dryRunAction.MakerFillRate = liveFill.CostEur > 0m && previewAction.TargetNotionalEur > 0m
                            ? Math.Min(1m, liveFill.CostEur / previewAction.TargetNotionalEur)
                            : 0m;
                        dryRunAction.TimeToFillMs = liveFill.TimeToFillMs;
                        dryRunAction.RepegCount = liveFill.RepegCount;
                        dryRunAction.EntryExecution = brokerOutcome.Diagnostics;
                    }
                }
                else
                {
                    dryRunAction = BuildLiveOrderNotExecutedAction(previewAction, portfolio, brokerVerdict);
                    dryRunAction.EntryExecution = brokerOutcome.Diagnostics;
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
            brokerVerdict = (await RunBrokerAsync(marketState, dryRunAction, portfolio, cancellationToken)).Verdict;
            if (dryRunAction.Action == "WOULD_BUY")
            {
                // A virtual / validate-only BUY is a MODELED maker fill, not a real
                // exchange-confirmed one: mark it so downstream analytics never mistake
                // the simulated instant-at-bid fill for a confirmed maker fill.
                dryRunAction.EntryExecution = new EntryExecutionDiagnostics
                {
                    ExecutionMode = "virtual",
                    FillSource = "MODELED_MAKER_FILL"
                };
            }
        }

    RecordDecision:
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
            Exploratory = proposal.ExploratoryCandidate,
            HasBullishStructure = proposal.HasBullishStructure,
            EmaFullyConfirmed = proposal.EmaFullyConfirmed,
            BullishEmaGapPercent = proposal.BullishEmaGapPercent,
            EmaGapVelocityPercent = proposal.EmaGapVelocityPercent,
            EarlyEntryEligible = proposal.EarlyEntryEligible,
            EarlyEntryReason = proposal.EarlyEntryReason,
            EarlyEntryDiagnosticScore = proposal.EarlyEntryDiagnosticScore,
            EarlyEntrySuggestedNotionalEur = proposal.EarlyEntrySuggestedNotionalEur
        };
    }

    // Verdict string plus the exchange transaction ids of a submitted live order,
    // so the caller can read back the real fill. TxIds is empty for every
    // non-LIVE_SUBMITTED outcome.
    private sealed record BrokerRunOutcome(
        string? Verdict,
        IReadOnlyList<string> TxIds,
        LiveOrderFill? LiveFill = null,
        EntryExecutionDiagnostics? Diagnostics = null)
    {
        public static readonly BrokerRunOutcome None = new(null, Array.Empty<string>());
        public static BrokerRunOutcome WithVerdict(string verdict) => new(verdict, Array.Empty<string>());
        public static BrokerRunOutcome WithDiagnostics(string verdict, EntryExecutionDiagnostics diagnostics) =>
            new(verdict, Array.Empty<string>(), null, diagnostics);
    }

    // Sends the order to Kraken for the two actionable outcomes only. The validate
    // flag is derived from the live gate: validate=true (exchange checks the order
    // without executing) unless live trading is explicitly enabled and the kill
    // switch is off, in which case validate=false places a real market order.
    private async Task<BrokerRunOutcome> RunBrokerAsync(
        InstrumentMarketState marketState,
        DryRunAction action,
        PortfolioState portfolio,
        CancellationToken cancellationToken)
    {
        if (broker is null)
        {
            return BrokerRunOutcome.None;
        }

        if (action.Action != "WOULD_BUY" && action.Action != "WOULD_SELL")
        {
            return BrokerRunOutcome.None;
        }

        var lotDecimals = marketState.PairRules?.LotDecimals ?? 8;
        var orderMin = marketState.PairRules?.OrderMinimum ?? 0m;
        var volume = TruncateTo(action.Quantity, lotDecimals);

        if (volume <= 0m)
        {
            return BrokerRunOutcome.WithVerdict("SKIPPED: computed volume is zero");
        }

        if (orderMin > 0m && volume < orderMin)
        {
            return BrokerRunOutcome.WithVerdict($"SKIPPED: volume {volume} below pair ordermin {orderMin}");
        }

        var side = action.Action == "WOULD_BUY" ? "buy" : "sell";
        var validate = !LiveOrdersActive;

        // Belt-and-suspenders: never let a live BUY exceed the hard per-order cap,
        // even though the risk gate already approved it upstream.
        if (!validate && side == "buy" && action.TargetNotionalEur > config.Risk.MaxOrderEur)
        {
            return BrokerRunOutcome.WithVerdict($"SKIPPED: live buy notional {action.TargetNotionalEur:0.##} exceeds MaxOrderEur {config.Risk.MaxOrderEur:0.##}");
        }

        if (side == "buy" && !config.Entry.UseMarketBuy)
        {
            return await RunMakerBuyBrokerAsync(marketState, action, portfolio, volume, validate, cancellationToken);
        }

        var result = await broker.AddOrderAsync(marketState.Instrument.KrakenPair, side, volume, validate, cancellationToken);

        if (!result.Success)
        {
            return BrokerRunOutcome.WithVerdict(validate ? $"VALIDATE_REJECTED: {result.Error}" : $"LIVE_ERROR: {result.Error}");
        }

        if (validate)
        {
            var descr = string.IsNullOrWhiteSpace(result.Description) ? string.Empty : $" descr=\"{result.Description}\"";
            return BrokerRunOutcome.WithVerdict($"VALIDATED_OK side={side} vol={volume}{descr}");
        }

        var txids = result.TxIds.Count > 0 ? string.Join(",", result.TxIds) : "(none)";
        return new BrokerRunOutcome($"LIVE_SUBMITTED side={side} vol={volume} txid={txids}", result.TxIds);
    }

    // Per-pair guard so two overlapping cycles can never fire two entry orders for
    // the same pair. The decision loop is sequential today, but the fallback adds a
    // second order path per entry, so this is the concurrency backstop the spec asks
    // for (and the "duplicate concurrent execution" test exercises it).
    private readonly object _inFlightLock = new();
    private readonly HashSet<string> _inFlightEntryPairs = new(StringComparer.OrdinalIgnoreCase);

    private bool TryBeginEntry(string pair)
    {
        lock (_inFlightLock)
        {
            return _inFlightEntryPairs.Add(pair);
        }
    }

    private void EndEntry(string pair)
    {
        lock (_inFlightLock)
        {
            _inFlightEntryPairs.Remove(pair);
        }
    }

    private const int MakerPollIntervalMs = 2000;

    private async Task<BrokerRunOutcome> RunMakerBuyBrokerAsync(
        InstrumentMarketState marketState,
        DryRunAction previewAction,
        PortfolioState portfolio,
        decimal volume,
        bool validate,
        CancellationToken cancellationToken)
    {
        if (broker is null)
        {
            return BrokerRunOutcome.None;
        }

        var pairDecimals = marketState.PairRules?.PairDecimals ?? 8;
        var requestedPrice = TruncateTo(Math.Min(marketState.BestBid, marketState.BestAsk - PriceTick(pairDecimals)), pairDecimals);
        if (requestedPrice <= 0m || requestedPrice >= marketState.BestAsk)
        {
            return BrokerRunOutcome.WithVerdict("SKIPPED: post-only buy price is invalid against best ask");
        }

        if (validate)
        {
            var validation = await broker.AddLimitPostOnlyOrderAsync(marketState.Instrument.KrakenPair, "buy", volume, requestedPrice, validate, cancellationToken);
            if (!validation.Success)
            {
                return BrokerRunOutcome.WithVerdict($"VALIDATE_REJECTED: {validation.Error}");
            }

            return BrokerRunOutcome.WithVerdict($"VALIDATED_OK side=buy postOnly=true price={requestedPrice} vol={volume}");
        }

        var pair = marketState.Instrument.Pair;
        if (!TryBeginEntry(pair))
        {
            return BrokerRunOutcome.WithVerdict($"SKIPPED: entry already in-flight for {pair}");
        }

        try
        {
            var diag = new EntryExecutionDiagnostics
            {
                ExecutionMode = "maker-then-ioc",
                OriginalMakerBid = marketState.BestBid,
                OriginalMakerAsk = marketState.BestAsk,
                FallbackAttempted = false
            };

            var makerOutcome = await RunMakerPhaseAsync(marketState, volume, requestedPrice, pairDecimals, diag, cancellationToken);
            if (makerOutcome is not null)
            {
                return makerOutcome;
            }

            // Maker phase closed with executedVolume == 0 -> IOC taker fallback.
            return await RunIocFallbackBuyAsync(marketState, previewAction, portfolio, volume, diag, cancellationToken);
        }
        finally
        {
            EndEntry(pair);
        }
    }

    // Maker phase. Returns a filled/partial outcome, an early LIVE_ERROR outcome, or
    // null to signal "maker missed with zero executed volume" so the caller runs the
    // IOC fallback. Populates the maker fields of <paramref name="diag"/> in all cases.
    private async Task<BrokerRunOutcome?> RunMakerPhaseAsync(
        InstrumentMarketState marketState,
        decimal volume,
        decimal requestedPrice,
        int pairDecimals,
        EntryExecutionDiagnostics diag,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var overallDeadline = started.AddSeconds(config.Entry.MakerFillTimeoutSec);
        var totalAttempts = config.Entry.MakerRepegs + 1;
        var repegs = 0;
        var makerFinalStatus = "unknown";
        var txids = new List<string>();

        for (var attempt = 0; attempt < totalAttempts; attempt++)
        {
            // Split the remaining budget evenly across the remaining attempts so a
            // repeg genuinely gets a fresh window (~half the timeout with one repeg)
            // instead of only firing after the whole timeout already elapsed.
            var attemptsLeft = totalAttempts - attempt;
            var now = DateTimeOffset.UtcNow;
            if (now >= overallDeadline)
            {
                break;
            }

            var attemptDeadline = now + TimeSpan.FromTicks((overallDeadline - now).Ticks / attemptsLeft);
            var submittedAt = now;

            var result = await broker!.AddLimitPostOnlyOrderAsync(marketState.Instrument.KrakenPair, "buy", volume, requestedPrice, validate: false, cancellationToken);
            if (!result.Success)
            {
                diag.FillSource = "NONE";
                diag.MakerExecutedVolume = 0m;
                diag.MakerRepegs = repegs;
                return BrokerRunOutcome.WithDiagnostics($"LIVE_ERROR: post-only maker buy rejected: {result.Error}", diag);
            }

            var txid = result.TxIds.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(txid))
            {
                diag.FillSource = "NONE";
                return BrokerRunOutcome.WithDiagnostics("LIVE_ERROR: post-only maker buy accepted without txid", diag);
            }

            txids.Add(txid);
            diag.MakerOrderId = txid;
            diag.MakerSubmittedPrice = requestedPrice;

            while (DateTimeOffset.UtcNow < attemptDeadline)
            {
                var query = await broker.QueryOrderAsync(txid, cancellationToken);
                if (query is not null
                    && query.Status.Equals("closed", StringComparison.OrdinalIgnoreCase)
                    && query.VolumeExecuted > 0m
                    && query.AveragePrice > 0m)
                {
                    var fillMs = (long)(DateTimeOffset.UtcNow - submittedAt).TotalMilliseconds;
                    Console.WriteLine($"  maker-entry-fill {marketState.Instrument.Pair}: requested={requestedPrice} fill={query.AveragePrice} timeToFillMs={fillMs} repegCount={repegs} filledEur={query.CostQuote:0.####}");
                    return MakerFillOutcome("MAKER", "makerFilled", marketState, requestedPrice, volume, txid, txids, query, repegs, fillMs, diag);
                }

                if (query is not null && query.VolumeExecuted > 0m)
                {
                    // Partial maker fill: cancel the remainder, commit the real filled
                    // volume, and DO NOT run the IOC fallback for the remainder.
                    await broker.CancelOrderAsync(txid, cancellationToken);
                    var final = await broker.QueryOrderAsync(txid, cancellationToken) ?? query;
                    if (final.VolumeExecuted > 0m && final.AveragePrice > 0m)
                    {
                        var fillMs = (long)(DateTimeOffset.UtcNow - submittedAt).TotalMilliseconds;
                        Console.WriteLine($"  maker-entry-partial {marketState.Instrument.Pair}: requested={requestedPrice} fill={final.AveragePrice} timeToFillMs={fillMs} repegCount={repegs} filledEur={final.CostQuote:0.####}");
                        return MakerFillOutcome("MAKER_PARTIAL", "makerPartial", marketState, requestedPrice, volume, txid, txids, final, repegs, fillMs, diag);
                    }
                }

                var remaining = attemptDeadline - DateTimeOffset.UtcNow;
                var delay = remaining < TimeSpan.FromMilliseconds(MakerPollIntervalMs) ? remaining : TimeSpan.FromMilliseconds(MakerPollIntervalMs);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }
            }

            // Attempt window elapsed with no full fill: cancel and reconcile.
            await broker.CancelOrderAsync(txid, cancellationToken);
            var finalQuery = await broker.QueryOrderAsync(txid, cancellationToken);
            makerFinalStatus = finalQuery?.Status ?? "unknown";
            if (finalQuery is not null && finalQuery.VolumeExecuted > 0m && finalQuery.AveragePrice > 0m)
            {
                var fillMs = (long)(DateTimeOffset.UtcNow - submittedAt).TotalMilliseconds;
                return MakerFillOutcome("MAKER_PARTIAL", "makerPartialAfterCancel", marketState, requestedPrice, volume, txid, txids, finalQuery, repegs, fillMs, diag);
            }

            // Only repeg once the FIRST order is confirmed dead (final status + zero
            // executed volume). If the cancel is unconfirmed or the state is ambiguous
            // (query failed, still 'open'), placing a second maker could leave two live
            // orders and double the filled volume — so stop here and let the IOC
            // fallback's final-state guard decide (it suppresses the IOC unless the
            // maker is confirmed cancelled with zero fill).
            var makerConfirmedDead = finalQuery is not null && IsFinalOrderStatus(finalQuery.Status);
            if (!makerConfirmedDead)
            {
                Console.WriteLine($"  maker-entry-repeg-suppressed {marketState.Instrument.Pair}: first maker not confirmed cancelled (status={makerFinalStatus}); no second maker placed");
                break;
            }

            // Zero fill on this attempt. Repeg with a fresh quote if budget and time remain.
            if (attempt < totalAttempts - 1 && DateTimeOffset.UtcNow < overallDeadline)
            {
                repegs++;
                var refreshed = await RefreshLightStateAsync(marketState.Instrument, cancellationToken);
                var repegBid = refreshed?.BestBid ?? marketState.BestBid;
                var repegAsk = refreshed?.BestAsk ?? marketState.BestAsk;
                requestedPrice = TruncateTo(Math.Min(repegBid, repegAsk - PriceTick(pairDecimals)), pairDecimals);
                Console.WriteLine($"  maker-entry-repeg {marketState.Instrument.Pair}: attempt={repegs} newPrice={requestedPrice}");
            }
        }

        diag.MakerExecutedVolume = 0m;
        diag.MakerRepegs = repegs;
        diag.MakerWaitMilliseconds = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
        diag.MakerFinalStatus = makerFinalStatus;
        Console.WriteLine($"  maker-entry-missed {marketState.Instrument.Pair}: repegs={repegs} finalStatus={makerFinalStatus}; attempting IOC fallback");
        return null;
    }

    private static BrokerRunOutcome MakerFillOutcome(
        string fillSource,
        string verdictTag,
        InstrumentMarketState marketState,
        decimal requestedPrice,
        decimal volume,
        string txid,
        IReadOnlyList<string> txids,
        BrokerOrderQuery query,
        int repegs,
        long fillMs,
        EntryExecutionDiagnostics diag)
    {
        diag.MakerExecutedVolume = query.VolumeExecuted;
        diag.MakerAverageFillPrice = query.AveragePrice;
        diag.MakerFeeEur = query.FeeQuote;
        diag.MakerWaitMilliseconds = fillMs;
        diag.MakerRepegs = repegs;
        diag.MakerFinalStatus = query.Status;
        diag.FillSource = fillSource;
        SetFinalFill(diag, query.AveragePrice, query.VolumeExecuted, query.FeeQuote);
        var reasonCode = fillSource == "MAKER" ? "LIVE_MAKER_FILLED" : "LIVE_MAKER_PARTIAL_FILLED";
        return new BrokerRunOutcome(
            $"LIVE_SUBMITTED side=buy postOnly=true {reasonCode} price={requestedPrice} vol={volume} txid={txid} {verdictTag}=true",
            txids,
            new LiveOrderFill(query.AveragePrice, query.VolumeExecuted, query.CostQuote, query.FeeQuote, repegs, fillMs, requestedPrice),
            diag);
    }

    // Phase 2 — IOC taker fallback, entered only when the maker phase confirmed zero
    // executed volume. It re-checks the ORIGINAL intent and the execution/risk guards
    // against a FRESH quote and the CURRENT portfolio (never re-running strategy /
    // ranking), guards against a late maker fill racing the cancel, and submits a
    // hard slippage-capped IOC limit — never an unrestricted market order.
    private async Task<BrokerRunOutcome> RunIocFallbackBuyAsync(
        InstrumentMarketState marketState,
        DryRunAction previewAction,
        PortfolioState portfolio,
        decimal volume,
        EntryExecutionDiagnostics diag,
        CancellationToken cancellationToken)
    {
        diag.FallbackAttempted = true;
        var pair = previewAction.Pair;

        // (a) Race guard: reconcile the FINAL maker state before sending any taker.
        if (!string.IsNullOrWhiteSpace(diag.MakerOrderId))
        {
            var finalMaker = await broker!.QueryOrderAsync(diag.MakerOrderId, cancellationToken);
            if (finalMaker is null || !IsFinalOrderStatus(finalMaker.Status))
            {
                diag.MakerFinalStatus = finalMaker?.Status ?? "unknown";
                diag.FillSource = "NONE";
                return BrokerRunOutcome.WithDiagnostics(
                    $"LIVE_FALLBACK_SKIPPED_UNKNOWN_MAKER_STATE: maker order state '{diag.MakerFinalStatus}' not final; IOC suppressed to avoid a double position",
                    diag);
            }

            diag.MakerFinalStatus = finalMaker.Status;
            if (finalMaker.VolumeExecuted > 0m && finalMaker.AveragePrice > 0m)
            {
                // Late maker fill after cancel: commit that fill, never send the IOC.
                diag.MakerExecutedVolume = finalMaker.VolumeExecuted;
                diag.MakerAverageFillPrice = finalMaker.AveragePrice;
                diag.MakerFeeEur = finalMaker.FeeQuote;
                diag.FillSource = "MAKER_PARTIAL";
                SetFinalFill(diag, finalMaker.AveragePrice, finalMaker.VolumeExecuted, finalMaker.FeeQuote);
                Console.WriteLine($"  fallback-late-maker-fill {pair}: vol={finalMaker.VolumeExecuted} price={finalMaker.AveragePrice}; IOC suppressed");
                return new BrokerRunOutcome(
                    $"LIVE_SUBMITTED side=buy LIVE_FALLBACK_SKIPPED_LATE_MAKER_FILL txid={diag.MakerOrderId} makerLateFill=true",
                    new[] { diag.MakerOrderId! },
                    new LiveOrderFill(
                        finalMaker.AveragePrice,
                        finalMaker.VolumeExecuted,
                        finalMaker.CostQuote,
                        finalMaker.FeeQuote,
                        diag.MakerRepegs ?? 0,
                        diag.MakerWaitMilliseconds ?? 0,
                        diag.MakerSubmittedPrice ?? 0m),
                    diag);
            }
        }

        // (b) Fresh quote — never reuse the stale cycle snapshot or `last`.
        var fresh = await RefreshLightStateAsync(marketState.Instrument, cancellationToken);
        var quoteFresh = fresh is not null
            && string.IsNullOrWhiteSpace(fresh.DataWarning)
            && fresh.Quote is not null
            && fresh.BestBid > 0m
            && fresh.BestAsk > 0m;
        var freshBid = fresh?.BestBid ?? 0m;
        var freshAsk = fresh?.BestAsk ?? 0m;
        var spread = fresh is not null ? EntryGate.SpreadPercentOf(fresh) : 0m;
        var pairRules = fresh?.PairRules ?? marketState.PairRules;
        var pairDecimals = pairRules?.PairDecimals ?? 8;

        diag.FallbackBid = freshBid;
        diag.FallbackAsk = freshAsk;
        diag.FallbackSpreadPercent = decimal.Round(spread, 4);
        if (diag.OriginalMakerBid is > 0m)
        {
            var refBid = diag.OriginalMakerBid.Value;
            diag.FallbackBidMovementPercent = decimal.Round((freshBid - refBid) / refBid * 100m, 4);
            diag.FallbackAskDisplacementPercent = decimal.Round((freshAsk - refBid) / refBid * 100m, 4);
        }

        // (c) Re-validate the original BUY intent against fresh quote + current portfolio.
        var alreadyOpen = portfolio.Positions.Any(position => position.Pair.Equals(pair, StringComparison.OrdinalIgnoreCase));
        var recentBuys = portfolio.ActionHistory.Count(history => history.LastBuyAtUtc is { } lastBuy && lastBuy > DateTimeOffset.UtcNow.AddHours(-1));
        var guard = FallbackEntryGuards.Evaluate(new FallbackEntryGuards.Inputs(
            OriginalMakerBid: diag.OriginalMakerBid ?? 0m,
            FreshBid: freshBid,
            FreshAsk: freshAsk,
            QuoteFresh: quoteFresh,
            SpreadPercent: spread,
            MaxEntrySpreadPercent: config.Strategy.MaxEntrySpreadPercent,
            MaxBuySlippagePercent: config.Entry.MaxBuySlippagePercent,
            TargetNotionalEur: previewAction.TargetNotionalEur,
            Volume: volume,
            PairDecimals: pairDecimals,
            OrderMinimum: pairRules?.OrderMinimum ?? 0m,
            CostMinimum: pairRules?.CostMinimum ?? 0m,
            PositionAlreadyOpen: alreadyOpen,
            EntryInFlight: false,
            OpenPositions: portfolio.Positions.Count,
            MaxOpenPositions: config.Risk.MaxOpenPositions,
            CashEur: portfolio.CashEur,
            CashReserveEur: config.PositionSizing.CashReserveEur,
            CurrentExposureEur: portfolio.PositionsValueEur,
            MaxTotalExposureEur: config.Risk.MaxTotalExposureEur,
            RecentEntriesLastHour: recentBuys,
            MaxNewPositionsPerHour: config.ExecutionPolicy.MaxNewPositionsPerHour));

        diag.FallbackMaxAllowedPrice = guard.MaxAllowedPrice;

        if (guard.Verdict != FallbackEntryGuards.Verdict.Allow)
        {
            diag.FillSource = "NONE";
            var code = guard.Verdict switch
            {
                FallbackEntryGuards.Verdict.RejectStaleQuote => "LIVE_FALLBACK_REJECTED_STALE_QUOTE",
                FallbackEntryGuards.Verdict.RejectSpread => "LIVE_FALLBACK_REJECTED_SPREAD",
                FallbackEntryGuards.Verdict.RejectSlippage => "LIVE_FALLBACK_REJECTED_SLIPPAGE",
                _ => "LIVE_FALLBACK_REJECTED_RISK"
            };
            Console.WriteLine($"  fallback-rejected {pair}: {code} ({guard.Reason})");
            return BrokerRunOutcome.WithDiagnostics($"{code}: {guard.Reason}", diag);
        }

        // (d) Submit the IOC limit BUY at the fresh ask (hard slippage-capped).
        diag.FallbackSubmittedPrice = guard.IocPrice;
        var ioc = await broker!.AddLimitIocOrderAsync(marketState.Instrument.KrakenPair, "buy", volume, guard.IocPrice, validate: false, cancellationToken);
        if (!ioc.Success)
        {
            diag.FillSource = "NONE";
            return BrokerRunOutcome.WithDiagnostics($"LIVE_FALLBACK_ORDER_FAILED: IOC buy rejected: {ioc.Error}", diag);
        }

        var iocTxid = ioc.TxIds.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(iocTxid))
        {
            diag.FillSource = "NONE";
            return BrokerRunOutcome.WithDiagnostics("LIVE_FALLBACK_ORDER_FAILED: IOC buy accepted without txid", diag);
        }

        diag.FallbackOrderId = iocTxid;
        var fill = await ReadImmediateFillAsync(iocTxid, cancellationToken);
        var iocFinal = await broker.QueryOrderAsync(iocTxid, cancellationToken);
        diag.FallbackFinalStatus = iocFinal?.Status ?? "unknown";
        if (fill is not null)
        {
            diag.FallbackExecutedVolume = fill.VolumeExecuted;
            diag.FallbackAverageFillPrice = fill.AveragePrice;
            diag.FallbackFeeEur = fill.FeeEur;
            diag.FillSource = "IOC_FALLBACK";
            SetFinalFill(diag, fill.AveragePrice, fill.VolumeExecuted, fill.FeeEur);
            Console.WriteLine($"  fallback-ioc-fill {pair}: price={guard.IocPrice} vol={fill.VolumeExecuted} avg={fill.AveragePrice} fee={fill.FeeEur:0.####}");
            return new BrokerRunOutcome(
                $"LIVE_SUBMITTED side=buy fallback=true LIVE_FALLBACK_FILLED price={guard.IocPrice} vol={fill.VolumeExecuted} txid={iocTxid}",
                new[] { iocTxid },
                fill,
                diag);
        }

        diag.FallbackExecutedVolume = 0m;
        diag.FillSource = "NONE";
        return BrokerRunOutcome.WithDiagnostics(
            $"LIVE_FALLBACK_ORDER_FAILED: IOC accepted but produced no confirmed fill (status={diag.FallbackFinalStatus})",
            diag);
    }

    private static void SetFinalFill(EntryExecutionDiagnostics diag, decimal price, decimal volume, decimal fee)
    {
        diag.FinalAverageFillPrice = price;
        diag.FinalExecutedVolume = volume;
        diag.FinalFeeEur = fee;
    }

    private static bool IsFinalOrderStatus(string status) =>
        status.Equals("closed", StringComparison.OrdinalIgnoreCase)
        || status.Equals("canceled", StringComparison.OrdinalIgnoreCase)
        || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
        || status.Equals("expired", StringComparison.OrdinalIgnoreCase);

    // Reads back a just-submitted IOC/taker order. Unlike TryFetchLiveFillAsync it
    // accepts a canceled-with-partial-fill order (the normal terminal state of an IOC
    // that only partially filled), returning the real executed volume/price/fee.
    private async Task<LiveOrderFill?> ReadImmediateFillAsync(string txid, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= LiveFillQueryAttempts; attempt++)
        {
            var query = await broker!.QueryOrderAsync(txid, cancellationToken);
            if (query is not null && query.VolumeExecuted > 0m && query.AveragePrice > 0m)
            {
                return new LiveOrderFill(query.AveragePrice, query.VolumeExecuted, query.CostQuote, query.FeeQuote);
            }

            // A final status with zero executed volume is a definitive no-fill.
            if (query is not null && IsFinalOrderStatus(query.Status))
            {
                return null;
            }

            if (attempt < LiveFillQueryAttempts)
            {
                await Task.Delay(LiveFillQueryDelay, cancellationToken);
            }
        }

        return null;
    }

    private async Task<InstrumentMarketState?> RefreshLightStateAsync(InstrumentOptions instrument, CancellationToken cancellationToken)
    {
        try
        {
            var states = await marketDataSource.GetLightMarketStatesAsync(new[] { instrument }, cancellationToken);
            return states.FirstOrDefault(state => state.Instrument.Pair.Equals(instrument.Pair, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"  fallback-quote-refresh {instrument.Pair}: failed ({ex.Message})");
            return null;
        }
    }

    // How long to keep asking the exchange for the fill of a just-submitted market
    // order. Market orders normally close within a second; the retry only covers
    // API propagation lag.
    private const int LiveFillQueryAttempts = 4;
    private static readonly TimeSpan LiveFillQueryDelay = TimeSpan.FromMilliseconds(750);

    // Reads back the REAL fill of a submitted live order. Returns null when the
    // fill cannot be confirmed (no txid, order still open, API error) — the caller
    // then commits the modeled fill and flags the record for manual reconciliation.
    private async Task<LiveOrderFill?> TryFetchLiveFillAsync(
        string pair,
        IReadOnlyList<string> txIds,
        CancellationToken cancellationToken)
    {
        if (broker is null || txIds.Count == 0)
        {
            return null;
        }

        var txid = txIds[0];
        for (var attempt = 1; attempt <= LiveFillQueryAttempts; attempt++)
        {
            var query = await broker.QueryOrderAsync(txid, cancellationToken);
            if (query is not null
                && query.Status.Equals("closed", StringComparison.OrdinalIgnoreCase)
                && query.VolumeExecuted > 0m
                && query.AveragePrice > 0m)
            {
                Console.WriteLine(
                    $"  live-fill {pair}: txid={txid} vol={query.VolumeExecuted} price={query.AveragePrice} cost={query.CostQuote:0.####} fee={query.FeeQuote:0.####}");
                return new LiveOrderFill(query.AveragePrice, query.VolumeExecuted, query.CostQuote, query.FeeQuote);
            }

            if (attempt < LiveFillQueryAttempts)
            {
                await Task.Delay(LiveFillQueryDelay, cancellationToken);
            }
        }

        Console.WriteLine($"  !!! live-fill {pair}: could not confirm fill for txid={txid} after {LiveFillQueryAttempts} attempts; committing MODELED fill (reconcile against Kraken history)");
        return null;
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

    private static decimal PriceTick(int pairDecimals)
    {
        var factor = 1m;
        for (var i = 0; i < Math.Max(0, pairDecimals); i++)
        {
            factor *= 10m;
        }

        return 1m / factor;
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
