namespace TradingBot.FuturesWorker;

// Dry-run futures cycle loop (blueprint phase 4): fetch market data, score with
// Core's SignalScorer, map intent to FuturesDesiredExposure, gate through the
// margin risk manager, simulate fills in the virtual margin ledger, tick the
// TP/SL orchestrator, and persist the cycle record under the futures instance
// id. There is deliberately no live order path anywhere in this class.
internal sealed class FuturesDecisionWorker(
    FuturesBotConfiguration config,
    IMarketDataSource marketDataSource,
    IndicatorEngine indicatorEngine,
    LongShortStrategy strategy,
    MarginRiskManager riskManager,
    FuturesVirtualPortfolio portfolio,
    TpSlOrchestrator tpSl,
    IClock? clock = null)
{
    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private readonly WorkerBuildInfo _buildInfo = WorkerBuildInfo.FromEnvironment();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine($"futures worker start instance={config.BotInstance.Id} marketDataMode={config.Kraken.MarketDataMode} dryRunOnly=true");
        Console.WriteLine($"futures limits: leverage<= {config.Futures.MaxLeverage:0.#}x, positions<= {config.Futures.MaxPositions}, shorts={(config.Futures.AllowShorts ? "allowed" : "off")}, flip=forbidden");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"futures cycle FAILED: {ex.Message}");
            }

            if (config.Worker.RunOnce)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(config.Worker.LoopIntervalSeconds), cancellationToken);
        }
    }

    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        var utc = _clock.UtcNow;
        var cycleId = $"{config.BotInstance.Id}-{utc:yyyyMMddHHmmss}";
        Console.WriteLine($"futures cycle={cycleId} utc={utc:O}");

        var universe = config.CandidateUniverse.Where(instrument => instrument.Enabled).ToList();
        var state = portfolio.Load();
        var portfolioBefore = state.Clone();

        var lightStates = await marketDataSource.GetLightMarketStatesAsync(universe, cancellationToken);
        PersistMarketSnapshots(cycleId, utc, lightStates);

        // Held pairs are always evaluated; new-entry candidates come from the
        // configured universe capped by MaxActiveInstruments.
        var heldPairs = state.Positions.Select(position => position.Pair).ToHashSet();
        var active = lightStates
            .OrderByDescending(candidate => heldPairs.Contains(candidate.Instrument.Pair))
            .ThenByDescending(candidate => candidate.LastVolume * candidate.LastPrice)
            .Take(Math.Max(config.Trading.MaxActiveInstruments, heldPairs.Count))
            .Select(candidate => candidate.Instrument)
            .ToList();

        var fullStates = await marketDataSource.GetFullMarketStatesAsync(active, config.Trading.TimeframeMinutes, lightStates, cancellationToken);
        var decisions = new List<DryRunDecisionRecord>();
        var newEntriesThisCycle = 0;

        foreach (var marketState in fullStates.Where(candidate => candidate.IsUsable))
        {
            var pair = marketState.Instrument.Pair;
            var markPrice = marketState.LastPrice;
            var indicators = indicatorEngine.Calculate(marketState.Candles, config.Strategy);
            var signal = SignalScorer.Evaluate(marketState, indicators, config.Strategy);
            var held = state.Positions.FirstOrDefault(position => position.Pair == pair);

            FuturesFillResult fill;
            IReadOnlyList<string> riskReasons;
            var riskApproved = true;

            if (held is not null)
            {
                portfolio.MarkToMarket(state, pair, markPrice);

                // TP/SL first: hard exits outrank the strategy's held desire.
                var trigger = tpSl.Evaluate(held, markPrice, marketState.LastPrice);
                if (trigger is not null)
                {
                    fill = portfolio.Apply(
                        state, pair, FuturesDesiredExposure.Flat, markPrice,
                        0m, held.Leverage ?? 1m, reduceOnly: true,
                        reason: $"{trigger.Kind} simulated trigger at {trigger.TriggerPrice:0.####}",
                        exitTriggerSource: trigger.TriggerSource);
                    fill.Action.ExitReasonCode = trigger.Kind == "STOP_LOSS" ? "SELL_STOP_LOSS" : "SELL_TAKE_PROFIT";
                    riskReasons = new[] { $"hard exit: {trigger.Kind} via {trigger.TriggerSource} price" };
                }
                else
                {
                    var desired = strategy.DecideHeld(held, signal);
                    fill = portfolio.Apply(
                        state, pair, desired, markPrice,
                        0m, held.Leverage ?? 1m,
                        reduceOnly: desired == FuturesDesiredExposure.Flat,
                        reason: desired == FuturesDesiredExposure.Flat ? "signal reversal close" : string.Empty);
                    riskReasons = new[] { "holding existing exposure; TP/SL and reversal rules govern this pair" };
                }
            }
            else
            {
                var desired = strategy.DecideEntry(signal);
                var remainingSlots = Math.Max(0, config.Futures.MaxPositions - state.Positions.Count);
                if (desired == FuturesDesiredExposure.Flat)
                {
                    riskReasons = new[] { ExplainNoEntry(signal) };
                    riskApproved = true;
                }
                else if (newEntriesThisCycle >= remainingSlots)
                {
                    desired = FuturesDesiredExposure.Flat;
                    riskReasons = new[] { $"entry skipped: futures position slots exhausted ({config.Futures.MaxPositions} max)" };
                    riskApproved = false;
                }
                else
                {
                    var evaluation = riskManager.EvaluateEntry(
                        state, desired, markPrice,
                        config.Futures.TargetNotionalEur,
                        config.Futures.DefaultLeverage,
                        portfolio.UsedMarginEur(state),
                        marketState.Quote?.FundingRatePercent);
                    riskReasons = evaluation.Reasons;
                    riskApproved = evaluation.Approved;
                    if (!evaluation.Approved)
                    {
                        desired = FuturesDesiredExposure.Flat;
                    }
                }

                fill = portfolio.Apply(
                    state, pair, desired, markPrice,
                    config.Futures.TargetNotionalEur, config.Futures.DefaultLeverage);
                if (fill.PositionOpened)
                {
                    newEntriesThisCycle++;
                }
            }

            decisions.Add(BuildDecisionRecord(marketState, indicators, signal, fill, riskApproved, riskReasons));
        }

        portfolio.Save(state);
        portfolio.Store.AppendCycle(new DryRunCycleRecord
        {
            CycleId = cycleId,
            BotInstanceId = config.BotInstance.Id,
            BotInstanceName = config.BotInstance.Name,
            Utc = utc,
            MarketDataMode = config.Kraken.MarketDataMode,
            AiProvider = "none",
            Worker = _buildInfo,
            ActivePairs = active.Select(instrument => instrument.Pair).ToList(),
            Decisions = decisions,
            PortfolioBefore = portfolioBefore,
            PortfolioAfter = state.Clone(),
            EntryDiagnostics = BuildEntryDiagnostics(lightStates, active, fullStates, decisions)
        });
        Console.WriteLine($"futures cycle done: decisions={decisions.Count} cash={state.CashEur:0.####} total={state.TotalValueEur:0.####} positions={state.Positions.Count}");
    }

    private static DryRunDecisionRecord BuildDecisionRecord(
        InstrumentMarketState marketState,
        IndicatorSnapshot indicators,
        TechnicalSignal signal,
        FuturesFillResult fill,
        bool riskApproved,
        IReadOnlyList<string> riskReasons) => new()
    {
        Pair = marketState.Instrument.Pair,
        Price = marketState.LastPrice,
        FastEma = indicators.FastEma,
        SlowEma = indicators.SlowEma,
        Rsi = indicators.Rsi,
        DesiredPosition = fill.Action.Side is null ? "FLAT" : $"{fill.Action.Side}",
        Score = signal.Score,
        RiskApproved = riskApproved,
        RiskReasons = riskReasons,
        Contributions = signal.Contributions,
        DryRunAction = fill.Action,
        EntryRejectionReason = fill.Action.Action == "NO_ORDER"
            ? (riskApproved ? "REJECT_NO_FUTURES_SIGNAL" : "REJECT_FUTURES_RISK")
            : null,
        SpreadPercent = SpreadPercentOf(marketState),
        HasBullishStructure = signal.HasBullishStructure,
        EmaFullyConfirmed = signal.EmaFullyConfirmed,
        BullishEmaGapPercent = signal.BullishEmaGapPercent,
        EmaGapVelocityPercent = signal.EmaGapVelocityPercent
    };

    private string ExplainNoEntry(TechnicalSignal signal)
    {
        if (signal.HasBullishStructure
            && !signal.EmaFullyConfirmed
            && signal.Score >= config.Strategy.MinimumLongScore)
        {
            return $"no futures long: score {signal.Score:0.##} passed but EMA gap {signal.BullishEmaGapPercent?.ToString("0.###") ?? "unknown"}% is below required {config.Strategy.MinimumEmaGapPercent:0.###}%";
        }

        if (signal.HasBullishStructure && !signal.EmaFullyConfirmed)
        {
            return $"no futures long: EMA gap {signal.BullishEmaGapPercent?.ToString("0.###") ?? "unknown"}% is below required {config.Strategy.MinimumEmaGapPercent:0.###}% and score {signal.Score:0.##} is below {config.Strategy.MinimumLongScore:0.##}";
        }

        if (signal.EmaFullyConfirmed && signal.Score < config.Strategy.MinimumLongScore)
        {
            return $"no futures long: EMA confirmed but score {signal.Score:0.##} is below required {config.Strategy.MinimumLongScore:0.##}";
        }

        if (signal.AllowsShort && !config.Futures.AllowShorts)
        {
            return "no futures short: short candidate detected but Futures.AllowShorts=false";
        }

        if (signal.HasBearishStructure && !signal.AllowsShort)
        {
            return $"no futures short: bearish EMA structure present but downside confirmation did not clear the short gate; long score {signal.Score:0.##}";
        }

        return $"no futures signal: score {signal.Score:0.##}, long threshold {config.Strategy.MinimumLongScore:0.##}, EMA gap requirement {config.Strategy.MinimumEmaGapPercent:0.###}%";
    }

    private CycleEntryDiagnostics BuildEntryDiagnostics(
        IReadOnlyList<InstrumentMarketState> lightStates,
        IReadOnlyList<InstrumentOptions> active,
        IReadOnlyList<InstrumentMarketState> fullStates,
        IReadOnlyList<DryRunDecisionRecord> decisions)
    {
        var activePairs = active.Select(instrument => instrument.Pair).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var decisionByPair = decisions.ToDictionary(decision => decision.Pair, StringComparer.OrdinalIgnoreCase);
        var entryDecisions = decisions
            .Where(decision => decision.DryRunAction.Action is "WOULD_OPEN_LONG" or "WOULD_OPEN_SHORT" or "NO_ORDER")
            .ToList();
        var rejectionCounts = decisions
            .Where(decision => !decision.RiskApproved || decision.DryRunAction.Action == "NO_ORDER")
            .Select(decision => decision.RiskReasons.FirstOrDefault() ?? decision.DryRunAction.Reason)
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .GroupBy(reason => reason)
            .ToDictionary(group => group.Key, group => group.Count());

        var topCandidates = fullStates
            .Where(state => decisionByPair.ContainsKey(state.Instrument.Pair))
            .OrderByDescending(state => decisionByPair[state.Instrument.Pair].Score)
            .Take(10)
            .Select(state =>
            {
                var decision = decisionByPair[state.Instrument.Pair];
                var action = decision.DryRunAction;
                var riskRejected = !decision.RiskApproved;
                return new CandidateDiagnostic(
                    state.Instrument.Pair,
                    decision.Score,
                    decision.DesiredPosition,
                    decision.SpreadPercent,
                    decision.Price,
                    state.BestBid,
                    state.BestAsk,
                    decision.HasBullishStructure,
                    decision.EmaFullyConfirmed,
                    decision.BullishEmaGapPercent,
                    decision.EmaGapVelocityPercent,
                    false,
                    null,
                    decision.Score,
                    action.TargetNotionalEur,
                    "UNKNOWN",
                    null,
                    "NOT_USED",
                    0,
                    0,
                    null,
                    null,
                    string.IsNullOrWhiteSpace(state.DataWarning),
                    decision.RiskApproved,
                    action.Action == "NO_ORDER" || riskRejected ? decision.RiskReasons : Array.Empty<string>(),
                    decision.EntryRejectionReason,
                    false);
            })
            .ToList();

        var excludedPairs = lightStates
            .Where(state => !activePairs.Contains(state.Instrument.Pair))
            .Select(state => new ExcludedPairDiagnostic(
                state.Instrument.Pair,
                "not selected for futures full-data evaluation",
                state.LastPrice,
                state.ChangePercent,
                Est24hVolumeEur: state.LastVolume,
                SpreadPercent: SpreadPercentOf(state)))
            .ToList();

        var chosen = decisions.FirstOrDefault(decision => decision.DryRunAction.Action is "WOULD_OPEN_LONG" or "WOULD_OPEN_SHORT")?.Pair;
        var noTradeReason = chosen is not null
            ? null
            : rejectionCounts.Count == 0 ? "NO_FUTURES_SIGNAL" : rejectionCounts.OrderByDescending(item => item.Value).First().Key;

        return new CycleEntryDiagnostics(
            lightStates.Count,
            fullStates.Count,
            entryDecisions.Count,
            PriceActionReadyCount: 0,
            ScoreAtLeast075: decisions.Count(decision => decision.Score >= 0.75m),
            ScoreAtLeast080: decisions.Count(decision => decision.Score >= 0.80m),
            ScoreAtLeast085: decisions.Count(decision => decision.Score >= 0.85m),
            ScoreAtLeast090: decisions.Count(decision => decision.Score >= 0.90m),
            HardFilterPassCount: decisions.Count(decision => decision.RiskApproved),
            EligibleEntryCandidates: decisions.Count(decision => decision.DryRunAction.Action is "WOULD_OPEN_LONG" or "WOULD_OPEN_SHORT"),
            ChosenPair: chosen,
            NoTradeReason: noTradeReason,
            RejectionCounts: rejectionCounts,
            TopCandidates: topCandidates,
            ExcludedPairs: excludedPairs);
    }

    private static decimal SpreadPercentOf(InstrumentMarketState marketState)
    {
        var mid = (marketState.BestBid + marketState.BestAsk) / 2m;
        return mid <= 0m ? 0m : decimal.Round((marketState.BestAsk - marketState.BestBid) / mid * 100m, 4);
    }

    private void PersistMarketSnapshots(string cycleId, DateTimeOffset utc, IReadOnlyList<InstrumentMarketState> lightStates)
    {
        try
        {
            var snapshots = lightStates
                .Where(state => state.LastPrice > 0m)
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
            portfolio.Store.AppendMarketSnapshots(snapshots);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"futures market-snapshots FAILED for {cycleId} ({ex.Message}); continuing cycle");
        }
    }
}
