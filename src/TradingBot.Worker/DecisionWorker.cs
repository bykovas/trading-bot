namespace TradingBot.Worker;

internal sealed class DecisionWorker(
    BotConfiguration config,
    IMarketDataSource marketDataSource,
    IWatchlistAdvisor watchlistAdvisor,
    IndicatorEngine indicatorEngine,
    TechnicalDecisionEngine decisionEngine,
    RiskManager riskManager,
    DryRunPortfolio dryRunPortfolio,
    KrakenBroker? broker)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        PrintStartup();
        await PrintBrokerStartupAsync(cancellationToken);

        do
        {
            await RunCycleAsync(cancellationToken);
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

        var candidates = await marketDataSource.GetMarketStatesAsync(
            config.CandidateUniverse,
            config.Trading.TimeframeMinutes,
            cancellationToken);

        PrintCandidates(candidates);

        var loadedPortfolio = dryRunPortfolio.Load();
        var workingPortfolio = dryRunPortfolio.CloneAndMark(loadedPortfolio, candidates);
        var portfolioBefore = workingPortfolio.Clone();
        PrintPortfolio("portfolio-before", portfolioBefore);

        var maxRecommendations = Math.Min(config.Trading.MaxActiveInstruments, config.Ai.MaxRecommendations);
        var advice = await watchlistAdvisor.SelectAsync(candidates, maxRecommendations, cancellationToken);
        PrintWatchlistAdvice(advice);

        var selected = advice.Recommendations
            .Select(recommendation => candidates.FirstOrDefault(candidate => candidate.Instrument.Pair.Equals(recommendation.Pair, StringComparison.OrdinalIgnoreCase)))
            .Where(candidate => candidate is not null)
            .Cast<InstrumentMarketState>()
            .ToList();

        ForceOpenPositionsIntoEvaluation(selected, candidates, portfolioBefore);

        var decisionRecords = new List<DryRunDecisionRecord>();
        foreach (var marketState in selected)
        {
            var record = await PrintDecisionAsync(marketState, workingPortfolio, cancellationToken);
            if (record is not null)
            {
                decisionRecords.Add(record);
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
                ActivePairs = selected.Select(candidate => candidate.Instrument.Pair).ToList(),
                Decisions = decisionRecords,
                PortfolioBefore = portfolioBefore,
                PortfolioAfter = portfolioAfter
            });
            Console.WriteLine($"dry-run-written state={dryRunPortfolio.GetStatePath()} events={dryRunPortfolio.GetEventsPath()}");
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

    private static void PrintCandidates(IReadOnlyList<InstrumentMarketState> candidates)
    {
        Console.WriteLine("candidate-universe:");
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

    private static void ForceOpenPositionsIntoEvaluation(
        List<InstrumentMarketState> selected,
        IReadOnlyList<InstrumentMarketState> candidates,
        PortfolioState portfolio)
    {
        foreach (var position in portfolio.Positions)
        {
            if (selected.Any(candidate => candidate.Instrument.Pair.Equals(position.Pair, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var candidate = candidates.FirstOrDefault(item => item.Instrument.Pair.Equals(position.Pair, StringComparison.OrdinalIgnoreCase));
            if (candidate is not null)
            {
                selected.Add(candidate);
                Console.WriteLine($"watchlist-forced {position.Pair}: open position must be evaluated even if advisor did not select it");
            }
            else
            {
                Console.WriteLine($"warning: open position {position.Pair} is not present in CandidateUniverse; cannot evaluate exit");
            }
        }
    }

    private async Task<DryRunDecisionRecord?> PrintDecisionAsync(
        InstrumentMarketState marketState,
        PortfolioState portfolio,
        CancellationToken cancellationToken)
    {
        if (!marketState.IsUsable)
        {
            Console.WriteLine($"decision {marketState.Instrument.Pair}: skipped unusable data");
            return null;
        }

        var indicators = indicatorEngine.Calculate(marketState.Candles, config.Strategy);
        var proposal = decisionEngine.Decide(marketState, indicators, config.Trading, config.Strategy, config.PositionSizing, config.Risk, portfolio.CashEur);
        var risk = riskManager.Evaluate(proposal, config.Risk);
        var currentPositionBeforeAction = portfolio.Positions.FirstOrDefault(position => position.Pair.Equals(proposal.Pair, StringComparison.OrdinalIgnoreCase))?.Clone();
        var dryRunAction = dryRunPortfolio.Apply(portfolio, marketState, proposal, risk, config.Risk);

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
