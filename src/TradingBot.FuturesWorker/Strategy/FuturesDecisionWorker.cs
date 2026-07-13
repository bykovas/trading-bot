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
    IFuturesBroker? broker = null,
    IClock? clock = null,
    IUniverseProvider? universeProvider = null)
{
    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private readonly IUniverseProvider _universeProvider = universeProvider ?? new ConfiguredUniverseProvider(config.CandidateUniverse);
    private readonly WorkerBuildInfo _buildInfo = WorkerBuildInfo.FromEnvironment();
    private readonly IReadOnlyDictionary<string, string> _pairToCorrelationGroup =
        CorrelationRiskResolver.BuildPairToGroup(config.CorrelationRisk);

    // Rolling per-pair light snapshot history feeding the anti-lag price-action
    // guard (same mechanism as the spot worker). In-memory; hydrated on startup
    // from the persisted market snapshots so the guard is not blind after restarts.
    private readonly SnapshotPriceHistory _priceHistory = new();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (IsLiveInstance && !config.Futures.LiveTradingEnabled)
        {
            throw new InvalidOperationException("Bot instance is futures-live but TRADINGBOT_FUTURES_LIVE_TRADING_ENABLED is not true; refusing to create virtual positions under a live instance id.");
        }

        if (config.Futures.LiveTradingEnabled && broker?.IsConfigured != true)
        {
            throw new InvalidOperationException("TRADINGBOT_FUTURES_LIVE_TRADING_ENABLED=true but Kraken Futures API keys are missing or broker is not configured.");
        }

        Console.WriteLine($"futures worker start instance={config.BotInstance.Id} marketDataMode={config.Kraken.MarketDataMode} dryRunOnly={!config.Futures.LiveTradingEnabled}");
        if (config.Futures.LiveTradingEnabled)
        {
            Console.WriteLine("!!! FUTURES LIVE TRADING ENABLED: approved decisions will place REAL Kraken Futures market orders !!!");
        }
        Console.WriteLine($"futures limits: leverage<= {config.Futures.MaxLeverage:0.#}x, positions<= {config.Futures.MaxPositions}, shorts={(config.Futures.AllowShorts ? "allowed" : "off")}, flip=forbidden");
        Console.WriteLine($"futures exit checks: fastExit={config.Futures.FastExitCheckSeconds}s fullCycle={config.Worker.LoopIntervalSeconds}s");
        HydratePriceHistory();

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

            await WaitUntilNextDecisionCycleAsync(cancellationToken);
        }
    }

    private async Task WaitUntilNextDecisionCycleAsync(CancellationToken cancellationToken)
    {
        var nextDecisionUtc = DateTimeOffset.UtcNow.AddSeconds(config.Worker.LoopIntervalSeconds);
        var fastExitInterval = TimeSpan.FromSeconds(config.Futures.FastExitCheckSeconds);
        while (!cancellationToken.IsCancellationRequested)
        {
            var remaining = nextDecisionUtc - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            if (remaining <= fastExitInterval)
            {
                await Task.Delay(remaining, cancellationToken);
                return;
            }

            await Task.Delay(fastExitInterval, cancellationToken);
            try
            {
                await RunFastExitCheckAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"futures fast-exit-check FAILED: {ex.Message}");
            }
        }
    }

    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        var utc = _clock.UtcNow;
        var cycleId = $"{config.BotInstance.Id}-{utc:yyyyMMddHHmmss}";
        Console.WriteLine($"futures cycle={cycleId} utc={utc:O}");

        var universeSelection = await ResolveUniverseAsync(cancellationToken);
        var universe = universeSelection.Instruments.Where(instrument => instrument.Enabled).ToList();
        var state = portfolio.Load();
        var lightStates = await marketDataSource.GetLightMarketStatesAsync(universe, cancellationToken);
        PersistMarketSnapshots(cycleId, utc, lightStates);
        _priceHistory.Record(utc, lightStates);
        if (config.Futures.LiveTradingEnabled)
        {
            await ReconcileWithKrakenAsync(state, universe, lightStates, utc, cancellationToken);
            await RefreshDeadManSwitchAsync(cancellationToken);
        }

        var portfolioBefore = state.Clone();

        // Held pairs are always evaluated; new-entry candidates come from the
        // discovered universe capped by MaxActiveInstruments. Strong movers are
        // ranked ahead of pure-volume leaders so fresh futures runners are not
        // hidden behind BTC/ETH-sized books.
        var heldPairs = state.Positions.Select(position => position.Pair).ToHashSet();
        var active = lightStates
            .OrderByDescending(candidate => heldPairs.Contains(candidate.Instrument.Pair))
            .ThenByDescending(IsStrongMoverActiveCandidate)
            .ThenByDescending(candidate => Math.Abs(candidate.ChangePercent))
            .ThenByDescending(candidate => candidate.LastVolume * candidate.LastPrice)
            .Take(Math.Max(config.Trading.MaxActiveInstruments, heldPairs.Count))
            .Select(candidate => candidate.Instrument)
            .ToList();
        var btcInstrument = universe.FirstOrDefault(instrument => instrument.Pair.Equals(config.Regime.BtcPair, StringComparison.OrdinalIgnoreCase));
        if (btcInstrument is not null && active.All(instrument => !instrument.Pair.Equals(btcInstrument.Pair, StringComparison.OrdinalIgnoreCase)))
        {
            active.Add(btcInstrument);
        }

        var fullStates = await marketDataSource.GetFullMarketStatesAsync(active, config.Trading.TimeframeMinutes, lightStates, cancellationToken);
        var btcRegime = EvaluateBtcRegime(fullStates);
        var decisions = new List<DryRunDecisionRecord>();
        var newEntriesThisCycle = 0;

        foreach (var marketState in fullStates.Where(candidate => candidate.IsUsable))
        {
            var pair = marketState.Instrument.Pair;
            var markPrice = marketState.LastPrice;
            var indicators = indicatorEngine.Calculate(marketState.Candles, config.Strategy);
            var priceAction = _priceHistory.Assess(
                pair,
                config.Strategy.PriceActionLookbackSnapshots,
                config.Strategy.PriceActionMinSnapshots,
                utc,
                config.Strategy.PriceActionMaxSampleAgeMinutes);
            var signal = SignalScorer.Evaluate(marketState, indicators, config.Strategy, priceAction);
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
                    fill = await ApplyOrExecuteLiveAsync(
                        state, pair, FuturesDesiredExposure.Flat, markPrice,
                        0m, held.Leverage ?? 1m, reduceOnly: true,
                        reason: $"{trigger.Kind} simulated trigger at {trigger.TriggerPrice:0.####}",
                        exitTriggerSource: trigger.TriggerSource,
                        instrument: marketState.Instrument,
                        entryPlan: null,
                        cancellationToken);
                    fill.Action.ExitReasonCode = trigger.Kind == "STOP_LOSS" ? "SELL_STOP_LOSS" : "SELL_TAKE_PROFIT";
                    riskReasons = new[] { $"hard exit: {trigger.Kind} via {trigger.TriggerSource} price" };
                }
                else if (IsPastMaxHold(held, utc))
                {
                    fill = await ApplyOrExecuteLiveAsync(
                        state, pair, FuturesDesiredExposure.Flat, markPrice,
                        0m, held.Leverage ?? 1m, reduceOnly: true,
                        reason: $"MAX_HOLD forced close after {config.Exits.MaxHoldMinutes}m",
                        exitTriggerSource: config.TpSl.TriggerSource,
                        instrument: marketState.Instrument,
                        entryPlan: null,
                        cancellationToken);
                    fill.Action.ExitReasonCode = "SELL_MAX_HOLD";
                    riskReasons = new[] { $"hard exit: maxHold {config.Exits.MaxHoldMinutes}m elapsed" };
                }
                else
                {
                    var desired = strategy.DecideHeld(held, signal);
                    var minHoldBlocked = desired == FuturesDesiredExposure.Flat && IsMinHoldActive(held, utc);
                    if (minHoldBlocked)
                    {
                        desired = held.Side == "SHORT" ? FuturesDesiredExposure.Short : FuturesDesiredExposure.Long;
                    }
                    fill = await ApplyOrExecuteLiveAsync(
                        state, pair, desired, markPrice,
                        0m, held.Leverage ?? 1m,
                        reduceOnly: desired == FuturesDesiredExposure.Flat,
                        reason: desired == FuturesDesiredExposure.Flat ? "signal reversal close" : minHoldBlocked ? "minimum hold active; reversal ignored" : string.Empty,
                        exitTriggerSource: null,
                        instrument: marketState.Instrument,
                        entryPlan: null,
                        cancellationToken);
                    riskReasons = minHoldBlocked
                        ? new[] { $"minimum hold active: reversal ignored until {config.ExecutionPolicy.MinHoldSeconds}s" }
                        : new[] { "holding existing exposure; TP/SL and reversal rules govern this pair" };
                }
            }
            else
            {
                var desired = strategy.DecideEntry(signal);
                FuturesEntryPlan? entryPlan = null;
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
                    entryPlan = BuildEntryPlan(state, marketState, desired, signal, btcRegime, utc);
                    // Market-quality gate first (spread, anti-lag price action,
                    // warm-up, anti-extension) — the same protections the spot
                    // worker applies before its portfolio and risk layers.
                    var qualityGate = FuturesEntryQualityGate.Evaluate(marketState, indicators, desired, priceAction, config.Strategy);
                    var portfolioGate = qualityGate.Approved
                        ? EvaluatePortfolioEntryGuards(state, pair, desired, utc)
                        : qualityGate;
                    var evaluation = portfolioGate.Approved
                        ? riskManager.EvaluateEntry(BuildRiskInputs(state, marketState, desired, signal, entryPlan, btcRegime))
                        : portfolioGate;
                    riskReasons = evaluation.Reasons;
                    riskApproved = evaluation.Approved;
                    if (!evaluation.Approved)
                    {
                        desired = FuturesDesiredExposure.Flat;
                    }
                    else
                    {
                        fill = await ApplyOrExecuteLiveAsync(
                            state, pair, desired, markPrice,
                            config.Futures.TargetNotionalEur, config.Futures.DefaultLeverage,
                            reduceOnly: false,
                            reason: string.Empty,
                            exitTriggerSource: null,
                            instrument: marketState.Instrument,
                            entryPlan: entryPlan,
                            cancellationToken);
                        if (fill.PositionOpened)
                        {
                            newEntriesThisCycle++;
                        }

                        decisions.Add(BuildDecisionRecord(marketState, indicators, signal, fill, riskApproved, riskReasons, priceAction));
                        continue;
                    }
                }

                fill = await ApplyOrExecuteLiveAsync(
                    state, pair, desired, markPrice,
                    config.Futures.TargetNotionalEur, config.Futures.DefaultLeverage,
                    reduceOnly: false,
                    reason: string.Empty,
                    exitTriggerSource: null,
                    instrument: marketState.Instrument,
                    entryPlan: null,
                    cancellationToken);
                if (entryPlan is not null)
                {
                    AttachEntryPlanDiagnostics(fill.Action, entryPlan);
                }
            }

            decisions.Add(BuildDecisionRecord(marketState, indicators, signal, fill, riskApproved, riskReasons, priceAction));
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
            EntryDiagnostics = BuildEntryDiagnostics(lightStates, active, fullStates, decisions, btcRegime)
        });
        Console.WriteLine($"futures cycle done: decisions={decisions.Count} cash={state.CashEur:0.####} total={state.TotalValueEur:0.####} positions={state.Positions.Count}");
    }

    public async Task RunFastExitCheckAsync(CancellationToken cancellationToken)
    {
        var utc = _clock.UtcNow;
        var state = portfolio.Load();
        if (state.Positions.Count == 0 && !config.Futures.LiveTradingEnabled)
        {
            return;
        }

        var universeSelection = await ResolveUniverseAsync(cancellationToken);
        var universe = universeSelection.Instruments.Where(instrument => instrument.Enabled).ToList();
        if (config.Futures.LiveTradingEnabled)
        {
            await ReconcileWithKrakenAsync(state, universe, Array.Empty<InstrumentMarketState>(), utc, cancellationToken);
            if (state.Positions.Count == 0)
            {
                portfolio.Save(state);
                return;
            }
        }

        var heldPairs = state.Positions.Select(position => position.Pair).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var heldInstruments = universe
            .Where(instrument => heldPairs.Contains(instrument.Pair))
            .ToList();
        if (heldInstruments.Count == 0)
        {
            Console.WriteLine($"futures fast-exit-check: no managed instruments found for {state.Positions.Count} held positions");
            portfolio.Save(state);
            return;
        }

        var lightStates = await marketDataSource.GetLightMarketStatesAsync(heldInstruments, cancellationToken);
        _priceHistory.Record(utc, lightStates);

        var stateByPair = lightStates.ToDictionary(item => item.Instrument.Pair, StringComparer.OrdinalIgnoreCase);
        var portfolioBefore = state.Clone();
        var decisions = new List<DryRunDecisionRecord>();
        var closed = 0;
        foreach (var held in state.Positions.ToList())
        {
            if (!stateByPair.TryGetValue(held.Pair, out var marketState))
            {
                Console.WriteLine($"futures fast-exit-check: no light quote for held pair {held.Pair}; skipping");
                continue;
            }

            var markPrice = FastExitMarkPrice(marketState);
            if (markPrice <= 0m)
            {
                Console.WriteLine($"futures fast-exit-check: invalid mark price for {held.Pair}; skipping");
                continue;
            }

            portfolio.MarkToMarket(state, held.Pair, markPrice);
            var trigger = tpSl.Evaluate(held, markPrice, marketState.Quote?.Last ?? marketState.LastPrice);
            FuturesFillResult? fill = null;
            if (trigger is not null)
            {
                fill = await ApplyOrExecuteLiveAsync(
                    state, held.Pair, FuturesDesiredExposure.Flat, markPrice,
                    0m, held.Leverage ?? 1m, reduceOnly: true,
                    reason: $"{trigger.Kind} fast trigger at {trigger.TriggerPrice:0.####}",
                    exitTriggerSource: trigger.TriggerSource,
                    instrument: marketState.Instrument,
                    entryPlan: null,
                    cancellationToken);
                fill.Action.ExitReasonCode = trigger.Kind == "STOP_LOSS" ? "SELL_STOP_LOSS" : "SELL_TAKE_PROFIT";
            }
            else if (IsPastMaxHold(held, utc))
            {
                fill = await ApplyOrExecuteLiveAsync(
                    state, held.Pair, FuturesDesiredExposure.Flat, markPrice,
                    0m, held.Leverage ?? 1m, reduceOnly: true,
                    reason: $"MAX_HOLD fast close after {config.Exits.MaxHoldMinutes}m",
                    exitTriggerSource: config.TpSl.TriggerSource,
                    instrument: marketState.Instrument,
                    entryPlan: null,
                    cancellationToken);
                fill.Action.ExitReasonCode = "SELL_MAX_HOLD";
            }

            if (fill?.PositionClosed == true)
            {
                closed++;
                decisions.Add(BuildFastExitDecisionRecord(marketState, fill));
                Console.WriteLine($"futures fast-exit-check: closed {held.Pair} reason={fill.Action.ExitReasonCode ?? fill.Action.Reason}");
            }
        }

        portfolio.Save(state);
        if (closed > 0)
        {
            portfolio.Store.AppendCycle(new DryRunCycleRecord
            {
                CycleId = $"{config.BotInstance.Id}-{utc:yyyyMMddHHmmss}-fast-exit",
                BotInstanceId = config.BotInstance.Id,
                BotInstanceName = config.BotInstance.Name,
                Utc = utc,
                MarketDataMode = config.Kraken.MarketDataMode,
                AiProvider = "none",
                Worker = _buildInfo,
                ActivePairs = heldInstruments.Select(instrument => instrument.Pair).ToList(),
                Decisions = decisions,
                PortfolioBefore = portfolioBefore,
                PortfolioAfter = state.Clone(),
                EntryDiagnostics = null
            });
            Console.WriteLine($"futures fast-exit-check: closed={closed} remainingPositions={state.Positions.Count}");
        }
    }

    private static decimal FastExitMarkPrice(InstrumentMarketState marketState) =>
        marketState.Quote?.MarkPrice
        ?? marketState.LastPrice;

    private bool IsStrongMoverActiveCandidate(InstrumentMarketState state)
    {
        var notionalVolume = state.LastVolume * state.LastPrice;
        return Math.Abs(state.ChangePercent) >= config.Trading.StrongMoverMinChangePercent
            && notionalVolume >= config.Trading.StrongMoverMinDailyVolumeEur;
    }

    // Best-effort warm-up hydration from persisted market snapshots, so the
    // anti-lag guard can judge pairs on the very first cycle after a restart.
    // A store failure only skips hydration; it never blocks the worker.
    private void HydratePriceHistory()
    {
        if (config.Strategy.PriceActionHydrationMinutes <= 0)
        {
            return;
        }

        try
        {
            var since = _clock.UtcNow.AddMinutes(-config.Strategy.PriceActionHydrationMinutes);
            var snapshots = portfolio.Store.LoadRecentMarketSnapshots(since);
            var loaded = _priceHistory.Hydrate(snapshots);
            Console.WriteLine(loaded > 0
                ? $"price-action-hydration: loaded {loaded} persisted snapshots from the last {config.Strategy.PriceActionHydrationMinutes} minutes"
                : "price-action-hydration: no recent persisted snapshots; guard warms up normally");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"price-action-hydration: FAILED ({ex.Message}); guard warms up normally");
        }
    }

    private async Task<UniverseSelection> ResolveUniverseAsync(CancellationToken cancellationToken)
    {
        var universe = await _universeProvider.GetUniverseAsync(cancellationToken);
        var diagnostics = universe.Diagnostics;
        Console.WriteLine(
            $"futures universe source={diagnostics.Source} discovered={diagnostics.DiscoveredCount} configured={diagnostics.ConfiguredCount} " +
            $"included={diagnostics.IncludedCount} blacklisted={diagnostics.BlacklistedCount}" +
            (string.IsNullOrWhiteSpace(diagnostics.Warning) ? string.Empty : $" warning={diagnostics.Warning}"));
        return universe;
    }

    private async Task<FuturesFillResult> ApplyOrExecuteLiveAsync(
        PortfolioState state,
        string pair,
        FuturesDesiredExposure desired,
        decimal markPrice,
        decimal targetNotionalEur,
        decimal leverage,
        bool reduceOnly,
        string reason,
        string? exitTriggerSource,
        InstrumentOptions instrument,
        FuturesEntryPlan? entryPlan,
        CancellationToken cancellationToken)
    {
        if (!config.Futures.LiveTradingEnabled)
        {
            return portfolio.Apply(state, pair, desired, markPrice, targetNotionalEur, leverage, reduceOnly, reason, exitTriggerSource, entryPlan);
        }

        var held = state.Positions.FirstOrDefault(position => position.Pair.Equals(pair, StringComparison.OrdinalIgnoreCase));
        if (desired == FuturesDesiredExposure.Flat && held is null)
        {
            return portfolio.Apply(state, pair, desired, markPrice, targetNotionalEur, leverage, reduceOnly, reason, exitTriggerSource, entryPlan);
        }

        if (desired != FuturesDesiredExposure.Flat && held is not null)
        {
            return portfolio.Apply(state, pair, desired, markPrice, targetNotionalEur, leverage, reduceOnly, reason, exitTriggerSource, entryPlan);
        }

        if (broker?.IsConfigured != true)
        {
            var noBroker = portfolio.Apply(state, pair, FuturesDesiredExposure.Flat, markPrice, 0m, leverage, reason: "live futures broker unavailable; order skipped");
            noBroker.Action.HoldReasonCode = "LIVE_BROKER_UNAVAILABLE";
            return noBroker;
        }

        if (desired == FuturesDesiredExposure.Flat && held is not null)
        {
            var closeSide = held.Side.Equals("SHORT", StringComparison.OrdinalIgnoreCase) ? "buy" : "sell";
            var close = await broker.SendOrderAsync(instrument.KrakenPair, closeSide, held.Quantity, reduceOnly: true, held.Leverage ?? leverage, cancellationToken);
            if (!close.Accepted)
            {
                var holdDesired = held.Side.Equals("SHORT", StringComparison.OrdinalIgnoreCase)
                    ? FuturesDesiredExposure.Short
                    : FuturesDesiredExposure.Long;
                var rejected = portfolio.Apply(state, pair, holdDesired, markPrice, 0m, held.Leverage ?? leverage, reason: $"live reduce-only close rejected: {close.Error ?? close.Status}");
                rejected.Action.HoldReasonCode = "LIVE_ORDER_REJECTED";
                rejected.Action.FillSource = "REAL_REJECTED";
                return rejected;
            }

            var fill = portfolio.Apply(state, pair, desired, markPrice, targetNotionalEur, leverage, reduceOnly: true, reason, exitTriggerSource, entryPlan);
            fill.Action.FillSource = "REAL";
            fill.Action.Reason = $"live Kraken Futures order accepted id={close.OrderId ?? "-"} status={close.Status}; {fill.Action.Reason}";
            return fill;
        }

        // Live market orders fill in full and immediately, so they are sized from
        // the REAL target notional — never from entryPlan.FilledNotionalEur, which
        // is the dry-run maker-fill simulation (it models partial passive fills,
        // e.g. exactly half, that do not apply to a taker `mkt` order).
        var rawSize = markPrice <= 0m ? 0m : targetNotionalEur / markPrice;
        var quantityDecimals = instrument.QuantityDecimals ?? 8;
        var size = TruncateToDecimals(rawSize, quantityDecimals);
        if (size <= 0m)
        {
            var skipReason = $"live futures entry skipped: raw size {rawSize:0.########} rounds to zero at Kraken quantity precision {quantityDecimals}";
            var skipped = portfolio.Apply(state, pair, FuturesDesiredExposure.Flat, markPrice, 0m, leverage, reason: skipReason);
            skipped.Action.HoldReasonCode = "LIVE_ORDER_SIZE_TOO_SMALL";
            return skipped;
        }

        var adjustedNotional = size * markPrice;
        var adjustedPlan = entryPlan is null
            ? null
            : entryPlan with { FilledNotionalEur = adjustedNotional };
        var side = desired == FuturesDesiredExposure.Short ? "sell" : "buy";

        // Kraken Futures leverage is a per-symbol margin preference, not an order
        // field. Set it (clamped to MaxLeverage) BEFORE the entry and refuse to
        // open if it cannot be set — otherwise the position inherits the exchange
        // default (10x+) and posts a fraction of the intended margin.
        var entryLeverage = Math.Clamp(leverage, 1m, config.Futures.MaxLeverage);
        if (!await broker.SetLeveragePreferenceAsync(instrument.KrakenPair, entryLeverage, cancellationToken))
        {
            var leverageReason = $"live futures entry skipped: could not set {entryLeverage:0.#}x leverage preference for {instrument.KrakenPair}; refusing to open at exchange-default leverage";
            Console.WriteLine($"futures-live-order-skipped: pair={pair} krakenPair={instrument.KrakenPair} reason={leverageReason}");
            var skipped = portfolio.Apply(state, pair, FuturesDesiredExposure.Flat, markPrice, 0m, entryLeverage, reason: leverageReason);
            skipped.Action.HoldReasonCode = "LIVE_LEVERAGE_SET_FAILED";
            return skipped;
        }

        var order = await broker.SendOrderAsync(instrument.KrakenPair, side, size, reduceOnly: false, entryLeverage, cancellationToken);
        if (!order.Accepted)
        {
            var rejectReason = $"live futures entry rejected: {order.Error ?? order.Status}";
            Console.WriteLine($"futures-live-order-rejected: pair={pair} krakenPair={instrument.KrakenPair} side={side} rawSize={rawSize:0.########} size={size:0.########} quantityDecimals={quantityDecimals} leverage={entryLeverage:0.#}x reason={rejectReason}");
            var rejected = portfolio.Apply(state, pair, FuturesDesiredExposure.Flat, markPrice, 0m, entryLeverage, reason: rejectReason);
            rejected.Action.HoldReasonCode = "LIVE_ORDER_REJECTED";
            rejected.Action.FillSource = "REAL_REJECTED";
            return rejected;
        }

        // Record the virtual ledger against the ACTUAL filled notional and the
        // leverage we set on the exchange, so virtual state mirrors the real fill.
        var opened = portfolio.Apply(state, pair, desired, markPrice, adjustedNotional, entryLeverage, reduceOnly: false, reason, exitTriggerSource, adjustedPlan);
        opened.Action.FillSource = "REAL";
        opened.Action.Reason = $"live Kraken Futures order accepted id={order.OrderId ?? "-"} status={order.Status}; {opened.Action.Reason}";
        return opened;
    }

    private static decimal TruncateToDecimals(decimal value, int decimals)
    {
        decimals = Math.Clamp(decimals, 0, 8);
        var factor = 1m;
        for (var i = 0; i < decimals; i++)
        {
            factor *= 10m;
        }

        return Math.Truncate(value * factor) / factor;
    }

    private async Task RefreshDeadManSwitchAsync(CancellationToken cancellationToken)
    {
        if (!config.Futures.LiveTradingEnabled || broker?.IsConfigured != true)
        {
            return;
        }

        await broker.CancelAllAfterAsync(config.Futures.DeadManSwitchSeconds, cancellationToken);
        Console.WriteLine($"futures dead-man-switch: refreshed timeout={config.Futures.DeadManSwitchSeconds}s");
    }

    private async Task ReconcileWithKrakenAsync(
        PortfolioState state,
        IReadOnlyList<InstrumentOptions> universe,
        IReadOnlyList<InstrumentMarketState> lightStates,
        DateTimeOffset utc,
        CancellationToken cancellationToken)
    {
        if (broker?.IsConfigured != true)
        {
            throw new InvalidOperationException("futures live reconciliation requested but broker is not configured.");
        }

        var accounts = await broker.GetAccountsAsync(cancellationToken);
        var positions = await broker.GetOpenPositionsAsync(cancellationToken);
        var bySymbol = universe
            .Where(instrument => !string.IsNullOrWhiteSpace(instrument.KrakenPair))
            .ToDictionary(instrument => instrument.KrakenPair, StringComparer.OrdinalIgnoreCase);
        var markByPair = lightStates.ToDictionary(state => state.Instrument.Pair, state => state.LastPrice, StringComparer.OrdinalIgnoreCase);

        var available = accounts.Sum(account => account.AvailableMargin);
        if (available > 0m)
        {
            state.CashEur = available;
        }

        var imported = new List<PortfolioPosition>();
        foreach (var remote in positions)
        {
            if (!bySymbol.TryGetValue(remote.Symbol, out var instrument))
            {
                Console.WriteLine($"futures-kraken-sync: ignoring unmanaged symbol {remote.Symbol} size={remote.Size}");
                continue;
            }

            var mark = remote.MarkPrice > 0m
                ? remote.MarkPrice
                : markByPair.GetValueOrDefault(instrument.Pair, remote.EntryPrice);
            var leverage = Math.Clamp(remote.Leverage <= 0m ? config.Futures.DefaultLeverage : remote.Leverage, 1m, config.Futures.MaxLeverage);
            var notional = remote.EntryPrice * remote.Size;
            var initialMargin = leverage <= 0m ? notional : notional / leverage;
            var pnl = FuturesMath.UnrealizedPnlEur(remote.Side, remote.EntryPrice, mark, remote.Size);
            var existing = state.Positions.FirstOrDefault(position => position.Pair.Equals(instrument.Pair, StringComparison.OrdinalIgnoreCase));
            imported.Add(new PortfolioPosition
            {
                Pair = instrument.Pair,
                Side = remote.Side,
                Quantity = remote.Size,
                EntryPrice = remote.EntryPrice,
                EntryNotionalEur = notional,
                LastPrice = mark,
                MarkPrice = mark,
                MarketValueEur = initialMargin + pnl,
                UnrealizedPnlEur = pnl,
                UnrealizedPnlPercent = notional <= 0m ? 0m : pnl / notional * 100m,
                OpenedAtUtc = existing?.OpenedAtUtc ?? utc,
                LastActionAtUtc = existing?.LastActionAtUtc ?? utc,
                Leverage = leverage,
                InitialMarginEur = initialMargin,
                LiquidationPrice = FuturesMath.EstimateLiquidationPrice(remote.Side, remote.EntryPrice, leverage, config.Margin.MaintenanceMarginRatePercent),
                LiquidationDistancePercent = FuturesMath.LiquidationDistancePercent(mark, FuturesMath.EstimateLiquidationPrice(remote.Side, remote.EntryPrice, leverage, config.Margin.MaintenanceMarginRatePercent)),
                TpOrderState = existing?.TpOrderState,
                SlOrderState = existing?.SlOrderState,
                StopLossPrice = existing?.StopLossPrice,
                TakeProfitPrice = existing?.TakeProfitPrice,
                EntryAtr = existing?.EntryAtr,
                RoundTripCostEstimatePct = existing?.RoundTripCostEstimatePct,
                ExpectedFundingPct = existing?.ExpectedFundingPct,
                AtrPct = existing?.AtrPct,
                StopDistancePct = existing?.StopDistancePct,
                TakeProfitDistancePct = existing?.TakeProfitDistancePct
            });
        }

        var before = state.Positions.Count;
        state.Positions = imported;
        state.UpdatedAt = utc;
        Console.WriteLine($"futures-kraken-sync: accounts={accounts.Count} remotePositions={positions.Count} trackedPositions={state.Positions.Count} previousTracked={before} availableMargin={state.CashEur:0.####}");
    }

    private bool IsLiveInstance =>
        config.BotInstance.Id.Equals("live", StringComparison.OrdinalIgnoreCase)
        || config.BotInstance.Id.EndsWith("-live", StringComparison.OrdinalIgnoreCase);

    private bool IsPastMaxHold(PortfolioPosition position, DateTimeOffset utc) =>
        config.Exits.MaxHoldMinutes > 0
        && position.OpenedAtUtc is { } opened
        && utc - opened >= TimeSpan.FromMinutes(config.Exits.MaxHoldMinutes);

    private bool IsMinHoldActive(PortfolioPosition position, DateTimeOffset utc) =>
        config.ExecutionPolicy.MinHoldSeconds > 0
        && position.OpenedAtUtc is { } opened
        && utc - opened < TimeSpan.FromSeconds(config.ExecutionPolicy.MinHoldSeconds);

    private RiskEvaluation EvaluatePortfolioEntryGuards(
        PortfolioState state,
        string pair,
        FuturesDesiredExposure desired,
        DateTimeOffset utc)
    {
        if (desired == FuturesDesiredExposure.Flat)
        {
            return new RiskEvaluation(true, new[] { "no exposure requested" });
        }

        if (IsInEntryBlackout(utc))
        {
            return new RiskEvaluation(false, new[] { $"entry blackout active: {config.ExecutionPolicy.EntryBlackoutMinutes}m after {config.ExecutionPolicy.EntryBlackoutUtcFromHour:00}:00 UTC" });
        }

        var history = state.ActionHistory.FirstOrDefault(item => item.Pair.Equals(pair, StringComparison.OrdinalIgnoreCase));
        if (history?.LastSellAtUtc is { } lastSell)
        {
            var elapsed = (utc - lastSell).TotalSeconds;
            if (elapsed < config.ExecutionPolicy.CooldownAfterCloseSeconds)
            {
                return new RiskEvaluation(false, new[] { $"cooldown after close active: elapsed {elapsed:0}s below {config.ExecutionPolicy.CooldownAfterCloseSeconds}s" });
            }
        }

        if (history?.LastStopLossAtUtc is { } lastStop)
        {
            var elapsed = (utc - lastStop).TotalSeconds;
            if (elapsed < config.ExecutionPolicy.CooldownAfterStopLossSeconds)
            {
                return new RiskEvaluation(false, new[] { $"stop-loss cooldown active: elapsed {elapsed:0}s below {config.ExecutionPolicy.CooldownAfterStopLossSeconds}s" });
            }
        }

        var group = CorrelationRiskResolver.ResolveGroup(_pairToCorrelationGroup, pair);
        var groupPositions = state.Positions
            .Where(position => CorrelationRiskResolver.ResolveGroup(_pairToCorrelationGroup, position.Pair)
                .Equals(group, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (config.CorrelationRisk.MaxOpenPositionsPerGroup > 0
            && groupPositions.Count >= config.CorrelationRisk.MaxOpenPositionsPerGroup)
        {
            return new RiskEvaluation(false, new[] { $"correlation group {group} position cap {config.CorrelationRisk.MaxOpenPositionsPerGroup} reached" });
        }

        var groupExposure = groupPositions.Sum(position => position.EntryNotionalEur);
        if (config.CorrelationRisk.MaxExposureEurPerGroup > 0m
            && groupExposure + config.Futures.TargetNotionalEur > config.CorrelationRisk.MaxExposureEurPerGroup)
        {
            return new RiskEvaluation(false, new[] { $"correlation group {group} exposure EUR {groupExposure + config.Futures.TargetNotionalEur:0.####} exceeds cap EUR {config.CorrelationRisk.MaxExposureEurPerGroup:0.####}" });
        }

        return new RiskEvaluation(true, new[] { $"portfolio entry guards passed for group {group}" });
    }

    private bool IsInEntryBlackout(DateTimeOffset utc)
    {
        if (config.ExecutionPolicy.EntryBlackoutMinutes <= 0)
        {
            return false;
        }

        var date = utc.UtcDateTime.Date;
        var windowStart = new DateTimeOffset(date.AddHours(config.ExecutionPolicy.EntryBlackoutUtcFromHour), TimeSpan.Zero);
        var minutesSinceStart = (utc - windowStart).TotalMinutes;
        return minutesSinceStart >= 0 && minutesSinceStart < config.ExecutionPolicy.EntryBlackoutMinutes;
    }

    private FuturesEntryPlan BuildEntryPlan(
        PortfolioState state,
        InstrumentMarketState marketState,
        FuturesDesiredExposure desired,
        TechnicalSignal signal,
        BtcRegimeState btcRegime,
        DateTimeOffset utc)
    {
        var markPrice = marketState.Quote?.MarkPrice ?? marketState.LastPrice;
        var atr = AtrIndicator.CalculateLatestClosedAtr(marketState.Candles, 14);
        var atrPct = atr is > 0m && markPrice > 0m ? atr.Value / markPrice * 100m : 0m;
        var stopDistancePct = Math.Max(config.Exits.StopAtrMult, config.Exits.MinStopAtrFloor) * atrPct;
        var expectedFundingPct = ExpectedFundingPct(desired, marketState.Quote?.FundingRatePercent);
        var roundTripCost = 2m * config.Fees.TakerPct + config.Exits.SlippageBufferPct + expectedFundingPct;
        var takeProfitDistancePct = Math.Max(config.Exits.TakeProfitAtrMult * atrPct, config.Exits.MinTpVsCostMult * roundTripCost);
        var queueAhead = QueueAheadEur(marketState, desired);
        var makerFilled = SimulatedMakerFillEur(queueAhead, config.Futures.TargetNotionalEur);
        var openRisk = ProjectedOpenRiskEur(state, desired, markPrice, makerFilled, stopDistancePct, roundTripCost);
        var shortGate = EvaluateShortGate(desired, signal, btcRegime);

        return new FuturesEntryPlan(
            RequestedNotionalEur: config.Futures.TargetNotionalEur,
            FilledNotionalEur: makerFilled,
            AtrPct: decimal.Round(atrPct, 6),
            StopDistancePct: decimal.Round(stopDistancePct, 6),
            TakeProfitDistancePct: decimal.Round(takeProfitDistancePct, 6),
            RoundTripCostEstimatePct: decimal.Round(roundTripCost, 6),
            ExpectedFundingPct: decimal.Round(expectedFundingPct, 6),
            QueueAheadEur: decimal.Round(queueAhead, 6),
            MakerFillRate: config.Futures.TargetNotionalEur <= 0m ? 0m : decimal.Round(makerFilled / config.Futures.TargetNotionalEur, 6),
            TimeToFillMs: makerFilled > 0m ? Math.Min(config.Entry.MakerFillTimeoutSec * 1000L, 1000L) : config.Entry.MakerFillTimeoutSec * 1000L,
            RepegCount: makerFilled > 0m && config.Entry.MakerRepegs > 0 ? 1 : 0,
            OpenRiskEur: openRisk,
            FundingState: FundingState(marketState.Quote?.FundingRatePercent, desired),
            BtcRegimeState: btcRegime.Description,
            ShortAllowed: shortGate.Allowed ? "yes" : $"no: {shortGate.Reason}");
    }

    private FuturesEntryRiskInputs BuildRiskInputs(
        PortfolioState state,
        InstrumentMarketState marketState,
        FuturesDesiredExposure desired,
        TechnicalSignal signal,
        FuturesEntryPlan plan,
        BtcRegimeState btcRegime)
    {
        var shortGate = EvaluateShortGate(desired, signal, btcRegime);
        return new FuturesEntryRiskInputs(
            state,
            desired,
            marketState.Quote?.MarkPrice ?? marketState.LastPrice,
            config.Futures.TargetNotionalEur,
            plan.FilledNotionalEur,
            config.Futures.DefaultLeverage,
            portfolio.UsedMarginEur(state),
            marketState.Quote?.FundingRatePercent,
            plan.AtrPct > 0m ? plan.AtrPct : null,
            plan.StopDistancePct > 0m ? plan.StopDistancePct : null,
            plan.TakeProfitDistancePct > 0m ? plan.TakeProfitDistancePct : null,
            marketState.Quote?.VolumeToday,
            ExitDepthEur(marketState, desired),
            plan.OpenRiskEur,
            btcRegime.AllowsLongs,
            btcRegime.Description,
            shortGate.Allowed,
            shortGate.Reason);
    }

    private static void AttachEntryPlanDiagnostics(DryRunAction action, FuturesEntryPlan plan)
    {
        action.RequestedNotionalEur = plan.RequestedNotionalEur;
        action.FilledNotionalEur = plan.FilledNotionalEur;
        action.RoundTripCostEstimatePct = plan.RoundTripCostEstimatePct;
        action.ExpectedFundingPct = plan.ExpectedFundingPct;
        action.AtrPct = plan.AtrPct;
        action.StopDistancePct = plan.StopDistancePct;
        action.TakeProfitDistancePct = plan.TakeProfitDistancePct;
        action.OpenRiskEur = plan.OpenRiskEur;
        action.QueueAheadEur = plan.QueueAheadEur;
        action.MakerOrderFilledEur = plan.FilledNotionalEur;
        action.MakerFillRate = plan.MakerFillRate;
        action.TimeToFillMs = plan.TimeToFillMs;
        action.RepegCount = plan.RepegCount;
        action.FundingState = plan.FundingState;
        action.BtcRegimeState = plan.BtcRegimeState;
        action.ShortAllowed = plan.ShortAllowed;
    }

    private decimal ExpectedFundingPct(FuturesDesiredExposure desired, decimal? fundingRatePercent)
    {
        if (fundingRatePercent is null)
        {
            return 0m;
        }

        var adverse = desired switch
        {
            FuturesDesiredExposure.Long => Math.Max(0m, fundingRatePercent.Value),
            FuturesDesiredExposure.Short => Math.Max(0m, -fundingRatePercent.Value),
            _ => 0m
        };
        var periods = Math.Max(1, (int)Math.Ceiling(config.Exits.MaxHoldMinutes / 240m));
        return adverse * periods;
    }

    private decimal QueueAheadEur(InstrumentMarketState marketState, FuturesDesiredExposure desired)
    {
        var quote = marketState.Quote;
        if (quote is null)
        {
            return decimal.MaxValue;
        }

        return desired == FuturesDesiredExposure.Short
            ? (quote.AskSize ?? 0m) * quote.Ask
            : (quote.BidSize ?? 0m) * quote.Bid;
    }

    private decimal SimulatedMakerFillEur(decimal queueAheadEur, decimal requestedNotionalEur)
    {
        if (queueAheadEur < 0m || queueAheadEur == decimal.MaxValue)
        {
            return 0m;
        }

        var fullFillQueueCap = requestedNotionalEur * config.Entry.MaxQueueAheadMultiple;
        if (queueAheadEur <= fullFillQueueCap)
        {
            return requestedNotionalEur;
        }

        var partialFillQueueCap = fullFillQueueCap * Math.Max(1, config.Entry.MakerRepegs + 1);
        return queueAheadEur <= partialFillQueueCap
            ? decimal.Round(requestedNotionalEur / 2m, 8)
            : 0m;
    }

    private decimal ProjectedOpenRiskEur(
        PortfolioState state,
        FuturesDesiredExposure desired,
        decimal markPrice,
        decimal filledNotionalEur,
        decimal stopDistancePct,
        decimal roundTripCostPct)
    {
        var current = state.Positions.Sum(position => PositionRiskEur(position, markPrice));
        if (filledNotionalEur <= 0m || stopDistancePct <= 0m)
        {
            return current;
        }

        var newRisk = filledNotionalEur * stopDistancePct / 100m
            + filledNotionalEur * (roundTripCostPct + config.Risk.EstimatedEmergencyExitCostPct) / 100m;
        return decimal.Round(current + Math.Max(0m, newRisk), 8);
    }

    private decimal PositionRiskEur(PortfolioPosition position, decimal markPrice)
    {
        if (position.StopLossPrice is null or <= 0m || position.EntryPrice <= 0m || position.Quantity <= 0m)
        {
            return config.Risk.MaxConcurrentOpenRisk + 1m;
        }

        var risk = position.Side == "SHORT"
            ? Math.Max(0m, (position.StopLossPrice.Value - markPrice) * position.Quantity)
            : Math.Max(0m, (markPrice - position.StopLossPrice.Value) * position.Quantity);
        var emergency = position.EntryNotionalEur * (config.Fees.TakerPct + config.Risk.EstimatedEmergencyExitCostPct) / 100m;
        return risk + emergency;
    }

    private decimal? ExitDepthEur(InstrumentMarketState marketState, FuturesDesiredExposure desired)
    {
        if (marketState.OrderBook is null)
        {
            return null;
        }

        var quote = marketState.Quote;
        if (quote is null || quote.Bid <= 0m || quote.Ask <= 0m)
        {
            return null;
        }

        var levels = desired == FuturesDesiredExposure.Short
            ? marketState.OrderBook.Bids
            : marketState.OrderBook.Asks;
        var reference = desired == FuturesDesiredExposure.Short ? quote.Bid : quote.Ask;
        var maxImpact = config.Filters.MaxExitImpactPct / 100m;
        var depth = levels
            .Where(level => desired == FuturesDesiredExposure.Short
                ? level.Price >= reference * (1m - maxImpact)
                : level.Price <= reference * (1m + maxImpact))
            .Sum(level => level.Price * level.Volume);
        return decimal.Round(depth, 8);
    }

    private BtcRegimeState EvaluateBtcRegime(IReadOnlyList<InstrumentMarketState> states)
    {
        var btc = states.FirstOrDefault(state => state.Instrument.Pair.Equals(config.Regime.BtcPair, StringComparison.OrdinalIgnoreCase));
        if (btc is null || btc.Candles.Count < config.Regime.BtcTrendMa + config.Regime.BtcSlopeLookback + 1)
        {
            return new BtcRegimeState(false, false, "BTC regime unavailable/stale");
        }

        var closes = btc.Candles.Select(candle => candle.Close).ToList();
        var close = closes[^1];
        var ma = closes.TakeLast(config.Regime.BtcTrendMa).Average();
        var priorMa = closes.Take(closes.Count - config.Regime.BtcSlopeLookback)
            .TakeLast(config.Regime.BtcTrendMa)
            .DefaultIfEmpty(0m)
            .Average();
        var slope = ma - priorMa;
        var lookbackIndex = Math.Max(0, closes.Count - 1 - config.Regime.BtcCrashLookback);
        var drawdown = closes[lookbackIndex] <= 0m ? 0m : (close - closes[lookbackIndex]) / closes[lookbackIndex] * 100m;
        var belowMa = close < ma;
        var downSlope = slope < 0m;
        var crash = drawdown <= -config.Regime.BtcCrashPct;
        var allowsLongs = !belowMa && !crash;
        var allowsShortRegime = belowMa && downSlope && drawdown > -config.Shorts.MaxChaseDrawdownPct;
        return new BtcRegimeState(
            allowsLongs,
            allowsShortRegime,
            $"close={close:0.####} ma{config.Regime.BtcTrendMa}={ma:0.####} slope={slope:0.####} drawdown{config.Regime.BtcCrashLookback}={drawdown:0.###}% allowsLongs={allowsLongs} allowsShorts={allowsShortRegime}");
    }

    private (bool Allowed, string? Reason) EvaluateShortGate(FuturesDesiredExposure desired, TechnicalSignal signal, BtcRegimeState btcRegime)
    {
        if (desired != FuturesDesiredExposure.Short)
        {
            return (true, null);
        }

        if (!btcRegime.AllowsShorts)
        {
            return (false, btcRegime.Description);
        }

        if (!signal.HasBearishStructure || !signal.AllowsShort)
        {
            return (false, "pair bearish signal not confirmed");
        }

        if (signal.Score < config.Shorts.MinShortScore)
        {
            return (false, $"short score {signal.Score:0.##} below {config.Shorts.MinShortScore:0.##}");
        }

        return (true, "short gates passed");
    }

    private string FundingState(decimal? fundingRatePercent, FuturesDesiredExposure desired)
    {
        if (fundingRatePercent is null)
        {
            return "missing";
        }

        var adverse = desired == FuturesDesiredExposure.Long
            ? fundingRatePercent > config.Funding.MaxAbsFundingRatePercentForEntry
            : desired == FuturesDesiredExposure.Short && fundingRatePercent < -config.Funding.MaxAbsFundingRatePercentForEntry;
        return $"apiField=fundingRate value={fundingRatePercent:0.######}% semantic={(fundingRatePercent >= 0m ? "positive longs pay shorts" : "negative shorts pay longs")} adverse={adverse}";
    }

    private static DryRunDecisionRecord BuildDecisionRecord(
        InstrumentMarketState marketState,
        IndicatorSnapshot indicators,
        TechnicalSignal signal,
        FuturesFillResult fill,
        bool riskApproved,
        IReadOnlyList<string> riskReasons,
        PriceActionAssessment? priceAction = null) => new()
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
        EmaGapVelocityPercent = signal.EmaGapVelocityPercent,
        PriceActionDirection = priceAction?.Direction,
        PriceActionTrendPercent = priceAction?.TrendPercent
    };

    private static DryRunDecisionRecord BuildFastExitDecisionRecord(
        InstrumentMarketState marketState,
        FuturesFillResult fill) => new()
    {
        Pair = marketState.Instrument.Pair,
        Price = marketState.LastPrice,
        FastEma = null,
        SlowEma = null,
        Rsi = null,
        DesiredPosition = "FLAT",
        Score = 0m,
        RiskApproved = true,
        RiskReasons = new[] { "fast held-position exit check" },
        Contributions = Array.Empty<SignalContribution>(),
        DryRunAction = fill.Action,
        EntryRejectionReason = null,
        SpreadPercent = SpreadPercentOf(marketState),
        HasBullishStructure = false,
        EmaFullyConfirmed = false,
        BullishEmaGapPercent = null,
        EmaGapVelocityPercent = null,
        PriceActionDirection = null,
        PriceActionTrendPercent = null
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
        IReadOnlyList<DryRunDecisionRecord> decisions,
        BtcRegimeState btcRegime)
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
            PriceActionReadyCount: lightStates.Count(state =>
                _priceHistory.Assess(
                    state.Instrument.Pair,
                    config.Strategy.PriceActionLookbackSnapshots,
                    config.Strategy.PriceActionMinSnapshots,
                    _clock.UtcNow,
                    config.Strategy.PriceActionMaxSampleAgeMinutes) is { DataSufficient: true }),
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
            ExcludedPairs: excludedPairs,
            ExecutionMode: "dry-run-maker-post-only",
            FillRate: entryDecisions.Count == 0
                ? 0m
                : decimal.Round(entryDecisions.Count(decision => (decision.DryRunAction.MakerFillRate ?? 0m) > 0m) / (decimal)entryDecisions.Count, 4),
            PairsPassedVolume: fullStates.Count(state => (state.Quote?.VolumeToday ?? 0m) >= config.Filters.MinQuoteVolume24h),
            PairsPassedDepth: fullStates.Count(state => ExitDepthEur(state, FuturesDesiredExposure.Long) >= config.Futures.TargetNotionalEur * config.Filters.MinExitDepthMultiple),
            OpenRiskEur: stateOpenRisk(decisions),
            BtcRegimeState: btcRegime.Description,
            PairsPassedExitDepth: fullStates.Count(state => ExitDepthEur(state, FuturesDesiredExposure.Long) >= config.Futures.TargetNotionalEur * config.Filters.MinExitDepthMultiple),
            FundingState: string.Join("; ", decisions.Select(decision => decision.DryRunAction.FundingState).Where(value => !string.IsNullOrWhiteSpace(value)).Take(3)));
    }

    private static decimal stateOpenRisk(IReadOnlyList<DryRunDecisionRecord> decisions) =>
        decisions.Select(decision => decision.DryRunAction.OpenRiskEur).Where(value => value.HasValue).DefaultIfEmpty(0m).Max() ?? 0m;

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
