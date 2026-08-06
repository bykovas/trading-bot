using System.Text.Json;

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
    private static readonly HttpClient FxHttpClient = new();

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

        // Held pairs are always evaluated; new-entry candidates first use the
        // normal MaxActiveInstruments ranking, then missing force-included pairs
        // are appended so core markets do not crowd out fresh movers.
        var heldPairs = state.Positions.Select(position => position.Pair).ToHashSet();
        var active = SelectActiveInstruments(
            lightStates,
            heldPairs,
            config.UniverseDiscovery.ForceInclude,
            config.Trading).ToList();
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
                var trigger = tpSl.Evaluate(held, markPrice, marketState.LastPrice, marketState.Quote?.Bid, marketState.Quote?.Ask);
                if (trigger is not null)
                {
                    fill = await HandleTpSlTriggerAsync(state, held, trigger, markPrice, marketState.Instrument, cancellationToken, fast: false);
                    riskReasons = new[] { $"hard exit: {trigger.Kind} via {trigger.TriggerSource} price" };
                }
                else if (!IsExternalFuturesPosition(held)
                    && EvaluateMaxHoldExit(held, utc, config.Exits.MaxHoldMinutes, config.Exits.MaxHoldMinStopProgressPct) is { ShouldClose: true } maxHold)
                {
                    fill = await ApplyOrExecuteLiveAsync(
                        state, pair, FuturesDesiredExposure.Flat, markPrice,
                        0m, held.Leverage ?? 1m, reduceOnly: true,
                        reason: maxHold.Reason ?? $"MAX_HOLD forced close after {config.Exits.MaxHoldMinutes}m",
                        exitTriggerSource: config.TpSl.TriggerSource,
                        instrument: marketState.Instrument,
                        entryPlan: null,
                        cancellationToken);
                    fill.Action.ExitReasonCode = "SELL_MAX_HOLD";
                    riskReasons = new[] { maxHold.Reason ?? $"hard exit: maxHold {config.Exits.MaxHoldMinutes}m elapsed" };
                }
                else
                {
                    var desired = strategy.DecideHeld(held, signal);
                    var minHoldBlocked = desired == FuturesDesiredExposure.Flat && IsMinHoldActive(held, utc);
                    var externalSoftExitBlocked = desired == FuturesDesiredExposure.Flat && IsExternalFuturesPosition(held);
                    if (minHoldBlocked)
                    {
                        desired = held.Side == "SHORT" ? FuturesDesiredExposure.Short : FuturesDesiredExposure.Long;
                    }
                    else if (externalSoftExitBlocked)
                    {
                        desired = held.Side == "SHORT" ? FuturesDesiredExposure.Short : FuturesDesiredExposure.Long;
                    }
                    fill = await ApplyOrExecuteLiveAsync(
                        state, pair, desired, markPrice,
                        0m, held.Leverage ?? 1m,
                        reduceOnly: desired == FuturesDesiredExposure.Flat,
                        reason: desired == FuturesDesiredExposure.Flat
                            ? "signal reversal close"
                            : externalSoftExitBlocked
                                ? "external/adopted Kraken Futures position: signal reversal ignored; exchange TP/SL or manual close only"
                                : minHoldBlocked
                                    ? "minimum hold active; reversal ignored"
                                    : string.Empty,
                        exitTriggerSource: null,
                        instrument: marketState.Instrument,
                        entryPlan: null,
                        cancellationToken);
                    if (externalSoftExitBlocked)
                    {
                        fill.Action.HoldReasonCode = "EXTERNAL_SIGNAL_FLIP_BLOCK";
                    }
                    riskReasons = externalSoftExitBlocked
                        ? new[] { "external/adopted Kraken Futures position: signal reversal ignored; exchange TP/SL or manual close only" }
                        : minHoldBlocked
                        ? new[] { $"minimum hold active: reversal ignored until {config.ExecutionPolicy.MinHoldSeconds}s" }
                        : new[] { "holding existing exposure; TP/SL and reversal rules govern this pair" };
                }
            }
            else
            {
                var desired = strategy.DecideEntry(signal);
                FuturesEntryPlan? entryPlan = null;
                EntryFreshnessResult? freshness = null;
                LongRangeResult? longRange = null;
                ShortEntryResult? shortEntry = null;
                var dipBounce = false;
                var remainingSlots = Math.Max(0, config.Futures.MaxPositions - state.Positions.Count);

                // Dip-bounce channel: a LONG candidate whose score sits just below the
                // firm bar is still admitted when price is near its 24h low AND a
                // confirmed bounce is visible — a fresh upward snapshot tape plus
                // non-negative 15m candle momentum (the same freshness the continuation
                // channel demands). This buys support-reclaim setups without catching a
                // falling knife: without the fresh tape+momentum the candidate stays
                // flat, and it still runs the full quality / freshness / risk gauntlet.
                if (desired == FuturesDesiredExposure.Flat
                    && config.Dip.Enabled
                    && signal.AllowsLong
                    && signal.Score >= config.Dip.MinScore
                    && signal.Score < config.Strategy.MinimumLongScore
                    && newEntriesThisCycle < remainingSlots)
                {
                    var dipFreshness = FuturesEntryFreshnessGuard.Evaluate(
                        marketState,
                        _priceHistory.RecentObservations(pair, config.Freshness.FreshTapeSnapshotCount),
                        FuturesDesiredExposure.Long,
                        config.Freshness);
                    // A lower-score entry demands a real up-tick, not merely a
                    // non-falling candle: require candle momentum >= Dip.MinCandleMomentumPct
                    // (a small POSITIVE floor) on top of the fresh tape. Momentum that
                    // cannot be computed (null) does not qualify — no blind dip entry.
                    var dipMomentumOk = dipFreshness.RecentCandleMomentumPct is { } dipMomentum
                        && dipMomentum >= config.Dip.MinCandleMomentumPct;
                    var dipEntryPrice = marketState.Quote?.Ask is > 0m
                        ? marketState.Quote.Ask
                        : marketState.LastPrice;
                    var dipClosePct = FuturesLongRangeGuard.ClosePercentileRank(
                        marketState.Candles, dipEntryPrice, lookback: 96);
                    // Dip zone uses close-percentile (value distribution), not wick 24h
                    // high-low, so a reclaim after spikes is not forced into the wick floor.
                    if (!dipFreshness.Blocked
                        && dipFreshness.HasFreshUpwardTape
                        && dipMomentumOk
                        && dipClosePct is { } dipPos
                        && dipPos <= config.Dip.NearLowMax24hRangePositionPct)
                    {
                        desired = FuturesDesiredExposure.Long;
                        dipBounce = true;
                        Console.WriteLine(
                            $"DIP_BOUNCE_ENTRY pair={pair} score={signal.Score:0.##} minScore={config.Dip.MinScore:0.##} firmBar={config.Strategy.MinimumLongScore:0.##} closePct={dipPos:0.###} nearLowMax={config.Dip.NearLowMax24hRangePositionPct:0.###} freshTape={dipFreshness.HasFreshUpwardTape} candleMom={dipFreshness.RecentCandleMomentumPct:0.###} minCandleMom={config.Dip.MinCandleMomentumPct:0.###} slope={dipFreshness.ShortSnapshotSlopePct:0.###} lastStep={dipFreshness.LastSnapshotStepPct:0.###}");
                    }
                }

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
                    freshness = qualityGate.Approved
                        ? FuturesEntryFreshnessGuard.Evaluate(
                            marketState,
                            _priceHistory.RecentObservations(pair, config.Freshness.FreshTapeSnapshotCount),
                            desired,
                            config.Freshness)
                        : null;
                    if (freshness is not null)
                    {
                        Console.WriteLine(
                            $"ENTRY_FRESHNESS pair={pair} desired={desired} nearHigh={freshness.IsNearHigh} freshTape={freshness.HasFreshUpwardTape} breakout={freshness.HasFreshBreakout} blocked={freshness.Blocked} pos24={freshness.PositionIn24hRangePct:0.###} distHigh={freshness.DistanceFromRecentHighPct:0.###} lastStep={freshness.LastSnapshotStepPct:0.###} slope={freshness.ShortSnapshotSlopePct:0.###}");
                    }

                    // Authoritative 24h-range LONG gate. Runs on the executable ask over a
                    // robust 24h range; fresh tape can never bypass it. SHORT is untouched.
                    longRange = freshness is not null
                        ? FuturesLongRangeGuard.Evaluate(marketState, freshness, desired, config.Freshness)
                        : null;
                    if (longRange is { Evaluated: true })
                    {
                        Console.WriteLine(
                            $"LONG_RANGE pair={pair} entry={longRange.EntryPrice:0.######}({longRange.EntryPriceSource}) rangeSrc={longRange.Range24hSource} robustLow={longRange.RobustLow24h:0.######} robustHigh={longRange.RobustHigh24h:0.######} pos={longRange.Range24hPosition:0.###} max={longRange.Max24hRangePositionForLong:0.###} rebound={longRange.DistanceFrom24hLowPct:0.###} rising={longRange.RisingSnapshotCount} slope={longRange.ShortSlopePct:0.###} freshTape={longRange.FreshTape} zone={longRange.Zone} antiChase={longRange.AntiChaseApplied} confirmations={longRange.ConfirmationsMet}/{longRange.ConfirmationsRequired} effMaxDrift={longRange.EffectiveMaxDriftPct:0.###} blocked={longRange.Blocked} reason={longRange.BlockReasonCode ?? "-"}");
                    }

                    // Authoritative SHORT entry gate — mirror of the LONG range/freshness
                    // guards on the executable bid. Fresh down-tape can never bypass it.
                    shortEntry = qualityGate.Approved && desired == FuturesDesiredExposure.Short
                        ? FuturesShortEntryGuard.Evaluate(
                            marketState,
                            _priceHistory.RecentObservations(pair, config.Freshness.FreshTapeSnapshotCount),
                            desired,
                            config.Shorts,
                            config.Freshness)
                        : null;
                    if (shortEntry is { Evaluated: true })
                    {
                        Console.WriteLine(
                            $"SHORT_ENTRY pair={pair} entry={shortEntry.EntryPrice:0.######}({shortEntry.EntryPriceSource}) rangeSrc={shortEntry.Range24hSource} robustLow={shortEntry.RobustLow24h:0.######} robustHigh={shortEntry.RobustHigh24h:0.######} pos={shortEntry.Range24hPosition:0.###} min={shortEntry.Min24hRangePositionForShort:0.###} pullback={shortEntry.DistanceFrom24hHighPct:0.###} falling={shortEntry.FallingSnapshotCount} slope={shortEntry.ShortSlopePct:0.###} freshTape={shortEntry.FreshTape} breakdown={shortEntry.HasFreshBreakdown} blocked={shortEntry.Blocked} reason={shortEntry.BlockReasonCode ?? "-"}");
                    }

                    // Relative strength versus BTC over the shared candle lookback. It is
                    // ALWAYS measured and recorded; it only vetoes when the operator turns
                    // Regime.RelativeStrengthGateEnabled on, so shipping this changes no
                    // entry behaviour until the recorded data justifies enabling it.
                    var relativeStrength = RelativeStrengthPct(freshness, btcRegime);
                    var relativeStrengthBlock = EvaluateRelativeStrengthGate(longRange, freshness, btcRegime, relativeStrength);
                    if (relativeStrengthBlock is not null)
                    {
                        Console.WriteLine(
                            $"LONG_RELATIVE_STRENGTH pair={pair} pairMomentum={freshness?.RecentCandleMomentumPct:0.###}% btcMomentum={btcRegime.RecentChangePct:0.###}% relative={relativeStrength:0.###}% min={config.Regime.MinRelativeStrengthPct:0.###}% blocked=true");
                    }

                    var followThroughGate = qualityGate.Approved && relativeStrengthBlock is null && longRange is { Blocked: false }
                        ? FuturesLongFollowThroughGate.Evaluate(
                            desired,
                            longRange,
                            freshness,
                            priceAction,
                            config.Freshness,
                            marketState.Candles)
                        : new RiskEvaluation(true, new[] { "long follow-through gate skipped" });
                    if (!followThroughGate.Approved)
                    {
                        Console.WriteLine(
                            $"LONG_FOLLOW_THROUGH pair={pair} zone={longRange?.Zone ?? "-"} breakout={freshness?.HasFreshBreakout} candleMom={freshness?.RecentCandleMomentumPct:0.###} priceAction={priceAction?.TrendPercent:0.###} blocked=true reason={followThroughGate.Reasons.FirstOrDefault() ?? "-"}");
                    }

                    // Gate precedence: relative strength (only when enabled), then the side
                    // range/anti-knife guard (its reasons are the most specific), then
                    // follow-through quality, then the freshness guard, then the quality gate.
                    var freshnessGate = qualityGate.Approved && relativeStrengthBlock is not null
                        ? new RiskEvaluation(false, new[] { relativeStrengthBlock })
                        : qualityGate.Approved && longRange is { Blocked: true }
                        ? new RiskEvaluation(false, new[] { longRange.BlockReason ?? "long blocked by 24h range guard" })
                        : qualityGate.Approved && shortEntry is { Blocked: true }
                            ? new RiskEvaluation(false, new[] { shortEntry.BlockReason ?? "short blocked by range guard" })
                        : qualityGate.Approved && !followThroughGate.Approved
                            ? followThroughGate
                        : qualityGate.Approved && freshness is { Blocked: true }
                            ? new RiskEvaluation(false, new[] { freshness.BlockReason ?? "entry stale near high" })
                            : qualityGate;
                    var portfolioGate = freshnessGate.Approved
                        ? EvaluatePortfolioEntryGuards(
                            state,
                            pair,
                            desired,
                            utc,
                            entryPlan?.SizedNotionalEur > 0m
                                ? entryPlan.SizedNotionalEur
                                : entryPlan!.RequestedNotionalEur)
                        : freshnessGate;
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
                        // Flipped-logic experiment: the LONG thesis passed every gate
                        // unchanged; only the executed side inverts to SHORT here, so
                        // order, ledger, TP/SL and reconciliation all see a real short.
                        var flipApplied = config.Futures.FlipLongEntries && desired == FuturesDesiredExposure.Long;
                        var executedDesired = flipApplied ? FuturesDesiredExposure.Short : desired;
                        fill = await ApplyOrExecuteLiveAsync(
                            state, pair, executedDesired, markPrice,
                            entryPlan.SizedNotionalEur > 0m ? entryPlan.SizedNotionalEur : entryPlan.RequestedNotionalEur,
                            entryPlan.EffectiveLeverage > 0m ? entryPlan.EffectiveLeverage : config.Futures.DefaultLeverage,
                            reduceOnly: false,
                            reason: string.Empty,
                            exitTriggerSource: null,
                            instrument: marketState.Instrument,
                            entryPlan: entryPlan,
                            cancellationToken,
                            signalPrice: marketState.LastPrice);
                        if (flipApplied)
                        {
                            fill.Action.Reason = string.IsNullOrWhiteSpace(fill.Action.Reason)
                                ? "flipped logic applied"
                                : $"{fill.Action.Reason}; flipped logic applied";
                            Console.WriteLine($"FLIPPED_LOGIC pair={pair} approved=LONG executed=SHORT");
                        }

                        if (freshness is not null)
                        {
                            freshness = FuturesEntryFreshnessGuard.WithFillDiagnostics(
                                freshness,
                                marketState,
                                desired,
                                fill.Action.AverageFillPrice ?? fill.Action.FillPrice,
                                config.Freshness);
                            AttachEntryFreshnessDiagnostics(fill.Action, freshness);
                        }

                        if (longRange is { Evaluated: true })
                        {
                            AttachLongRangeDiagnostics(fill.Action, longRange);
                        }

                        fill.Action.BtcRecentChangePct = btcRegime.RecentChangePct;
                        fill.Action.RelativeStrengthPct = relativeStrength;

                        if (shortEntry is { Evaluated: true })
                        {
                            AttachShortEntryDiagnostics(fill.Action, shortEntry);
                        }

                        var entryChannel = ClassifyEntryChannel(dipBounce, freshness, longRange, shortEntry);
                        fill.Action.EntryChannel = entryChannel;
                        if (dipBounce)
                        {
                            fill.Action.DipBounceMinScoreApplied = config.Dip.MinScore;
                        }

                        AttachEntryPlanDiagnostics(fill.Action, entryPlan);

                        if (fill.PositionOpened)
                        {
                            var openedPosition = state.Positions.FirstOrDefault(position => position.Pair == pair);
                            if (openedPosition is not null)
                            {
                                openedPosition.EntryChannel = entryChannel;
                            }

                            newEntriesThisCycle++;
                        }

                        decisions.Add(BuildDecisionRecord(marketState, indicators, signal, fill, riskApproved, riskReasons, priceAction));
                        continue;
                    }
                }

                fill = await ApplyOrExecuteLiveAsync(
                    state, pair, desired, markPrice,
                    entryPlan?.SizedNotionalEur > 0m ? entryPlan.SizedNotionalEur : config.Futures.DerivedNotionalEur(config.Futures.DefaultLeverage),
                    entryPlan?.EffectiveLeverage > 0m ? entryPlan.EffectiveLeverage : config.Futures.DefaultLeverage,
                    reduceOnly: false,
                    reason: string.Empty,
                    exitTriggerSource: null,
                    instrument: marketState.Instrument,
                    entryPlan: entryPlan,
                    cancellationToken);
                if (entryPlan is not null)
                {
                    AttachEntryPlanDiagnostics(fill.Action, entryPlan);
                }

                if (freshness is not null)
                {
                    AttachEntryFreshnessDiagnostics(fill.Action, freshness);
                    if (freshness.Blocked)
                    {
                        fill.Action.HoldReasonCode = FuturesEntryFreshnessGuard.HoldReasonCode;
                    }
                }

                if (shortEntry is { Evaluated: true })
                {
                    AttachShortEntryDiagnostics(fill.Action, shortEntry);
                    // The side range/anti-knife block reason is the most specific, so it
                    // wins the hold-reason code when that guard rejected the entry.
                    if (shortEntry.Blocked)
                    {
                        fill.Action.HoldReasonCode = shortEntry.BlockReasonCode;
                    }
                }

                if (longRange is { Evaluated: true })
                {
                    AttachLongRangeDiagnostics(fill.Action, longRange);
                    // The 24h-range block reason is the most specific, so it wins the
                    // hold-reason code when the range guard rejected the entry.
                    if (longRange.Blocked)
                    {
                        fill.Action.HoldReasonCode = longRange.BlockReasonCode;
                    }
                }

                fill.Action.BtcRecentChangePct = btcRegime.RecentChangePct;
                fill.Action.RelativeStrengthPct = RelativeStrengthPct(freshness, btcRegime);
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
        var recorded = 0;
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
            var trigger = tpSl.Evaluate(held, markPrice, marketState.Quote?.Last ?? marketState.LastPrice, marketState.Quote?.Bid, marketState.Quote?.Ask);
            FuturesFillResult? fill = null;
            if (trigger is not null)
            {
                fill = await HandleTpSlTriggerAsync(state, held, trigger, markPrice, marketState.Instrument, cancellationToken, fast: true);
            }
            else if (!IsExternalFuturesPosition(held)
                && EvaluateMaxHoldExit(held, utc, config.Exits.MaxHoldMinutes, config.Exits.MaxHoldMinStopProgressPct) is { ShouldClose: true } maxHold)
            {
                fill = await ApplyOrExecuteLiveAsync(
                    state, held.Pair, FuturesDesiredExposure.Flat, markPrice,
                    0m, held.Leverage ?? 1m, reduceOnly: true,
                    reason: maxHold.Reason ?? $"MAX_HOLD fast close after {config.Exits.MaxHoldMinutes}m",
                    exitTriggerSource: config.TpSl.TriggerSource,
                    instrument: marketState.Instrument,
                    entryPlan: null,
                    cancellationToken);
                fill.Action.ExitReasonCode = "SELL_MAX_HOLD";
            }

            if (fill is not null)
            {
                decisions.Add(BuildFastExitDecisionRecord(marketState, fill));
                recorded++;
                if (fill.PositionClosed)
                {
                    closed++;
                    Console.WriteLine($"futures fast-exit-check: closed {held.Pair} reason={fill.Action.ExitReasonCode ?? fill.Action.Reason}");
                }
                else
                {
                    Console.WriteLine($"futures fast-exit-check: recorded {held.Pair} action={fill.Action.Action} reason={fill.Action.HoldReasonCode ?? fill.Action.Reason}");
                }
            }
        }

        portfolio.Save(state);
        if (recorded > 0)
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
            Console.WriteLine($"futures fast-exit-check: recorded={recorded} closed={closed} remainingPositions={state.Positions.Count}");
        }
    }

    private static decimal FastExitMarkPrice(InstrumentMarketState marketState) =>
        marketState.Quote?.MarkPrice
        ?? marketState.LastPrice;

    internal static IReadOnlyList<InstrumentOptions> SelectActiveInstruments(
        IReadOnlyList<InstrumentMarketState> lightStates,
        IReadOnlySet<string> heldPairs,
        IReadOnlyList<string> forceIncludePairs,
        TradingOptions trading)
    {
        var forceIncluded = forceIncludePairs
            .Where(pair => !string.IsNullOrWhiteSpace(pair))
            .Select(pair => pair.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var selected = lightStates
            .OrderByDescending(candidate => heldPairs.Contains(candidate.Instrument.Pair))
            .ThenByDescending(candidate => IsStrongMoverActiveCandidate(candidate, trading))
            .ThenByDescending(candidate => Math.Abs(candidate.ChangePercent))
            .ThenByDescending(candidate => candidate.LastVolume * candidate.LastPrice)
            .Take(Math.Max(trading.MaxActiveInstruments, heldPairs.Count))
            .Select(candidate => candidate.Instrument)
            .ToList();

        var selectedPairs = selected
            .Select(instrument => instrument.Pair)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var state in lightStates.Where(state => IsForceIncludedActiveCandidate(state, forceIncluded)))
        {
            if (selectedPairs.Add(state.Instrument.Pair))
            {
                selected.Add(state.Instrument);
            }
        }

        return selected;
    }

    private static bool IsForceIncludedActiveCandidate(InstrumentMarketState state, IReadOnlySet<string> forceIncluded) =>
        forceIncluded.Contains(state.Instrument.Pair)
        || forceIncluded.Contains(state.Instrument.KrakenPair);

    private static bool IsStrongMoverActiveCandidate(InstrumentMarketState state, TradingOptions trading)
    {
        var notionalVolume = state.LastVolume * state.LastPrice;
        return Math.Abs(state.ChangePercent) >= trading.StrongMoverMinChangePercent
            && notionalVolume >= trading.StrongMoverMinDailyVolumeEur;
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

    private async Task<FuturesFillResult> HandleTpSlTriggerAsync(
        PortfolioState state,
        PortfolioPosition held,
        TpSlOrchestrator.TpSlTrigger trigger,
        decimal markPrice,
        InstrumentOptions instrument,
        CancellationToken cancellationToken,
        bool fast)
    {
        var prefix = fast ? "fast " : string.Empty;
        if (trigger.Kind == "TAKE_PROFIT"
            && config.Futures.LiveTradingEnabled
            && IsBotOwnedFuturesPosition(held))
        {
            var trailing = await ActivateTrailingStopAsync(held, instrument, cancellationToken);
            var trailingSucceeded = TrailingActivationSucceeded(trailing);
            if (!trailingSucceeded)
            {
                held.TpOrderState = trigger.PreviousTpOrderState;
                held.SlOrderState = trigger.PreviousSlOrderState;
            }

            var desired = held.Side.Equals("SHORT", StringComparison.OrdinalIgnoreCase)
                ? FuturesDesiredExposure.Short
                : FuturesDesiredExposure.Long;
            var hold = portfolio.Apply(
                state,
                held.Pair,
                desired,
                markPrice,
                0m,
                held.Leverage ?? 1m,
                reason: trailing);
            hold.Action.HoldReasonCode = trailingSucceeded
                ? "TRAILING_ACTIVATED"
                : "TRAILING_ACTIVATION_FAILED";
            hold.Action.ExitTriggerSource = trigger.TriggerSource;
            return hold;
        }

        var fill = await ApplyOrExecuteLiveAsync(
            state, held.Pair, FuturesDesiredExposure.Flat, markPrice,
            0m, held.Leverage ?? 1m, reduceOnly: true,
            reason: $"{trigger.Kind} {prefix}trigger at {trigger.TriggerPrice:0.####}",
            exitTriggerSource: trigger.TriggerSource,
            instrument: instrument,
            entryPlan: null,
            cancellationToken);
        fill.Action.ExitReasonCode = trigger.Kind == "STOP_LOSS" ? "SELL_STOP_LOSS" : "SELL_TAKE_PROFIT";

        if (fill.PositionClosed && config.Futures.LiveTradingEnabled && IsBotOwnedFuturesPosition(held))
        {
            if (await IsPositionClosedOnExchangeAsync(instrument, cancellationToken))
            {
                await CancelProtectionOrdersAsync(held, instrument, cancellationToken);
            }
            else
            {
                Console.WriteLine($"futures-tpsl-cleanup: skipped for {instrument.KrakenPair}; close order accepted but exchange position is still open");
            }
        }

        return fill;
    }

    private async Task<string> ActivateTrailingStopAsync(
        PortfolioPosition position,
        InstrumentOptions instrument,
        CancellationToken cancellationToken,
        string reasonPrefix = "working take-profit reached",
        bool requireBothProtectionOrders = false,
        (FuturesOpenOrder? StopLossOrder, FuturesOpenOrder? TakeProfitOrder, FuturesOpenOrder? TrailingStopOrder)? knownProtection = null)
    {
        if (broker?.IsConfigured != true)
        {
            return $"{reasonPrefix}, but live broker unavailable; protective TP/SL left unchanged";
        }

        if (position.TrailingStopState?.Equals("EXCHANGE_OPEN", StringComparison.OrdinalIgnoreCase) == true)
        {
            return $"{reasonPrefix}; trailing stop already active at {position.TrailingStopPercent:0.###}%";
        }

        var protection = knownProtection ?? FindProtectionOrders(await broker.GetOpenOrdersAsync(cancellationToken), instrument.KrakenPair, position.Side);
        if (requireBothProtectionOrders && (protection.StopLossOrder is null || protection.TakeProfitOrder is null))
        {
            return $"{reasonPrefix}, but existing reduce-only TP/SL pair was not found; trailing not armed";
        }

        var cancelled = new List<string>();
        foreach (var order in new[] { protection.StopLossOrder, protection.TakeProfitOrder }.Where(order => order is not null).Cast<FuturesOpenOrder>())
        {
            var cancel = await broker.CancelOrderAsync(order.OrderId, cancellationToken);
            if (!cancel.Accepted)
            {
                return $"{reasonPrefix}, but protective order cancel failed orderId={order.OrderId}: {cancel.Error ?? cancel.Status}; trailing not armed";
            }

            cancelled.Add(order.OrderId);
        }

        var closeSide = CloseSide(position.Side);
        var trailingPercent = config.TpSl.TrailingStopPercent;
        var trailing = await broker.SendTrailingStopOrderAsync(
            instrument.KrakenPair,
            closeSide,
            position.Quantity,
            trailingPercent,
            config.TpSl.TriggerSource,
            reduceOnly: true,
            cancellationToken);

        if (trailing.Accepted)
        {
            position.TpOrderState = "CANCELLED";
            position.SlOrderState = "CANCELLED";
            position.TrailingStopState = "EXCHANGE_OPEN";
            position.TrailingStopPercent = trailingPercent;
            position.TrailingStopOrderId = trailing.OrderId;
            position.TrailingActivatedAtUtc = _clock.UtcNow;
            Console.WriteLine($"futures-trailing-arm: symbol={instrument.KrakenPair} side={position.Side} orderId={trailing.OrderId ?? "-"} distancePct={trailingPercent:0.###} cancelled=[{string.Join(",", cancelled)}]");
            return $"{reasonPrefix}; cancelled protective TP/SL and activated reduce-only trailing stop {trailingPercent:0.###}%";
        }

        var restored = await RestoreProtectiveStopLossAsync(position, instrument, cancellationToken);
        var restoreText = restored ? "protective SL restored" : "protective SL restore FAILED";
        return $"{reasonPrefix}, but trailing stop failed: {trailing.Error ?? trailing.Status}; {restoreText}";
    }

    private async Task TryActivateExternalTrailingStopAsync(
        PortfolioPosition position,
        InstrumentOptions instrument,
        (FuturesOpenOrder? StopLossOrder, FuturesOpenOrder? TakeProfitOrder, FuturesOpenOrder? TrailingStopOrder) protection,
        CancellationToken cancellationToken)
    {
        if (!config.TpSl.Enabled
            || config.TpSl.ExternalTrailingActivationProgressPercent <= 0m
            || broker?.IsConfigured != true
            || position.TrailingStopState?.Equals("EXCHANGE_OPEN", StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }

        if (position.Origin?.Equals(PositionOrigins.KrakenSync, StringComparison.OrdinalIgnoreCase) != true)
        {
            return;
        }

        var stop = protection.StopLossOrder;
        var take = protection.TakeProfitOrder;
        if (stop?.StopPrice is not > 0m
            || take?.StopPrice is not > 0m
            || !OrderSizeMatchesPosition(stop, position)
            || !OrderSizeMatchesPosition(take, position)
            || !IsValidProtectionPair(position, stop.StopPrice.Value, take.StopPrice.Value))
        {
            return;
        }

        var ticker = await broker.GetTickerAsync(instrument.KrakenPair, cancellationToken);
        var isShort = position.Side.Equals("SHORT", StringComparison.OrdinalIgnoreCase);
        var closeablePrice = isShort
            ? ticker?.Ask ?? 0m
            : ticker?.Bid ?? 0m;
        if (closeablePrice <= 0m)
        {
            return;
        }

        var progressPct = TakeProfitProgressPct(position.EntryPrice, take.StopPrice.Value, closeablePrice, position.Side);
        if (progressPct < config.TpSl.ExternalTrailingActivationProgressPercent)
        {
            return;
        }

        var reasonPrefix =
            $"external position reached {progressPct:0.##}% of TP path (threshold {config.TpSl.ExternalTrailingActivationProgressPercent:0.##}%)";
        var result = await ActivateTrailingStopAsync(
            position,
            instrument,
            cancellationToken,
            reasonPrefix,
            requireBothProtectionOrders: true,
            knownProtection: protection);
        Console.WriteLine($"futures-external-trailing: symbol={instrument.KrakenPair} side={position.Side} progress={progressPct:0.##}% result={result}");
    }

    private async Task<bool> RestoreProtectiveStopLossAsync(
        PortfolioPosition position,
        InstrumentOptions instrument,
        CancellationToken cancellationToken)
    {
        if (broker?.IsConfigured != true)
        {
            return false;
        }

        var stopPrice = position.ExchangeStopLossPrice is > 0m
            ? position.ExchangeStopLossPrice.Value
            : ExchangeProtectionPrice(position.EntryPrice, position.Side, isTakeProfit: false);
        if (stopPrice <= 0m)
        {
            return false;
        }

        stopPrice = RoundTriggerPrice(stopPrice, position.Side, isTakeProfit: false, instrument.PriceDecimals);
        var stop = await broker.SendTriggerOrderAsync(
            instrument.KrakenPair,
            CloseSide(position.Side),
            position.Quantity,
            "stp",
            stopPrice,
            config.TpSl.TriggerSource,
            reduceOnly: true,
            cancellationToken);
        if (!stop.Accepted)
        {
            Console.WriteLine($"futures-trailing-rollback: FAILED symbol={instrument.KrakenPair} kind=stop_loss price={stopPrice:0.########} reason={stop.Error ?? stop.Status}");
            return false;
        }

        position.SlOrderState = "EXCHANGE_OPEN";
        position.ExchangeStopLossPrice = stopPrice;
        Console.WriteLine($"futures-trailing-rollback: restored stop_loss symbol={instrument.KrakenPair} orderId={stop.OrderId ?? "-"} price={stopPrice:0.########}");
        return true;
    }

    private async Task CancelProtectionOrdersAsync(
        PortfolioPosition position,
        InstrumentOptions instrument,
        CancellationToken cancellationToken)
    {
        if (broker?.IsConfigured != true)
        {
            return;
        }

        var openOrders = await broker.GetOpenOrdersAsync(cancellationToken);
        var protection = FindProtectionOrders(openOrders, instrument.KrakenPair, position.Side);
        foreach (var order in new[] { protection.StopLossOrder, protection.TakeProfitOrder }.Where(order => order is not null).Cast<FuturesOpenOrder>())
        {
            var cancel = await broker.CancelOrderAsync(order.OrderId, cancellationToken);
            Console.WriteLine($"futures-tpsl-cleanup: symbol={instrument.KrakenPair} orderId={order.OrderId} status={cancel.Status} accepted={cancel.Accepted}");
        }
    }

    private async Task<bool> IsPositionClosedOnExchangeAsync(InstrumentOptions instrument, CancellationToken cancellationToken)
    {
        if (broker?.IsConfigured != true)
        {
            return false;
        }

        var openPositions = await broker.GetOpenPositionsAsync(cancellationToken);
        return !openPositions.Any(position =>
            position.Symbol.Equals(instrument.KrakenPair, StringComparison.OrdinalIgnoreCase)
            && position.Size > 0m);
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
        CancellationToken cancellationToken,
        decimal? signalPrice = null)
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
        // the REAL target notional (margin * leverage) — never from
        // entryPlan.FilledNotionalEur, which is the dry-run maker-fill simulation
        // (it models partial passive fills, e.g. exactly half, that do not apply to
        // a taker order). The EUR notional is converted to the instrument's USD
        // quote currency via UsdPerEur before dividing by the USD mark price.
        var notionalQuote = targetNotionalEur * config.Futures.UsdPerEur;
        var rawSize = markPrice <= 0m ? 0m : notionalQuote / markPrice;
        var quantityDecimals = instrument.QuantityDecimals ?? 8;
        var size = TruncateToDecimals(rawSize, quantityDecimals);
        if (size <= 0m)
        {
            var skipReason = $"live futures entry skipped: raw size {rawSize:0.########} rounds to zero at Kraken quantity precision {quantityDecimals}";
            var skipped = portfolio.Apply(state, pair, FuturesDesiredExposure.Flat, markPrice, 0m, leverage, reason: skipReason);
            skipped.Action.HoldReasonCode = "LIVE_ORDER_SIZE_TOO_SMALL";
            return skipped;
        }

        var side = desired == FuturesDesiredExposure.Short ? "sell" : "buy";
        var preSubmit = await broker.GetTickerAsync(instrument.KrakenPair, cancellationToken);
        if (preSubmit is null)
        {
            var skipReason = $"live futures entry skipped: fresh pre-submit ticker unavailable for {instrument.KrakenPair}";
            var skipped = portfolio.Apply(state, pair, FuturesDesiredExposure.Flat, markPrice, 0m, leverage, reason: skipReason);
            skipped.Action.HoldReasonCode = "ENTRY_INVALID_REFERENCE_PRICE";
            return skipped;
        }

        var referencePrice = signalPrice is > 0m ? signalPrice.Value : markPrice;
        var maxDeviation = config.Entry.MaxEntryPriceDeviationPct;
        var rawLimitPrice = desired == FuturesDesiredExposure.Short
            ? referencePrice * (1m - maxDeviation / 100m)
            : referencePrice * (1m + maxDeviation / 100m);
        var priceDecimals = ResolvePriceDecimals(instrument, preSubmit, referencePrice);
        var limitPrice = RoundLimitPrice(rawLimitPrice, desired, priceDecimals);
        var quoteAlreadyWorse = desired == FuturesDesiredExposure.Short
            ? preSubmit.Bid < limitPrice
            : preSubmit.Ask > limitPrice;
        if (quoteAlreadyWorse)
        {
            var rejectReason = desired == FuturesDesiredExposure.Short
                ? $"live futures entry skipped: refreshed bid {preSubmit.Bid:0.########} is below min allowed {limitPrice:0.########} from signal {referencePrice:0.########} ({maxDeviation:0.###}% max deviation, price decimals {priceDecimals})"
                : $"live futures entry skipped: refreshed ask {preSubmit.Ask:0.########} exceeds max allowed {limitPrice:0.########} from signal {referencePrice:0.########} ({maxDeviation:0.###}% max deviation, price decimals {priceDecimals})";
            Console.WriteLine($"EXECUTION pair={pair} symbol={instrument.KrakenPair} side={side} rejected=PRICE_DEVIATION signal={referencePrice:0.########} bid={preSubmit.Bid:0.########} ask={preSubmit.Ask:0.########} limit={limitPrice:0.########}");
            var rejected = portfolio.Apply(state, pair, FuturesDesiredExposure.Flat, markPrice, 0m, leverage, reason: rejectReason);
            rejected.Action.HoldReasonCode = "LIVE_ENTRY_PRICE_DEVIATION";
            AttachExecutionDiagnostics(rejected.Action, referencePrice, preSubmit, limitPrice, size, null, null);
            return rejected;
        }

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

        var order = await broker.SendIocLimitOrderAsync(instrument.KrakenPair, side, size, limitPrice, reduceOnly: false, cancellationToken);
        if (!order.Accepted)
        {
            var rejectReason = $"live futures entry rejected: {order.Error ?? order.Status}";
            Console.WriteLine($"futures-live-order-rejected: pair={pair} krakenPair={instrument.KrakenPair} side={side} rawSize={rawSize:0.########} size={size:0.########} limit={limitPrice:0.########} rawLimit={rawLimitPrice:0.##############} quantityDecimals={quantityDecimals} priceDecimals={priceDecimals} leverage={entryLeverage:0.#}x reason={rejectReason}");
            var rejected = portfolio.Apply(state, pair, FuturesDesiredExposure.Flat, markPrice, 0m, entryLeverage, reason: rejectReason);
            rejected.Action.HoldReasonCode = "LIVE_ORDER_REJECTED";
            rejected.Action.FillSource = "REAL_REJECTED";
            AttachExecutionDiagnostics(rejected.Action, referencePrice, preSubmit, limitPrice, size, order, null);
            return rejected;
        }

        if (!order.FillKnown)
        {
            var pendingReason = $"live Kraken Futures order accepted id={order.OrderId ?? "-"} status={order.Status} but fill details were not returned; reconciliation pending";
            Console.WriteLine($"EXECUTION pair={pair} symbol={instrument.KrakenPair} side={side} status=FILL_RECONCILIATION_PENDING orderId={order.OrderId ?? "-"} requestedQty={size:0.########} limit={limitPrice:0.########} priceDecimals={priceDecimals}");
            state.PendingFuturesOrders.RemoveAll(order => order.Pair.Equals(pair, StringComparison.OrdinalIgnoreCase));
            state.PendingFuturesOrders.Add(new PendingFuturesOrder
            {
                Pair = pair,
                ExchangeOrderId = order.OrderId,
                CreatedAtUtc = _clock.UtcNow,
                RequestedQuantity = size,
                SubmittedLimitPrice = limitPrice
            });
            var pending = portfolio.Apply(state, pair, FuturesDesiredExposure.Flat, markPrice, 0m, entryLeverage, reason: pendingReason);
            pending.Action.HoldReasonCode = "FILL_RECONCILIATION_PENDING";
            pending.Action.FillSource = "FILL_RECONCILIATION_PENDING";
            AttachExecutionDiagnostics(pending.Action, referencePrice, preSubmit, limitPrice, size, order, null);
            return pending;
        }

        var fillDetails = order.Fill!;
        var filledNotionalEur = config.Futures.UsdPerEur <= 0m
            ? fillDetails.Quantity * fillDetails.AveragePrice
            : fillDetails.Quantity * fillDetails.AveragePrice / config.Futures.UsdPerEur;
        var adjustedPlan = entryPlan is null
            ? null
            : entryPlan with { FilledNotionalEur = filledNotionalEur };

        // Record the virtual ledger against the exchange average fill and filled
        // quantity. Partial IOC fills commit only the filled part; unfilled remainder
        // is canceled by the exchange.
        var opened = portfolio.Apply(state, pair, desired, fillDetails.AveragePrice, filledNotionalEur, entryLeverage, reduceOnly: false, reason, exitTriggerSource, adjustedPlan);
        opened.Action.FillSource = "REAL";
        opened.Action.Reason = $"live Kraken Futures IOC accepted id={order.OrderId ?? "-"} status={order.Status}; {opened.Action.Reason}";
        AttachExecutionDiagnostics(opened.Action, referencePrice, preSubmit, limitPrice, size, order, fillDetails);
        if (state.Positions.FirstOrDefault(position => position.Pair.Equals(pair, StringComparison.OrdinalIgnoreCase)) is { } openedPosition)
        {
            var remote = new FuturesOpenPosition(
                instrument.KrakenPair,
                openedPosition.Side,
                openedPosition.Quantity,
                openedPosition.EntryPrice,
                openedPosition.MarkPrice ?? openedPosition.LastPrice,
                openedPosition.Leverage ?? entryLeverage);
            var tpSl = new ImportedTpSlState(
                openedPosition.TpOrderState,
                openedPosition.SlOrderState,
                openedPosition.StopLossPrice,
                openedPosition.TakeProfitPrice,
                openedPosition.StopDistancePct,
                openedPosition.TakeProfitDistancePct,
                openedPosition.ExchangeStopLossPrice,
                openedPosition.ExchangeTakeProfitPrice,
                openedPosition.ExchangeProtectionMultiplierPercent,
                openedPosition.TrailingStopState,
                openedPosition.TrailingStopPercent,
                openedPosition.TrailingStopOrderId,
                openedPosition.TrailingActivatedAtUtc);
            var armed = await EnsureExchangeProtectionOrdersAsync(instrument, remote, tpSl, null, null, botOwned: true, cancellationToken);
            openedPosition.TpOrderState = armed.TpOrderState;
            openedPosition.SlOrderState = armed.SlOrderState;
            openedPosition.ExchangeStopLossPrice = armed.ExchangeStopLossPrice;
            openedPosition.ExchangeTakeProfitPrice = armed.ExchangeTakeProfitPrice;
        }

        Console.WriteLine($"EXECUTION pair={pair} symbol={instrument.KrakenPair} side={side} status={order.Status} orderId={order.OrderId ?? "-"} requestedQty={size:0.########} filledQty={fillDetails.Quantity:0.########} avgFill={fillDetails.AveragePrice:0.########} limit={limitPrice:0.########} priceDecimals={priceDecimals}");
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

    private static decimal RoundLimitPrice(decimal value, FuturesDesiredExposure desired, int decimals)
    {
        decimals = Math.Clamp(decimals, 0, 8);
        var factor = 1m;
        for (var i = 0; i < decimals; i++)
        {
            factor *= 10m;
        }

        var scaled = value * factor;
        var rounded = desired == FuturesDesiredExposure.Short
            ? Math.Ceiling(scaled)
            : Math.Floor(scaled);
        return rounded / factor;
    }

    private static int ResolvePriceDecimals(InstrumentOptions instrument, FuturesTickerQuote preSubmit, decimal referencePrice)
    {
        if (instrument.PriceDecimals is >= 0)
        {
            return Math.Clamp(instrument.PriceDecimals.Value, 0, 8);
        }

        return new[]
            {
                DecimalPlaces(preSubmit.Bid),
                DecimalPlaces(preSubmit.Ask),
                DecimalPlaces(preSubmit.Last),
                DecimalPlaces(preSubmit.MarkPrice ?? 0m),
                DecimalPlaces(referencePrice)
            }
            .Where(decimals => decimals > 0)
            .DefaultIfEmpty(2)
            .Min();
    }

    private static int DecimalPlaces(decimal value)
    {
        if (value <= 0m)
        {
            return 0;
        }

        value = decimal.Round(value, 8);
        var places = 0;
        while (places < 8 && value != decimal.Truncate(value))
        {
            value *= 10m;
            places++;
        }

        return places;
    }

    private async Task RefreshDeadManSwitchAsync(CancellationToken cancellationToken)
    {
        if (!config.Futures.LiveTradingEnabled || !config.Futures.DeadManSwitchEnabled || broker?.IsConfigured != true)
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
        var openOrders = await broker.GetOpenOrdersAsync(cancellationToken);
        var bySymbol = universe
            .Where(instrument => !string.IsNullOrWhiteSpace(instrument.KrakenPair))
            .ToDictionary(instrument => instrument.KrakenPair, StringComparer.OrdinalIgnoreCase);
        var markByPair = lightStates.ToDictionary(state => state.Instrument.Pair, state => state.LastPrice, StringComparer.OrdinalIgnoreCase);

        var collateral = await ConvertFuturesAvailableCollateralToEurAsync(accounts, cancellationToken);
        if (collateral.AvailableEur > 0m)
        {
            state.CashEur = collateral.AvailableEur;
            state.CashQuoteValue = collateral.AvailableUsd > 0m ? collateral.AvailableUsd : null;
            state.CashQuoteCurrency = collateral.AvailableUsd > 0m ? "USD" : null;
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
            // MaxLeverage is an entry cap, not a historical truth filter. A position that
            // already exists on Kraken must be booked with the exchange-reported leverage;
            // otherwise lowering the new-entry cap corrupts synced margin/value math.
            var leverage = remote.Leverage <= 0m
                ? Math.Clamp(config.Futures.DefaultLeverage, 1m, config.Futures.MaxLeverage)
                : Math.Max(1m, remote.Leverage);
            var notional = remote.EntryPrice * remote.Size;
            var initialMargin = leverage <= 0m ? notional : notional / leverage;
            var pnl = FuturesMath.UnrealizedPnlEur(remote.Side, remote.EntryPrice, mark, remote.Size);
            var existing = state.Positions.FirstOrDefault(position => position.Pair.Equals(instrument.Pair, StringComparison.OrdinalIgnoreCase));
            var protectionOrders = FindProtectionOrders(openOrders, remote.Symbol, remote.Side);
            var tpSl = ImportedTpSl(existing, remote.Side, remote.EntryPrice, protectionOrders.StopLossOrder, protectionOrders.TakeProfitOrder, protectionOrders.TrailingStopOrder);
            tpSl = await EnsureExchangeProtectionOrdersAsync(
                instrument,
                remote,
                tpSl,
                protectionOrders.StopLossOrder,
                protectionOrders.TakeProfitOrder,
                botOwned: existing?.Origin?.Equals(PositionOrigins.Bot, StringComparison.OrdinalIgnoreCase) == true,
                cancellationToken);
            var origin = existing?.Origin ?? PositionOrigins.KrakenSync;
            var importedPosition = new PortfolioPosition
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
                TpOrderState = tpSl.TpOrderState,
                SlOrderState = tpSl.SlOrderState,
                Origin = origin,
                StopLossPrice = tpSl.StopLossPrice,
                TakeProfitPrice = tpSl.TakeProfitPrice,
                ExchangeStopLossPrice = tpSl.ExchangeStopLossPrice,
                ExchangeTakeProfitPrice = tpSl.ExchangeTakeProfitPrice,
                ExchangeProtectionMultiplierPercent = tpSl.ExchangeProtectionMultiplierPercent,
                TrailingStopState = tpSl.TrailingStopState,
                TrailingStopPercent = tpSl.TrailingStopPercent,
                TrailingStopOrderId = tpSl.TrailingStopOrderId,
                TrailingActivatedAtUtc = tpSl.TrailingActivatedAtUtc,
                EntryChannel = existing?.EntryChannel,
                EntryAtr = existing?.EntryAtr,
                RoundTripCostEstimatePct = existing?.RoundTripCostEstimatePct,
                ExpectedFundingPct = existing?.ExpectedFundingPct,
                AtrPct = existing?.AtrPct,
                StopDistancePct = tpSl.StopDistancePct,
                TakeProfitDistancePct = tpSl.TakeProfitDistancePct
            };

            if (origin.Equals(PositionOrigins.KrakenSync, StringComparison.OrdinalIgnoreCase))
            {
                await TryActivateExternalTrailingStopAsync(importedPosition, instrument, protectionOrders, cancellationToken);
            }

            imported.Add(importedPosition);
        }

        var before = state.Positions.Count;
        state.Positions = imported;
        state.PendingFuturesOrders.RemoveAll(order =>
            imported.Any(position => position.Pair.Equals(order.Pair, StringComparison.OrdinalIgnoreCase)));
        state.UpdatedAt = utc;
        var quoteText = state.CashQuoteValue is { } quote
            ? $" ({quote:0.####} {state.CashQuoteCurrency})"
            : "";
        Console.WriteLine($"futures-kraken-sync: accounts={accounts.Count} remotePositions={positions.Count} openOrders={openOrders.Count} trackedPositions={state.Positions.Count} previousTracked={before} availableCollateral={state.CashEur:0.####} EUR{quoteText}");
    }

    private static async Task<FuturesCollateralTotals> ConvertFuturesAvailableCollateralToEurAsync(
        IReadOnlyList<FuturesAccountBalance> accounts,
        CancellationToken cancellationToken)
    {
        var eur = 0m;
        var usd = 0m;
        decimal? usdToEur = null;

        foreach (var account in accounts)
        {
            var currency = NormalizeCurrency(account.Currency);
            if (account.AvailableMargin <= 0m)
            {
                continue;
            }

            if (currency is "EUR")
            {
                eur += account.AvailableMargin;
                continue;
            }

            if (currency is "USD" or "USDC" or "USDT")
            {
                usd += account.AvailableMargin;
                usdToEur ??= await FetchUsdToEurRateAsync(cancellationToken);
                eur += usdToEur is { } rate
                    ? account.AvailableMargin * rate
                    : account.AvailableMargin;
                continue;
            }

            // Unknown collateral units are rare for this account. Keep them visible
            // instead of dropping buying power, but make the fallback explicit in logs.
            eur += account.AvailableMargin;
            Console.WriteLine($"futures-kraken-sync: unknown collateral currency '{account.Currency}', treating available {account.AvailableMargin:0.####} as EUR");
        }

        return new FuturesCollateralTotals(eur, usd);
    }

    private static async Task<decimal?> FetchUsdToEurRateAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await FxHttpClient.GetAsync(
                "https://api.kraken.com/0/public/Ticker?assetVersion=1&pair=EUR/USD",
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!doc.RootElement.TryGetProperty("result", out var result)
                || !result.TryGetProperty("EUR/USD", out var ticker)
                || !ticker.TryGetProperty("c", out var close)
                || close.ValueKind != JsonValueKind.Array
                || close.GetArrayLength() == 0
                || !decimal.TryParse(close[0].GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var eurUsd)
                || eurUsd <= 0m)
            {
                Console.WriteLine("futures-kraken-sync: EUR/USD ticker missing; keeping USD available collateral without conversion");
                return null;
            }

            return 1m / eurUsd;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Console.WriteLine($"futures-kraken-sync: EUR/USD conversion unavailable ({ex.Message}); keeping USD available collateral without conversion");
            return null;
        }
    }

    private static string NormalizeCurrency(string currency)
    {
        var normalized = currency.Trim().ToUpperInvariant();
        return normalized switch
        {
            "ZEUR" => "EUR",
            "ZUSD" => "USD",
            "USD.M" => "USD",
            "EUR.M" => "EUR",
            _ => normalized
        };
    }

    private sealed record FuturesCollateralTotals(decimal AvailableEur, decimal AvailableUsd);

    private async Task<ImportedTpSlState> EnsureExchangeProtectionOrdersAsync(
        InstrumentOptions instrument,
        FuturesOpenPosition remote,
        ImportedTpSlState tpSl,
        FuturesOpenOrder? existingStopLoss,
        FuturesOpenOrder? existingTakeProfit,
        bool botOwned,
        CancellationToken cancellationToken)
    {
        if (!config.TpSl.Enabled || broker?.IsConfigured != true)
        {
            return tpSl;
        }

        if (!botOwned)
        {
            return tpSl;
        }

        if (tpSl.TrailingStopState?.Equals("EXCHANGE_OPEN", StringComparison.OrdinalIgnoreCase) == true)
        {
            return tpSl;
        }

        var closeSide = CloseSide(remote.Side);
        var triggerSource = config.TpSl.TriggerSource;
        var result = tpSl;
        if (existingStopLoss is null && tpSl.ExchangeStopLossPrice is > 0m)
        {
            var stopPrice = RoundTriggerPrice(tpSl.ExchangeStopLossPrice.Value, remote.Side, isTakeProfit: false, instrument.PriceDecimals);
            var stop = await broker.SendTriggerOrderAsync(
                instrument.KrakenPair,
                closeSide,
                remote.Size,
                "stp",
                stopPrice,
                triggerSource,
                reduceOnly: true,
                cancellationToken);
            if (stop.Accepted)
            {
                result = result with { SlOrderState = "EXCHANGE_OPEN", ExchangeStopLossPrice = stopPrice };
                Console.WriteLine($"futures-tpsl-arm: symbol={instrument.KrakenPair} side={remote.Side} kind=stop_loss orderId={stop.OrderId ?? "-"} price={stopPrice:0.########}");
            }
            else
            {
                Console.WriteLine($"futures-tpsl-arm: FAILED symbol={instrument.KrakenPair} side={remote.Side} kind=stop_loss price={stopPrice:0.########} rawPrice={tpSl.ExchangeStopLossPrice:0.########} reason={stop.Error ?? stop.Status}");
            }
        }

        if (existingTakeProfit is null && tpSl.ExchangeTakeProfitPrice is > 0m)
        {
            var takeProfitPrice = RoundTriggerPrice(tpSl.ExchangeTakeProfitPrice.Value, remote.Side, isTakeProfit: true, instrument.PriceDecimals);
            var takeProfit = await broker.SendTriggerOrderAsync(
                instrument.KrakenPair,
                closeSide,
                remote.Size,
                "take_profit",
                takeProfitPrice,
                triggerSource,
                reduceOnly: true,
                cancellationToken);
            if (takeProfit.Accepted)
            {
                result = result with { TpOrderState = "EXCHANGE_OPEN", ExchangeTakeProfitPrice = takeProfitPrice };
                Console.WriteLine($"futures-tpsl-arm: symbol={instrument.KrakenPair} side={remote.Side} kind=take_profit orderId={takeProfit.OrderId ?? "-"} price={takeProfitPrice:0.########}");
            }
            else
            {
                Console.WriteLine($"futures-tpsl-arm: FAILED symbol={instrument.KrakenPair} side={remote.Side} kind=take_profit price={takeProfitPrice:0.########} rawPrice={tpSl.ExchangeTakeProfitPrice:0.########} reason={takeProfit.Error ?? takeProfit.Status}");
            }
        }

        return result;
    }

    private ImportedTpSlState ImportedTpSl(
        PortfolioPosition? existing,
        string side,
        decimal entryPrice,
        FuturesOpenOrder? existingStopLoss,
        FuturesOpenOrder? existingTakeProfit,
        FuturesOpenOrder? existingTrailingStop)
    {
        var stopDistancePct = config.TpSl.StopLossPercent;
        var takeProfitDistancePct = config.TpSl.TakeProfitPercent;
        var trailingStopState = existingTrailingStop is not null
            ? "EXCHANGE_OPEN"
            : existing?.TrailingStopState;
        var trailingStopPercent = existingTrailingStop?.TrailingStopPercent is > 0m
            ? existingTrailingStop.TrailingStopPercent
            : existing?.TrailingStopPercent ?? (existingTrailingStop is not null ? config.TpSl.TrailingStopPercent : null);
        var trailingStopOrderId = existingTrailingStop is not null
            ? existingTrailingStop.OrderId
            : existing?.TrailingStopOrderId;
        var trailingActivatedAtUtc = existingTrailingStop is not null
            ? existing?.TrailingActivatedAtUtc
            : existing?.TrailingActivatedAtUtc;

        if (!config.TpSl.Enabled || entryPrice <= 0m || stopDistancePct <= 0m || takeProfitDistancePct <= 0m)
        {
            return new ImportedTpSlState(
                existing?.TpOrderState,
                existing?.SlOrderState,
                existing?.StopLossPrice,
                existing?.TakeProfitPrice,
                existing?.StopDistancePct,
                existing?.TakeProfitDistancePct,
                existing?.ExchangeStopLossPrice,
                existing?.ExchangeTakeProfitPrice,
                existing?.ExchangeProtectionMultiplierPercent,
                trailingStopState,
                trailingStopPercent,
                trailingStopOrderId,
                trailingActivatedAtUtc);
        }

        var isShort = side.Equals("SHORT", StringComparison.OrdinalIgnoreCase);
        var hasExistingStopLossPrice = existing?.StopLossPrice is > 0m;
        var hasExistingTakeProfitPrice = existing?.TakeProfitPrice is > 0m;
        var stopLossPrice = hasExistingStopLossPrice
            ? existing!.StopLossPrice!.Value
            : isShort
                ? entryPrice * (1m + stopDistancePct / 100m)
                : entryPrice * (1m - stopDistancePct / 100m);
        var takeProfitPrice = hasExistingTakeProfitPrice
            ? existing!.TakeProfitPrice!.Value
            : isShort
                ? entryPrice * (1m - takeProfitDistancePct / 100m)
                : entryPrice * (1m + takeProfitDistancePct / 100m);
        var effectiveStopDistancePct = hasExistingStopLossPrice
            ? existing?.StopDistancePct is > 0m
                ? existing.StopDistancePct.Value
                : DistancePct(entryPrice, stopLossPrice)
            : stopDistancePct;
        var effectiveTakeProfitDistancePct = hasExistingTakeProfitPrice
            ? existing?.TakeProfitDistancePct is > 0m
                ? existing.TakeProfitDistancePct.Value
                : DistancePct(entryPrice, takeProfitPrice)
            : takeProfitDistancePct;
        var exchangeStopLossPrice = existingStopLoss?.StopPrice is > 0m
            ? existingStopLoss.StopPrice.Value
            : existing?.ExchangeStopLossPrice is > 0m
                ? existing.ExchangeStopLossPrice.Value
                : ExchangeProtectionPrice(entryPrice, side, isTakeProfit: false);
        var exchangeTakeProfitPrice = existingTakeProfit?.StopPrice is > 0m
            ? existingTakeProfit.StopPrice.Value
            : existing?.ExchangeTakeProfitPrice is > 0m
                ? existing.ExchangeTakeProfitPrice.Value
                : ExchangeProtectionPrice(entryPrice, side, isTakeProfit: true);

        return new ImportedTpSlState(
            trailingStopState?.Equals("EXCHANGE_OPEN", StringComparison.OrdinalIgnoreCase) == true
                ? "CANCELLED"
                : existingTakeProfit is not null ? "EXCHANGE_OPEN" : existing?.TpOrderState ?? "SIMULATED_OPEN",
            trailingStopState?.Equals("EXCHANGE_OPEN", StringComparison.OrdinalIgnoreCase) == true
                ? "CANCELLED"
                : existingStopLoss is not null ? "EXCHANGE_OPEN" : existing?.SlOrderState ?? "SIMULATED_OPEN",
            decimal.Round(stopLossPrice, 8),
            decimal.Round(takeProfitPrice, 8),
            effectiveStopDistancePct,
            effectiveTakeProfitDistancePct,
            decimal.Round(exchangeStopLossPrice, 8),
            decimal.Round(exchangeTakeProfitPrice, 8),
            config.TpSl.ExchangeProtectionMultiplierPercent,
            trailingStopState,
            trailingStopPercent,
            trailingStopOrderId,
            trailingActivatedAtUtc);
    }

    private static decimal DistancePct(decimal entryPrice, decimal price) =>
        entryPrice <= 0m || price <= 0m
            ? 0m
            : decimal.Round(Math.Abs(price - entryPrice) / entryPrice * 100m, 6);

    private static (FuturesOpenOrder? StopLossOrder, FuturesOpenOrder? TakeProfitOrder, FuturesOpenOrder? TrailingStopOrder) FindProtectionOrders(
        IReadOnlyList<FuturesOpenOrder> openOrders,
        string symbol,
        string positionSide)
    {
        var closeSide = CloseSide(positionSide);
        var matching = openOrders
            .Where(order => order.ReduceOnly
                && order.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)
                && order.Side.Equals(closeSide, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var stop = matching.FirstOrDefault(order => IsStopLossOrder(order.OrderType));
        var takeProfit = matching.FirstOrDefault(order => IsTakeProfitOrder(order.OrderType));
        var trailingStop = matching.FirstOrDefault(order => IsTrailingStopOrder(order.OrderType));
        return (stop, takeProfit, trailingStop);
    }

    private static bool IsStopLossOrder(string orderType) =>
        orderType.Equals("stp", StringComparison.OrdinalIgnoreCase)
        || orderType.Equals("stop", StringComparison.OrdinalIgnoreCase)
        || orderType.Equals("stop_loss", StringComparison.OrdinalIgnoreCase)
        || orderType.Equals("stop-loss", StringComparison.OrdinalIgnoreCase);

    private static bool IsTakeProfitOrder(string orderType) =>
        orderType.Equals("take_profit", StringComparison.OrdinalIgnoreCase)
        || orderType.Equals("takeprofit", StringComparison.OrdinalIgnoreCase)
        || orderType.Equals("take-profit", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrailingStopOrder(string orderType) =>
        orderType.Equals("trailing_stop", StringComparison.OrdinalIgnoreCase)
        || orderType.Equals("trailingstop", StringComparison.OrdinalIgnoreCase)
        || orderType.Equals("trailing-stop", StringComparison.OrdinalIgnoreCase);

    private static bool TrailingActivationSucceeded(string result) =>
        result.Contains("activated reduce-only trailing", StringComparison.OrdinalIgnoreCase)
        || result.Contains("trailing stop already active", StringComparison.OrdinalIgnoreCase);

    private static string CloseSide(string positionSide) =>
        positionSide.Equals("SHORT", StringComparison.OrdinalIgnoreCase) ? "buy" : "sell";

    private static decimal DistancePercent(decimal entryPrice, decimal triggerPrice) =>
        entryPrice <= 0m ? 0m : Math.Abs(triggerPrice - entryPrice) / entryPrice * 100m;

    private static bool OrderSizeMatchesPosition(FuturesOpenOrder order, PortfolioPosition position)
    {
        if (order.UnfilledSize <= 0m || position.Quantity <= 0m)
        {
            return false;
        }

        var tolerance = Math.Max(0.00000001m, position.Quantity * 0.001m);
        return Math.Abs(order.UnfilledSize - position.Quantity) <= tolerance;
    }

    private static bool IsValidProtectionPair(PortfolioPosition position, decimal stopPrice, decimal takeProfitPrice)
    {
        if (position.EntryPrice <= 0m || stopPrice <= 0m || takeProfitPrice <= 0m)
        {
            return false;
        }

        var isShort = position.Side.Equals("SHORT", StringComparison.OrdinalIgnoreCase);
        return isShort
            ? takeProfitPrice < position.EntryPrice && stopPrice > position.EntryPrice
            : takeProfitPrice > position.EntryPrice && stopPrice < position.EntryPrice;
    }

    private static decimal TakeProfitProgressPct(decimal entryPrice, decimal takeProfitPrice, decimal closeablePrice, string side)
    {
        if (entryPrice <= 0m || takeProfitPrice <= 0m || closeablePrice <= 0m)
        {
            return 0m;
        }

        var isShort = side.Equals("SHORT", StringComparison.OrdinalIgnoreCase);
        var targetDistance = isShort ? entryPrice - takeProfitPrice : takeProfitPrice - entryPrice;
        var travelled = isShort ? entryPrice - closeablePrice : closeablePrice - entryPrice;
        if (targetDistance <= 0m || travelled <= 0m)
        {
            return 0m;
        }

        return decimal.Round(Math.Min(100m, travelled / targetDistance * 100m), 4);
    }

    private decimal ExchangeProtectionPrice(decimal entryPrice, string positionSide, bool isTakeProfit)
    {
        if (entryPrice <= 0m)
        {
            return 0m;
        }

        var baseDistancePct = isTakeProfit
            ? config.TpSl.TakeProfitPercent
            : config.TpSl.StopLossPercent;
        var distancePct = baseDistancePct * Math.Max(0m, config.TpSl.ExchangeProtectionMultiplierPercent) / 100m;
        var isShort = positionSide.Equals("SHORT", StringComparison.OrdinalIgnoreCase);
        if (isTakeProfit)
        {
            return isShort
                ? entryPrice * (1m - distancePct / 100m)
                : entryPrice * (1m + distancePct / 100m);
        }

        return isShort
            ? entryPrice * (1m + distancePct / 100m)
            : entryPrice * (1m - distancePct / 100m);
    }

    private static decimal RoundTriggerPrice(decimal price, string positionSide, bool isTakeProfit, int? priceDecimals)
    {
        var decimals = Math.Clamp(priceDecimals ?? DecimalPlaces(price), 0, 8);
        var factor = 1m;
        for (var i = 0; i < decimals; i++)
        {
            factor *= 10m;
        }

        var scaled = price * factor;
        var roundDown = positionSide.Equals("LONG", StringComparison.OrdinalIgnoreCase) == isTakeProfit;
        return (roundDown ? Math.Floor(scaled) : Math.Ceiling(scaled)) / factor;
    }

    private sealed record ImportedTpSlState(
        string? TpOrderState,
        string? SlOrderState,
        decimal? StopLossPrice,
        decimal? TakeProfitPrice,
        decimal? StopDistancePct,
        decimal? TakeProfitDistancePct,
        decimal? ExchangeStopLossPrice,
        decimal? ExchangeTakeProfitPrice,
        decimal? ExchangeProtectionMultiplierPercent,
        string? TrailingStopState,
        decimal? TrailingStopPercent,
        string? TrailingStopOrderId,
        DateTimeOffset? TrailingActivatedAtUtc);

    private bool IsLiveInstance =>
        config.BotInstance.Id.Equals("live", StringComparison.OrdinalIgnoreCase)
        || config.BotInstance.Id.EndsWith("-live", StringComparison.OrdinalIgnoreCase);

    internal static bool IsExternalFuturesPosition(PortfolioPosition position) =>
        position.Origin is null
            ? false
            : !position.Origin.Equals(PositionOrigins.Bot, StringComparison.OrdinalIgnoreCase);

    private static bool IsBotOwnedFuturesPosition(PortfolioPosition position) =>
        position.Origin?.Equals(PositionOrigins.Bot, StringComparison.OrdinalIgnoreCase) == true;

    internal static FuturesMaxHoldExit EvaluateMaxHoldExit(
        PortfolioPosition position,
        DateTimeOffset utc,
        int maxHoldMinutes,
        decimal minStopProgressPct)
    {
        if (maxHoldMinutes <= 0 || position.OpenedAtUtc is not { } opened || utc - opened < TimeSpan.FromMinutes(maxHoldMinutes))
        {
            return new FuturesMaxHoldExit(false, null);
        }

        if (position.UnrealizedPnlEur >= 0m)
        {
            return new FuturesMaxHoldExit(
                false,
                $"MAX_HOLD healthy hold after {maxHoldMinutes}m: unrealized PnL EUR {position.UnrealizedPnlEur:0.####} >= 0");
        }

        var stopProgressPct = StopProgressPct(position);
        if (stopProgressPct is { } progress && progress < minStopProgressPct)
        {
            return new FuturesMaxHoldExit(
                false,
                $"MAX_HOLD stale-loss hold after {maxHoldMinutes}m: unrealized PnL EUR {position.UnrealizedPnlEur:0.####} < 0, but stop progress {progress:0.##}% < {minStopProgressPct:0.##}%");
        }

        var stopText = stopProgressPct is { } value
            ? $", stop progress {value:0.##}% >= {minStopProgressPct:0.##}%"
            : ", stop progress unavailable";
        return new FuturesMaxHoldExit(
            true,
            $"MAX_HOLD stale-loss close after {maxHoldMinutes}m: unrealized PnL EUR {position.UnrealizedPnlEur:0.####} < 0{stopText}");
    }

    private static decimal? StopProgressPct(PortfolioPosition position)
    {
        var stop = position.StopLossPrice;
        var mark = position.MarkPrice > 0m ? position.MarkPrice.Value : position.LastPrice;
        if (stop is null || position.EntryPrice <= 0m || mark <= 0m)
        {
            return null;
        }

        var stopDistance = position.Side.Equals("SHORT", StringComparison.OrdinalIgnoreCase)
            ? stop.Value - position.EntryPrice
            : position.EntryPrice - stop.Value;
        var adverseMove = position.Side.Equals("SHORT", StringComparison.OrdinalIgnoreCase)
            ? mark - position.EntryPrice
            : position.EntryPrice - mark;
        if (stopDistance <= 0m || adverseMove <= 0m)
        {
            return 0m;
        }

        return decimal.Round(Math.Min(100m, adverseMove / stopDistance * 100m), 4);
    }

    private bool IsMinHoldActive(PortfolioPosition position, DateTimeOffset utc) =>
        config.ExecutionPolicy.MinHoldSeconds > 0
        && position.OpenedAtUtc is { } opened
        && utc - opened < TimeSpan.FromSeconds(config.ExecutionPolicy.MinHoldSeconds);

    private RiskEvaluation EvaluatePortfolioEntryGuards(
        PortfolioState state,
        string pair,
        FuturesDesiredExposure desired,
        DateTimeOffset utc,
        decimal sizedNotionalEur)
    {
        if (desired == FuturesDesiredExposure.Flat)
        {
            return new RiskEvaluation(true, new[] { "no exposure requested" });
        }

        if (state.PendingFuturesOrders.Any(order => order.Pair.Equals(pair, StringComparison.OrdinalIgnoreCase)))
        {
            return new RiskEvaluation(false, new[] { "fill reconciliation pending for this pair; duplicate entry blocked" });
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
        var incrementalNotional = sizedNotionalEur > 0m
            ? sizedNotionalEur
            : config.Futures.DerivedNotionalEur(config.Futures.DefaultLeverage);
        if (config.CorrelationRisk.MaxExposureEurPerGroup > 0m
            && groupExposure + incrementalNotional > config.CorrelationRisk.MaxExposureEurPerGroup)
        {
            return new RiskEvaluation(false, new[] { $"correlation group {group} exposure EUR {groupExposure + incrementalNotional:0.####} exceeds cap EUR {config.CorrelationRisk.MaxExposureEurPerGroup:0.####}" });
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
        var costs = FuturesExecutionCostModel.Estimate(config, desired, marketState.Quote?.FundingRatePercent);
        var leverage = config.Futures.DefaultLeverage;
        var size = FuturesPositionSizer.Size(config, atrPct, costs, leverage);
        size = FuturesPositionSizer.FitToAvailableCollateral(size, config, state, portfolio.UsedMarginEur(state), costs);
        var queueAhead = QueueAheadEur(marketState, desired);
        // Taker IOC model: plan the full risk-sized notional. Live still refreshes the
        // quote and enforces MaxEntryPriceDeviationPct before submit.
        var filledNotional = size.SizedNotionalEur;
        var openRisk = ProjectedConcurrentStopRiskEur(state, markPrice, filledNotional, size.StopDistancePct);
        var shortGate = EvaluateShortGate(desired, signal, btcRegime);

        return new FuturesEntryPlan(
            RequestedNotionalEur: size.SizedNotionalEur,
            FilledNotionalEur: filledNotional,
            AtrPct: size.AtrPct,
            StopDistancePct: size.StopDistancePct,
            TakeProfitDistancePct: size.TakeProfitDistancePct,
            RoundTripCostEstimatePct: costs.RoundTripCostPct,
            ExpectedFundingPct: costs.ExpectedFundingPct,
            QueueAheadEur: decimal.Round(queueAhead, 6),
            MakerFillRate: 1m,
            TimeToFillMs: 0,
            RepegCount: 0,
            OpenRiskEur: openRisk,
            FundingState: FundingState(marketState.Quote?.FundingRatePercent, desired),
            BtcRegimeState: btcRegime.Description,
            ShortAllowed: shortGate.Allowed ? "yes" : $"no: {shortGate.Reason}",
            TargetRiskEur: size.TargetRiskEur,
            SizedNotionalEur: size.SizedNotionalEur,
            RequiredMarginEur: size.RequiredMarginEur,
            EffectiveLeverage: size.EffectiveLeverage,
            ProjectedStopLossEur: size.ProjectedStopLossEur,
            ExecutionCostModel: costs.Model,
            StopSource: size.StopSource,
            NotionalCapReason: size.NotionalCapReason);
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
        var btcAllowsLong = btcRegime.AllowsLongs
            || (desired == FuturesDesiredExposure.Long
                && btcRegime.BlocksLongsDueToRegime
                && signal.Score >= config.Regime.LongOverrideMinScore);
        var btcRegimeState = btcRegime.Description;
        if (btcAllowsLong && !btcRegime.AllowsLongs)
        {
            btcRegimeState = $"{btcRegime.Description}; long override: score {signal.Score:0.##} >= {config.Regime.LongOverrideMinScore:0.##}";
        }

        return new FuturesEntryRiskInputs(
            state,
            desired,
            marketState.Quote?.MarkPrice ?? marketState.LastPrice,
            plan.SizedNotionalEur > 0m ? plan.SizedNotionalEur : plan.RequestedNotionalEur,
            plan.FilledNotionalEur,
            plan.EffectiveLeverage > 0m ? plan.EffectiveLeverage : config.Futures.DefaultLeverage,
            portfolio.UsedMarginEur(state),
            marketState.Quote?.FundingRatePercent,
            plan.AtrPct > 0m ? plan.AtrPct : null,
            plan.StopDistancePct > 0m ? plan.StopDistancePct : null,
            plan.TakeProfitDistancePct > 0m ? plan.TakeProfitDistancePct : null,
            marketState.Quote?.VolumeToday,
            ExitDepthEur(marketState, desired),
            plan.OpenRiskEur,
            btcAllowsLong,
            btcRegimeState,
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
        action.TargetRiskEur = plan.TargetRiskEur;
        action.SizedNotionalEur = plan.SizedNotionalEur;
        action.RequiredMarginEur = plan.RequiredMarginEur;
        action.EffectiveLeverage = plan.EffectiveLeverage;
        action.ProjectedStopLossEur = plan.ProjectedStopLossEur;
        action.ExecutionCostModel = plan.ExecutionCostModel;
        action.StopSource = plan.StopSource;
        action.NotionalCapReason = plan.NotionalCapReason;
        action.RequestedMarginEur = plan.RequiredMarginEur;
        action.RequestedLeverage = plan.EffectiveLeverage;
    }

    // Labels the entry channel for per-channel PnL attribution.
    private string ClassifyEntryChannel(
        bool dipBounce,
        EntryFreshnessResult? freshness,
        LongRangeResult? longRange,
        ShortEntryResult? shortEntry)
    {
        // SHORT channels mirror the LONG ones (breakdown ~ breakout, continuation, reclaim).
        if (shortEntry is { Evaluated: true })
        {
            if (shortEntry.HasFreshBreakdown)
            {
                return "ShortBreakdown";
            }

            var upperZone = shortEntry.Range24hPosition is { } sp
                && sp >= config.Shorts.Min24hRangePositionForShort;
            if (shortEntry.FreshTape && upperZone)
            {
                return "ShortContinuation";
            }

            if (shortEntry.FreshTape
                && shortEntry.ClosePercentile is { } scp
                && scp >= 25m
                && scp <= 75m)
            {
                return "ShortReclaim";
            }

            return "Standard";
        }

        if (dipBounce)
        {
            return "DipBounce";
        }

        if (freshness is { HasFreshBreakout: true })
        {
            return "Breakout";
        }

        var continuationZone = freshness?.PositionIn24hRangePct is { } pos
            && pos >= config.Freshness.FreshContinuationMin24hRangePositionPct;
        if (freshness is { HasFreshUpwardTape: true } && continuationZone && freshness is not { HasFreshBreakout: true })
        {
            return "Continuation";
        }

        // Reclaim: mid close-percentile with fresh tape after a wide wick range —
        // not glued to the 24h low, not a breakout chase.
        if (freshness is { HasFreshUpwardTape: true }
            && longRange?.ClosePercentile is { } closePct
            && closePct >= 25m
            && closePct <= 75m)
        {
            return "Reclaim";
        }

        return "Standard";
    }

    private static void AttachEntryFreshnessDiagnostics(DryRunAction action, EntryFreshnessResult freshness)
    {
        action.EntryFreshnessPositionIn24hRangePct = freshness.PositionIn24hRangePct;
        action.EntryFreshnessDistanceFromRecentHighPct = freshness.DistanceFromRecentHighPct;
        action.EntryFreshnessLastSnapshotStepPct = freshness.LastSnapshotStepPct;
        action.EntryFreshnessShortSnapshotSlopePct = freshness.ShortSnapshotSlopePct;
        action.EntryFreshnessPositiveStepsInLast3 = freshness.PositiveStepsInLast3;
        action.EntryFreshnessIsNearHigh = freshness.IsNearHigh;
        action.EntryFreshnessHasFreshUpwardTape = freshness.HasFreshUpwardTape;
        action.EntryFreshnessHasFreshBreakout = freshness.HasFreshBreakout;
        action.EntryFreshnessBlockReason = freshness.BlockReason;
        action.EntryFreshnessRecentCandleMomentumPct = freshness.RecentCandleMomentumPct;
        action.EntryDistanceFromLocalHighPct = freshness.EntryDistanceFromLocalHighPct;
        action.LocalHighSource = freshness.LocalHighSource;
        action.BreakoutBufferPct = freshness.BreakoutBufferPct;
        action.LivePriceVsSignalClosePct = freshness.LivePriceVsSignalClosePct;
        action.PostFillEntryDistanceFromLocalHighPct = freshness.PostFillEntryDistanceFromLocalHighPct;
        action.PostFillLivePriceVsSignalClosePct = freshness.PostFillLivePriceVsSignalClosePct;
    }

    // Pair momentum minus BTC momentum over the shared candle lookback. Null when either
    // side is unavailable, so a missing measurement never masquerades as weakness.
    internal static decimal? RelativeStrengthPct(EntryFreshnessResult? freshness, BtcRegimeState btcRegime) =>
        freshness?.RecentCandleMomentumPct is { } pairMomentum && btcRegime.RecentChangePct is { } btcMomentum
            ? pairMomentum - btcMomentum
            : null;

    // Veto for a LOW-zone long taken while the BTC regime blocks longs: the pair must be
    // rising on its own AND outperforming BTC, otherwise it is just drifting with a
    // market-wide selloff. Returns null (no veto) whenever the gate is disabled, the
    // entry is not a low-zone long, the regime allows longs, or the data is missing —
    // an unmeasurable pair is never blocked on suspicion.
    private string? EvaluateRelativeStrengthGate(
        LongRangeResult? longRange,
        EntryFreshnessResult? freshness,
        BtcRegimeState btcRegime,
        decimal? relativeStrengthPct)
    {
        if (!config.Regime.RelativeStrengthGateEnabled
            || longRange is not { Evaluated: true, Zone: "LOW" }
            || !btcRegime.BlocksLongsDueToRegime
            || relativeStrengthPct is not { } relative
            || freshness?.RecentCandleMomentumPct is not { } pairMomentum)
        {
            return null;
        }

        if (pairMomentum <= 0m)
        {
            return $"long blocked: low-range entry while BTC regime blocks longs and the pair is not rising on its own ({pairMomentum:0.###}% over the momentum lookback)";
        }

        return relative < config.Regime.MinRelativeStrengthPct
            ? $"long blocked: low-range entry while BTC regime blocks longs and relative strength {relative:0.###}% (pair {pairMomentum:0.###}% vs BTC {btcRegime.RecentChangePct:0.###}%) is below the required {config.Regime.MinRelativeStrengthPct:0.###}%"
            : null;
    }

    private static void AttachLongRangeDiagnostics(DryRunAction action, LongRangeResult longRange)
    {
        action.LongRangeEntryPrice = longRange.EntryPrice;
        action.LongRangeEntryPriceSource = longRange.EntryPriceSource;
        action.LongRangeAbsoluteLow24h = longRange.AbsoluteLow24h;
        action.LongRangeAbsoluteHigh24h = longRange.AbsoluteHigh24h;
        action.LongRangeRobustLow24h = longRange.RobustLow24h;
        action.LongRangeRobustHigh24h = longRange.RobustHigh24h;
        action.LongRange24hSource = longRange.Range24hSource;
        action.LongRange24hSampleCount = longRange.Range24hSampleCount;
        action.LongRange24hPositionRaw = longRange.Range24hPositionRaw;
        action.LongRange24hPosition = longRange.Range24hPosition;
        action.LongRangeMaxPositionForLong = longRange.Max24hRangePositionForLong;
        action.LongRangeDistanceFrom24hLowPct = longRange.DistanceFrom24hLowPct;
        action.LongRangeRisingSnapshotCount = longRange.RisingSnapshotCount;
        action.EntryBlockedBy24hRange = longRange.Blocked;
        action.LongRangeBlockReasonCode = longRange.BlockReasonCode;
        action.LongRangeZone = longRange.Zone;
        action.LongRangeAntiChaseApplied = longRange.AntiChaseApplied;
        action.LongRangeConfirmationsMet = longRange.ConfirmationsMet;
        action.LongRangeConfirmationsRequired = longRange.ConfirmationsRequired;
        action.LongRangeEffectiveMaxDriftPct = longRange.EffectiveMaxDriftPct;
        action.LongRangeAtrPct = longRange.AtrPct;
        action.RangeBasis = longRange.RangeBasis;
        action.ClosePercentile = longRange.ClosePercentile;
        action.RecentSwingPosition = longRange.RecentSwingPosition;
    }

    // SHORT diagnostics reuse the side-agnostic range fields (basis / close-percentile /
    // recent-swing / range-blocked). The detailed short numbers are emitted on the
    // SHORT_ENTRY log line and the block reason is surfaced as the decision reason.
    private static void AttachShortEntryDiagnostics(DryRunAction action, ShortEntryResult shortEntry)
    {
        action.RangeBasis = shortEntry.RangeBasis;
        action.ClosePercentile = shortEntry.ClosePercentile;
        action.RecentSwingPosition = shortEntry.RecentSwingPosition;
        action.EntryBlockedBy24hRange = shortEntry.Blocked;
    }

    private static void AttachExecutionDiagnostics(
        DryRunAction action,
        decimal signalPrice,
        FuturesTickerQuote preSubmit,
        decimal submittedLimitPrice,
        decimal requestedQuantity,
        FuturesOrderResult? order,
        FuturesOrderFill? fill)
    {
        action.SignalPrice = signalPrice;
        action.PreSubmitBid = preSubmit.Bid;
        action.PreSubmitAsk = preSubmit.Ask;
        action.SubmittedLimitPrice = submittedLimitPrice;
        action.RequestedQuantity = requestedQuantity;
        action.ExchangeOrderId = order?.OrderId;
        if (fill is null)
        {
            return;
        }

        action.FilledQuantity = fill.Quantity;
        action.AverageFillPrice = fill.AveragePrice;
        action.ExchangeFillTimestamp = fill.TimestampUtc;
        action.EntryDeviationFromSignalPct = PercentDiff(fill.AveragePrice, signalPrice);
        action.EntryDeviationFromAskPct = PercentDiff(fill.AveragePrice, preSubmit.Ask);
    }

    private static decimal? PercentDiff(decimal value, decimal reference) =>
        reference <= 0m ? null : decimal.Round((value - reference) / reference * 100m, 6);

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

    // Concurrent portfolio heat = PURE stop-distance loss summed over open positions plus
    // the new entry. This matches the MaxConcurrentOpenRisk = TargetRiskEur * MaxPositions
    // budget exactly. Realistic execution/slippage cost is reported per trade by the sizer
    // (PositionSizePlan.ProjectedOpenRiskEur) and bounded by the notional caps; it is NOT
    // added here so the budget stays a clean N-stops figure.
    private decimal ProjectedConcurrentStopRiskEur(
        PortfolioState state,
        decimal markPrice,
        decimal filledNotionalEur,
        decimal stopDistancePct)
    {
        var current = state.Positions.Sum(position => PositionRiskEur(position, markPrice));
        if (filledNotionalEur <= 0m || stopDistancePct <= 0m)
        {
            return current;
        }

        var newRisk = filledNotionalEur * stopDistancePct / 100m;
        return decimal.Round(current + Math.Max(0m, newRisk), 8);
    }

    private decimal PositionRiskEur(PortfolioPosition position, decimal markPrice)
    {
        if (position.StopLossPrice is null or <= 0m || position.EntryPrice <= 0m || position.Quantity <= 0m)
        {
            return config.Risk.MaxConcurrentOpenRisk + 1m;
        }

        return position.Side == "SHORT"
            ? Math.Max(0m, (position.StopLossPrice.Value - markPrice) * position.Quantity)
            : Math.Max(0m, (markPrice - position.StopLossPrice.Value) * position.Quantity);
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
            return new BtcRegimeState(false, false, true, "BTC regime unavailable/stale");
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
        // BTC change over the same lookback the pair momentum uses, so relative strength
        // ("this pair is flying while the market bleeds") is measurable per decision.
        var btcLookback = Math.Max(1, config.Freshness.ContinuationCandleMomentumLookback);
        decimal? btcRecentChangePct = null;
        if (closes.Count > btcLookback)
        {
            var reference = closes[^(btcLookback + 1)];
            btcRecentChangePct = reference > 0m ? (close - reference) / reference * 100m : null;
        }

        return new BtcRegimeState(
            allowsLongs,
            allowsShortRegime,
            !allowsLongs,
            $"close={close:0.####} ma{config.Regime.BtcTrendMa}={ma:0.####} slope={slope:0.####} drawdown{config.Regime.BtcCrashLookback}={drawdown:0.###}% btcChange{btcLookback}={btcRecentChangePct:0.###}% allowsLongs={allowsLongs} allowsShorts={allowsShortRegime}",
            btcRecentChangePct);
    }

    private (bool Allowed, string? Reason) EvaluateShortGate(FuturesDesiredExposure desired, TechnicalSignal signal, BtcRegimeState btcRegime)
    {
        if (desired != FuturesDesiredExposure.Short)
        {
            return (true, null);
        }

        if (!signal.HasBearishStructure || !signal.AllowsShort)
        {
            return (false, "pair bearish signal not confirmed");
        }

        if (signal.ShortScore < config.Shorts.MinShortScore)
        {
            return (false, $"short score {signal.ShortScore:0.##} below {config.Shorts.MinShortScore:0.##}");
        }

        if (!btcRegime.AllowsShorts)
        {
            if (signal.ShortScore >= config.Regime.ShortOverrideMinScore)
            {
                return (true, $"short override: score {signal.ShortScore:0.##} >= {config.Regime.ShortOverrideMinScore:0.##}; {btcRegime.Description}");
            }

            return (false, btcRegime.Description);
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

    private DryRunDecisionRecord BuildDecisionRecord(
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
            ? (riskApproved
                ? "REJECT_NO_FUTURES_SIGNAL"
                : EntryRejection.FromHoldReasonCode(fill.Action.HoldReasonCode) ?? "REJECT_FUTURES_RISK")
            : null,
        SpreadPercent = SpreadPercentOf(marketState),
        HasBullishStructure = signal.HasBullishStructure,
        EmaFullyConfirmed = signal.EmaFullyConfirmed,
        BullishEmaGapPercent = signal.BullishEmaGapPercent,
        EmaGapVelocityPercent = signal.EmaGapVelocityPercent,
        AllowsShort = signal.AllowsShort,
        HasBearishStructure = signal.HasBearishStructure,
        BearishEmaGapPercent = signal.BearishEmaGapPercent,
        ShortScore = signal.ShortScore,
        LongScoreThreshold = config.Strategy.MinimumLongScore,
        ShortScoreThreshold = config.Shorts.MinShortScore,
        MinimumEmaGapPercent = config.Strategy.MinimumEmaGapPercent,
        ShortBaseBlockReasonCode = ShortBaseBlockReasonCode(signal),
        ShortBaseBlockReason = ShortBaseBlockReason(signal),
        PriceActionDirection = priceAction?.Direction,
        PriceActionTrendPercent = priceAction?.TrendPercent
    };

    private string? ShortBaseBlockReasonCode(TechnicalSignal signal)
    {
        if (!signal.HasBearishStructure || signal.AllowsShort)
        {
            return null;
        }

        if (signal.BearishEmaGapPercent is not { } gap || gap < config.Strategy.MinimumEmaGapPercent)
        {
            return "SHORT_EMA_NOT_CONFIRMED";
        }

        return signal.ShortScore < config.Strategy.MinimumLongScore
            ? "SHORT_SCORE_BELOW_SIGNAL_THRESHOLD"
            : "SHORT_DOWNSIDE_CONFIRMATION_MISSING";
    }

    private string? ShortBaseBlockReason(TechnicalSignal signal) => ShortBaseBlockReasonCode(signal) switch
    {
        "SHORT_EMA_NOT_CONFIRMED" =>
            $"bearish EMA gap {signal.BearishEmaGapPercent?.ToString("0.###") ?? "unavailable"}% is below required {config.Strategy.MinimumEmaGapPercent:0.###}%",
        "SHORT_SCORE_BELOW_SIGNAL_THRESHOLD" =>
            $"short score {signal.ShortScore:0.##} is below signal threshold {config.Strategy.MinimumLongScore:0.##}",
        "SHORT_DOWNSIDE_CONFIRMATION_MISSING" =>
            "short score and bearish EMA passed, but none of downside momentum, downside volume, or price-below-trend confirmation passed",
        _ => null
    };

    private DryRunDecisionRecord BuildFastExitDecisionRecord(
        InstrumentMarketState marketState,
        FuturesFillResult fill) => new()
    {
        Pair = marketState.Instrument.Pair,
        Price = marketState.LastPrice,
        FastEma = null,
        SlowEma = null,
        Rsi = null,
        DesiredPosition = string.IsNullOrWhiteSpace(fill.Action.DesiredPosition)
            ? "FLAT"
            : fill.Action.DesiredPosition,
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
        AllowsShort = false,
        HasBearishStructure = false,
        BearishEmaGapPercent = null,
        ShortScore = null,
        LongScoreThreshold = config.Strategy.MinimumLongScore,
        ShortScoreThreshold = config.Shorts.MinShortScore,
        MinimumEmaGapPercent = config.Strategy.MinimumEmaGapPercent,
        ShortBaseBlockReasonCode = null,
        ShortBaseBlockReason = null,
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
            return $"no futures short: bearish EMA structure present but downside confirmation did not clear the short gate; short score {signal.ShortScore:0.##}, long score {signal.Score:0.##}";
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
            ExecutionMode: FuturesExecutionCostModel.TakerIocRoundTrip,
            FillRate: entryDecisions.Count == 0
                ? 0m
                : decimal.Round(entryDecisions.Count(decision => (decision.DryRunAction.MakerFillRate ?? 0m) > 0m) / (decimal)entryDecisions.Count, 4),
            PairsPassedVolume: fullStates.Count(state => (state.Quote?.VolumeToday ?? 0m) >= config.Filters.MinQuoteVolume24h),
            PairsPassedDepth: fullStates.Count(state => ExitDepthEur(state, FuturesDesiredExposure.Long) >= config.Futures.DerivedNotionalEur(config.Futures.DefaultLeverage) * config.Filters.MinExitDepthMultiple),
            OpenRiskEur: stateOpenRisk(decisions),
            BtcRegimeState: btcRegime.Description,
            PairsPassedExitDepth: fullStates.Count(state => ExitDepthEur(state, FuturesDesiredExposure.Long) >= config.Futures.DerivedNotionalEur(config.Futures.DefaultLeverage) * config.Filters.MinExitDepthMultiple),
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
