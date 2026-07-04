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

    // Number of consecutive failed cycles that auto-trips the kill switch. A crash
    // loop with unmonitored stop-losses is far more dangerous than pausing, so after
    // this many back-to-back failures we halt new orders and let the operator look.
    private const int MaxConsecutiveCycleFailures = 5;
    private int _consecutiveCycleFailures;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        PrintStartup();
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
        var entryIndex = 0;
        foreach (var marketState in entryStates)
        {
            var prepared = PrepareDecision(marketState, workingPortfolio);
            if (prepared is null)
            {
                entryIndex++;
                continue;
            }

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

        foreach (var ranked in EntryRanking.Rank(buyCandidates.Select(candidate => candidate.Rank)))
        {
            var prepared = buyCandidates.First(candidate => ReferenceEquals(candidate.Rank, ranked)).Prepared;

            if (config.ExecutionPolicy.MaxNewPositionsPerCycle > 0
                && newPositionsThisCycle >= config.ExecutionPolicy.MaxNewPositionsPerCycle)
            {
                decisionRecords.Add(BuildSkippedBuyRecord(prepared, workingPortfolio));
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
                PortfolioAfter = portfolioAfter
            });
            Console.WriteLine($"dry-run-written state={dryRunPortfolio.GetStatePath()} events={dryRunPortfolio.GetEventsPath()}");
        }
    }

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
        RiskEvaluation Risk);

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
        var proposal = decisionEngine.Decide(marketState, indicators, config.Trading, config.Strategy, config.PositionSizing, config.Risk, portfolio.CashEur, currentExposureEur, hasOpenPosition);
        var risk = riskManager.Evaluate(proposal, config.Risk, hasOpenPosition);
        return new PreparedDecision(marketState, indicators, proposal, risk);
    }

    private static decimal BullishEmaGapPercent(IndicatorSnapshot indicators)
    {
        if (indicators.FastEma is not { } fast || indicators.SlowEma is not { } slow || slow == 0m || fast <= slow)
        {
            return 0m;
        }

        return (fast - slow) / slow * 100m;
    }

    // Record for a ranked BUY candidate that lost the per-cycle entry race to
    // higher-ranked candidates. No portfolio mutation, no broker call.
    private DryRunDecisionRecord BuildSkippedBuyRecord(PreparedDecision prepared, PortfolioState portfolio)
    {
        const string reason = "buy candidate skipped because higher-ranked candidates consumed max new positions per cycle";
        Console.WriteLine($"decision {prepared.Proposal.Pair}:");
        Console.WriteLine($"  desired={prepared.Proposal.DesiredPosition} score={prepared.Proposal.Score:0.##} targetEur={prepared.Proposal.TargetNotionalEur:0.##}");
        Console.WriteLine("  execution=WOULD_BUY_BLOCKED");
        Console.WriteLine("  execution-hold-reason-code: CYCLE_POSITION_LIMIT");
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
                HoldReasonCode = "CYCLE_POSITION_LIMIT",
                DesiredPosition = prepared.Proposal.DesiredPosition,
                TargetNotionalEur = prepared.Proposal.TargetNotionalEur,
                CashBeforeEur = portfolio.CashEur,
                CashAfterEur = portfolio.CashEur,
                PortfolioValueBeforeEur = portfolio.TotalValueEur,
                PortfolioValueAfterEur = portfolio.TotalValueEur
            },
            Broker = null
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
        var dryRunAction = dryRunPortfolio.Apply(portfolio, marketState, proposal, risk, config.Risk, newPositionsThisCycle);

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

        var brokerVerdict = await RunBrokerAsync(marketState, dryRunAction, cancellationToken);
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
            Broker = brokerVerdict
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
