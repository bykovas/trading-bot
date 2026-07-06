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
                if (desired != FuturesDesiredExposure.Flat && newEntriesThisCycle > 0)
                {
                    desired = FuturesDesiredExposure.Flat;
                    riskReasons = new[] { "entry skipped: one new futures position per cycle" };
                    riskApproved = false;
                }
                else
                {
                    var evaluation = riskManager.EvaluateEntry(
                        state, desired, markPrice,
                        config.Futures.TargetNotionalEur,
                        config.Futures.DefaultLeverage,
                        portfolio.UsedMarginEur(state));
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
            EntryDiagnostics = null
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
        SpreadPercent = 0m,
        HasBullishStructure = signal.HasBullishStructure,
        EmaFullyConfirmed = signal.EmaFullyConfirmed,
        BullishEmaGapPercent = signal.BullishEmaGapPercent,
        EmaGapVelocityPercent = signal.EmaGapVelocityPercent
    };

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
