using System.Text.Json;

namespace TradingBot.Worker;

internal sealed class DryRunPortfolio(
    DryRunOptions options,
    PortfolioOptions initialPortfolio,
    ExecutionPolicyOptions executionPolicy,
    PositionExitOptions positionExit,
    PositionSizingOptions positionSizing,
    IDryRunPortfolioStore? store = null,
    IClock? clock = null,
    StrategyOptions? strategy = null,
    CorrelationRiskOptions? correlationRisk = null)
{
    private readonly IDryRunPortfolioStore _store = store ?? new FileDryRunPortfolioStore(options);
    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private readonly StrategyOptions _strategy = strategy ?? new StrategyOptions();
    private readonly CorrelationRiskOptions _correlationRisk = correlationRisk ?? new CorrelationRiskOptions();
    private readonly IReadOnlyDictionary<string, string> _pairToGroup =
        CorrelationRiskResolver.BuildPairToGroup(correlationRisk ?? new CorrelationRiskOptions());
    private readonly ISet<string> _highBetaGroups =
        CorrelationRiskResolver.BuildHighBetaGroups(correlationRisk ?? new CorrelationRiskOptions());

    // True when exit hysteresis is enabled: a held position whose desired flips to NONE
    // then represents a CONFIRMED bearish cross, which unlocks the controlled-loss exit.
    private bool ExitHysteresisEnabled => _strategy.ExitEmaGapPercent > 0m;

    public PortfolioState Load()
    {
        var state = _store.Load();
        if (IsUsable(state))
        {
            state!.Positions ??= new List<PortfolioPosition>();
            state.ActionHistory ??= new List<PairActionHistory>();
            Console.WriteLine(
                $"portfolio-load: reusing existing state from {_store.StateDescription} (cash {state.CashEur:0.##} EUR, positions {state.Positions.Count})");
            return state;
        }

        Console.WriteLine(
            $"portfolio-load: no usable state at {_store.StateDescription}; creating a fresh portfolio with {initialPortfolio.StartingCashEur:0.##} EUR");

        var fresh = CreateInitialState();
        Save(fresh);
        return fresh;
    }

    private PortfolioState CreateInitialState()
    {
        var now = DateTimeOffset.UtcNow;
        return new PortfolioState
        {
            UpdatedAt = now,
            CashEur = initialPortfolio.StartingCashEur,
            Positions = initialPortfolio.Positions
                .Select(position => new PortfolioPosition
                {
                    Pair = position.Pair,
                    Side = position.Side,
                    Quantity = position.Quantity,
                    EntryPrice = position.EntryPrice,
                    EntryNotionalEur = position.EntryNotionalEur,
                    // Config-seeded positions are created now, so their hold clock starts now.
                    OpenedAtUtc = now,
                    LastActionAtUtc = now
                })
                .ToList()
        };
    }

    private static bool IsUsable(PortfolioState? state) =>
        state is not null && (state.CashEur > 0m || state.Positions is { Count: > 0 });

    public void Save(PortfolioState state)
    {
        _store.Save(state);
    }

    public void AppendCycle(DryRunCycleRecord record)
    {
        _store.AppendCycle(record);
    }

    public void AppendMarketSnapshots(IReadOnlyList<MarketSnapshotRecord> snapshots)
    {
        _store.AppendMarketSnapshots(snapshots);
    }

    public string GetStatePath() => _store.StateDescription;
    public string GetEventsPath() => _store.EventsDescription;

    public PortfolioState CloneAndMark(PortfolioState state, IReadOnlyList<InstrumentMarketState> marketStates)
    {
        var clone = state.Clone();
        MarkToMarket(clone, marketStates);
        // Mark-to-market refreshed position values, so the state timestamp must move
        // too — otherwise saved snapshots carry the time of the last fill forever.
        clone.UpdatedAt = DateTimeOffset.UtcNow;
        return clone;
    }

    public DryRunAction Apply(
        PortfolioState state,
        InstrumentMarketState marketState,
        DecisionProposal proposal,
        RiskEvaluation risk,
        RiskOptions riskOptions,
        int newPositionsThisCycle,
        PriceActionAssessment? priceAction = null)
    {
        // The correlation snapshot is resolved for the pair regardless of the decision
        // so its diagnostics land on every record (even holds and sells). It is used to
        // reject BUYs only; exits and holds are never blocked by the correlation layer.
        var correlation = BuildCorrelationSnapshot(state, proposal.Pair);
        var action = ApplyCore(state, marketState, proposal, risk, riskOptions, newPositionsThisCycle, correlation, priceAction);
        action.CorrelationGroup = correlation.Group;
        action.CorrelationGroupOpenPositions = correlation.GroupOpenPositions;
        action.CorrelationGroupExposureEur = correlation.GroupExposureEur;
        return action;
    }

    private DryRunAction ApplyCore(
        PortfolioState state,
        InstrumentMarketState marketState,
        DecisionProposal proposal,
        RiskEvaluation risk,
        RiskOptions riskOptions,
        int newPositionsThisCycle,
        CorrelationSnapshot correlation,
        PriceActionAssessment? priceAction)
    {
        var now = _clock.UtcNow;
        var position = state.Positions.FirstOrDefault(item => item.Pair.Equals(proposal.Pair, StringComparison.OrdinalIgnoreCase));
        var beforeCash = state.CashEur;
        var beforeValue = CalculateTotalValue(state);
        var desiredLong = proposal.DesiredPosition == "LONG_MICRO";

        // A held position is evaluated first. Exit rules (kill switch, stop-loss,
        // take-profit, max-hold, and the soft signal-flip guards) reduce or manage
        // existing risk, so they run before the opening risk gate below and are never
        // blocked by it.
        if (position is not null)
        {
            return ApplyHeldPosition(state, marketState, proposal, riskOptions, position, beforeCash, beforeValue, now, desiredLong, priceAction);
        }

        if (!risk.Approved)
        {
            return BuildAction("REJECTED", "risk rejected proposal", proposal, position, beforeCash, beforeValue, state);
        }

        if (desiredLong)
        {
            // UTC-midnight entry blackout: block NEW entries during the configured
            // window (thin liquidity and meaningless early-UTC change data). Exits and
            // held-position management are unaffected because they never reach here.
            if (IsInEntryBlackout(now, executionPolicy))
            {
                return BuildAction(
                    "WOULD_BUY_BLOCKED",
                    $"entry blackout active: no new entries during the {executionPolicy.EntryBlackoutMinutes} min after {executionPolicy.EntryBlackoutUtcFromHour:00}:00 UTC (now {now.UtcDateTime:HH:mm} UTC)",
                    proposal,
                    position,
                    beforeCash,
                    beforeValue,
                    state,
                    holdReasonCode: "ENTRY_BLACKOUT");
            }

            // Daily loss cap: once today's realized PnL breaches the configured cap,
            // no new risk-increasing entries are allowed until the next UTC day.
            // Exits (stop-loss / take-profit / signal flips) stay unaffected — the cap
            // halts new risk, it never blocks reducing risk.
            var realizedToday = RealizedPnlToday(state, now);
            if (riskOptions.MaxDailyLossEur > 0m && realizedToday <= -riskOptions.MaxDailyLossEur)
            {
                return BuildAction("WOULD_BUY_BLOCKED", $"daily loss cap reached: realized PnL today EUR {realizedToday:0.##} <= -EUR {riskOptions.MaxDailyLossEur:0.##}; new entries blocked until next UTC day", proposal, position, beforeCash, beforeValue, state, holdReasonCode: "DAILY_LOSS_BLOCK");
            }

            // Hourly entry throttle: cap NEW entries per rolling 60 minutes, counted over
            // the BUY timestamps recorded in ActionHistory. 0 disables it.
            if (executionPolicy.MaxNewPositionsPerHour > 0)
            {
                var windowStart = now.AddHours(-1);
                var recentBuys = state.ActionHistory.Count(item => item.LastBuyAtUtc is { } lastBuy && lastBuy > windowStart);
                if (recentBuys >= executionPolicy.MaxNewPositionsPerHour)
                {
                    return BuildAction("WOULD_BUY_BLOCKED", $"hourly entry limit reached: {recentBuys} entries in the last 60 minutes >= max {executionPolicy.MaxNewPositionsPerHour}", proposal, position, beforeCash, beforeValue, state, holdReasonCode: "HOURLY_ENTRY_LIMIT");
                }
            }

            // Pair-level cooldowns: block opening a new position too soon after the
            // previous sell (or buy) for the same pair to avoid rapid re-entry churn.
            var history = state.ActionHistory.FirstOrDefault(item => item.Pair.Equals(proposal.Pair, StringComparison.OrdinalIgnoreCase));
            if (history is not null)
            {
                if (history.LastSellAtUtc is { } lastSell)
                {
                    var sinceSell = (now - lastSell).TotalSeconds;
                    if (sinceSell < executionPolicy.CooldownAfterSellSeconds)
                    {
                        return BuildAction("WOULD_BUY_BLOCKED", $"cooldown after sell active: wait {executionPolicy.CooldownAfterSellSeconds} seconds before re-buying {proposal.Pair} (elapsed {sinceSell:0} s)", proposal, position, beforeCash, beforeValue, state, holdReasonCode: "COOLDOWN_BLOCK");
                    }
                }

                if (history.LastBuyAtUtc is { } lastBuy)
                {
                    var sinceBuy = (now - lastBuy).TotalSeconds;
                    if (sinceBuy < executionPolicy.CooldownAfterBuySeconds)
                    {
                        return BuildAction("WOULD_BUY_BLOCKED", $"cooldown after buy active: wait {executionPolicy.CooldownAfterBuySeconds} seconds before buying {proposal.Pair} again (elapsed {sinceBuy:0} s)", proposal, position, beforeCash, beforeValue, state, holdReasonCode: "COOLDOWN_BLOCK");
                    }
                }

                // Additive per-pair cooldown after a stop-loss. A stop-loss also stamps
                // LastSellAtUtc, so this window (typically longer) extends the plain sell
                // cooldown; plain sells never set LastStopLossAtUtc and keep the sell
                // cooldown only.
                if (executionPolicy.CooldownAfterStopLossSeconds > 0 && history.LastStopLossAtUtc is { } lastStopLoss)
                {
                    var sinceStopLoss = (now - lastStopLoss).TotalSeconds;
                    if (sinceStopLoss < executionPolicy.CooldownAfterStopLossSeconds)
                    {
                        return BuildAction("WOULD_BUY_BLOCKED", $"stop-loss cooldown active: wait {executionPolicy.CooldownAfterStopLossSeconds} seconds before re-buying {proposal.Pair} after a stop-loss (elapsed {sinceStopLoss:0} s)", proposal, position, beforeCash, beforeValue, state, holdReasonCode: "STOPLOSS_COOLDOWN");
                    }
                }
            }

            // Correlation-risk layer: reject when the pair's group is already crowded, or
            // when its group / high-beta exposure would be exceeded. Runs alongside the
            // other opening gates; exits and holds never reach here.
            var correlationBlock = EvaluateCorrelationRejection(proposal, correlation, position, beforeCash, beforeValue, state);
            if (correlationBlock is not null)
            {
                return correlationBlock;
            }

            if (state.Positions.Count >= riskOptions.MaxOpenPositions)
            {
                return BuildAction("WOULD_BUY_BLOCKED", $"max open positions {riskOptions.MaxOpenPositions} already reached", proposal, position, beforeCash, beforeValue, state, holdReasonCode: "MAX_POSITIONS");
            }

            if (executionPolicy.MaxNewPositionsPerCycle > 0 && newPositionsThisCycle >= executionPolicy.MaxNewPositionsPerCycle)
            {
                return BuildAction("WOULD_BUY_BLOCKED", $"max new positions per cycle {executionPolicy.MaxNewPositionsPerCycle} already reached", proposal, position, beforeCash, beforeValue, state, holdReasonCode: "CYCLE_POSITION_LIMIT");
            }

            var currentExposureEur = state.Positions.Sum(item => item.EntryNotionalEur);
            if (riskOptions.MaxTotalExposureEur > 0m && currentExposureEur + proposal.TargetNotionalEur > riskOptions.MaxTotalExposureEur)
            {
                return BuildAction("WOULD_BUY_BLOCKED", $"total exposure EUR {currentExposureEur + proposal.TargetNotionalEur:0.##} would exceed max EUR {riskOptions.MaxTotalExposureEur:0.##}", proposal, position, beforeCash, beforeValue, state, holdReasonCode: "EXPOSURE_LIMIT");
            }

            if (proposal.TargetNotionalEur > state.CashEur)
            {
                return BuildAction("WOULD_BUY_BLOCKED", $"cash EUR {state.CashEur:0.##} is below target EUR {proposal.TargetNotionalEur:0.##}", proposal, position, beforeCash, beforeValue, state, holdReasonCode: "INSUFFICIENT_CASH");
            }

            if (positionSizing.Enabled && state.CashEur - proposal.TargetNotionalEur < positionSizing.CashReserveEur)
            {
                return BuildAction("WOULD_BUY_BLOCKED", $"cash reserve EUR {positionSizing.CashReserveEur:0.##} would be breached by target EUR {proposal.TargetNotionalEur:0.##}", proposal, position, beforeCash, beforeValue, state, holdReasonCode: "CASH_RESERVE_BLOCK");
            }

            if (marketState.LastPrice <= 0m)
            {
                return BuildAction("WOULD_BUY_BLOCKED", "last price is zero", proposal, position, beforeCash, beforeValue, state);
            }

            var buyPrice = CalculateBuyPrice(marketState);
            var feeRate = FeeRate;
            var grossNotional = proposal.TargetNotionalEur / (1m + feeRate);
            var buyFee = proposal.TargetNotionalEur - grossNotional;
            var quantity = decimal.Round(grossNotional / buyPrice, 10);
            if (quantity <= 0m)
            {
                return BuildAction("WOULD_BUY_BLOCKED", "calculated quantity is zero", proposal, position, beforeCash, beforeValue, state);
            }

            var newPosition = new PortfolioPosition
            {
                Pair = proposal.Pair,
                Side = "LONG",
                Quantity = quantity,
                EntryPrice = buyPrice,
                EntryNotionalEur = proposal.TargetNotionalEur,
                LastPrice = marketState.LastPrice,
                MarketValueEur = CalculateLiquidationValue(quantity, marketState),
                OpenedAtUtc = now,
                LastActionAtUtc = now,
                EntryScore = proposal.Score
            };

            if (options.ApplyVirtualFills)
            {
                state.CashEur -= proposal.TargetNotionalEur;
                state.Positions.Add(newPosition);
                RecordAction(state, proposal.Pair, buy: true, now);
                state.UpdatedAt = now;
                MarkToMarket(state, new[] { marketState });
            }

            return BuildAction(
                "WOULD_BUY",
                $"open virtual long: gross EUR {grossNotional:0.####}, fee EUR {buyFee:0.####}, fill ask+slippage {buyPrice:0.####}",
                proposal,
                newPosition,
                beforeCash,
                beforeValue,
                state,
                buyPrice,
                buyFee,
                grossNotional,
                proposal.TargetNotionalEur);
        }

        return BuildAction("NO_ORDER", "no current position and desired is none", proposal, position, beforeCash, beforeValue, state);
    }

    private DryRunAction ApplyHeldPosition(
        PortfolioState state,
        InstrumentMarketState marketState,
        DecisionProposal proposal,
        RiskOptions riskOptions,
        PortfolioPosition position,
        decimal beforeCash,
        decimal beforeValue,
        DateTimeOffset now,
        bool desiredLong,
        PriceActionAssessment? priceAction)
    {
        var canValue = marketState.LastPrice > 0m;
        var pnlPercent = ConservativeUnrealizedPnlPercent(position, marketState, canValue);
        var ageSeconds = PositionAgeSeconds(position, now);
        var scoreDecay = UpdateScoreDecay(position, proposal.Score);

        var evaluation = PositionExitPolicy.EvaluateHeldPosition(
            desiredLong,
            ageSeconds,
            pnlPercent,
            canValue,
            riskOptions.KillSwitch,
            executionPolicy,
            positionExit,
            position.PeakPnlPercent,
            ExitHysteresisEnabled,
            scoreDecay,
            recentPriceActionNegative: priceAction is { DataSufficient: true, TrendPercent: < 0m });

        if (scoreDecay.EntryScore is { } trackedEntryScore && scoreDecay.ConsecutiveLowScoreCycles > 0)
        {
            Console.WriteLine(
                $"score-decay {proposal.Pair}: entry score {trackedEntryScore:0.##} current {scoreDecay.CurrentScore:0.##} lowScoreCycles={scoreDecay.ConsecutiveLowScoreCycles} action={(evaluation.ShouldSell ? "SELL" : "HOLD")}");
        }

        if (!evaluation.ShouldSell)
        {
            return BuildAction(
                "WOULD_HOLD",
                evaluation.Reason,
                proposal,
                position,
                beforeCash,
                beforeValue,
                state,
                holdReasonCode: evaluation.HoldReasonCode);
        }

        var sellPrice = CalculateSellPrice(marketState);
        var grossExitValue = position.Quantity * sellPrice;
        var sellFee = grossExitValue * FeeRate;
        var exitValue = grossExitValue - sellFee;
        var realizedPnl = exitValue - position.EntryNotionalEur;
        var realizedPnlPercent = position.EntryNotionalEur == 0m ? 0m : realizedPnl / position.EntryNotionalEur * 100m;

        if (options.ApplyVirtualFills)
        {
            state.CashEur += exitValue;
            state.Positions.Remove(position);
            RecordAction(state, proposal.Pair, buy: false, now);
            // Stamp the stop-loss time so the post-stop-loss cooldown can gate re-buys.
            if (evaluation.ExitReason == ExitReason.StopLoss)
            {
                RecordStopLoss(state, proposal.Pair, now);
            }
            RecordRealizedPnl(state, realizedPnl, now);
            state.UpdatedAt = now;
            MarkToMarket(state, new[] { marketState });
        }

        var closeDetail = $"close virtual long: gross EUR {grossExitValue:0.####}, fee EUR {sellFee:0.####}, fill bid-slippage {sellPrice:0.####}, realized PnL EUR {realizedPnl:0.####} ({realizedPnlPercent:0.##}%)";
        var exitReasonCode = evaluation.ExitReason is { } reason ? PositionExitPolicy.ExitReasonCode(reason) : "SELL_SIGNAL_FLIP";

        return BuildAction(
            "WOULD_SELL",
            $"{evaluation.Reason} | {closeDetail}",
            proposal,
            position,
            beforeCash,
            beforeValue,
            state,
            sellPrice,
            sellFee,
            grossExitValue,
            exitValue,
            exitReasonCode: exitReasonCode);
    }

    // Updates the per-position consecutive-low-score counter from this cycle's score
    // and returns the snapshot the defensive exit rules consume.
    private ScoreDecaySnapshot UpdateScoreDecay(PortfolioPosition position, decimal currentScore)
    {
        if (positionExit.ScoreDecayDefensiveScore > 0m)
        {
            position.LowScoreCycles = currentScore <= positionExit.ScoreDecayDefensiveScore
                ? position.LowScoreCycles + 1
                : 0;
        }

        return new ScoreDecaySnapshot(
            position.EntryScore,
            currentScore,
            position.LowScoreCycles,
            ScoreConfirmsEntry: currentScore >= _strategy.MinimumLongScore);
    }

    private void MarkToMarket(PortfolioState state, IReadOnlyList<InstrumentMarketState> marketStates)
    {
        foreach (var position in state.Positions)
        {
            var marketState = marketStates.FirstOrDefault(item => item.Instrument.Pair.Equals(position.Pair, StringComparison.OrdinalIgnoreCase));
            if (marketState is null || marketState.LastPrice <= 0m)
            {
                continue;
            }

            position.LastPrice = marketState.LastPrice;
            position.MarketValueEur = CalculateLiquidationValue(position.Quantity, marketState);
            position.UnrealizedPnlEur = position.MarketValueEur - position.EntryNotionalEur;
            position.UnrealizedPnlPercent = position.EntryNotionalEur == 0m
                ? 0m
                : position.UnrealizedPnlEur / position.EntryNotionalEur * 100m;

            // Track the running peak of the conservative PnL for the trailing stop.
            // A legacy position (null peak) simply adopts its current PnL as the first
            // peak, so it cannot trail-fire until it rises and then falls again.
            position.PeakPnlPercent = position.PeakPnlPercent is { } peak
                ? Math.Max(peak, position.UnrealizedPnlPercent)
                : position.UnrealizedPnlPercent;
        }
    }

    // Conservative unrealized PnL percent: values the position as if it had to be
    // liquidated right now at bid - slippage - fee. This is the same figure used for
    // mark-to-market, so stop-loss / take-profit never rely on an optimistic price.
    private decimal ConservativeUnrealizedPnlPercent(PortfolioPosition position, InstrumentMarketState marketState, bool canValue)
    {
        if (!canValue || position.EntryNotionalEur <= 0m)
        {
            return 0m;
        }

        var liquidationValue = CalculateLiquidationValue(position.Quantity, marketState);
        return (liquidationValue - position.EntryNotionalEur) / position.EntryNotionalEur * 100m;
    }

    private static double PositionAgeSeconds(PortfolioPosition position, DateTimeOffset now)
    {
        // Legacy positions loaded from old state files may not carry OpenedAtUtc.
        // We treat those as "old enough" (infinite age) so the minimum-hold guard
        // never suddenly locks a pre-existing position in. This is the least
        // surprising fallback: existing positions keep closing as before, and only
        // positions opened after this feature shipped are held for the minimum.
        if (position.OpenedAtUtc is not { } openedAt)
        {
            return double.MaxValue;
        }

        return (now - openedAt).TotalSeconds;
    }

    // Realized PnL accumulated so far today (UTC). A DailyRisk entry from a previous
    // day counts as zero — the cap resets at UTC midnight. Legacy state files without
    // the field deserialize to null and also count as zero.
    private static decimal RealizedPnlToday(PortfolioState state, DateTimeOffset now)
    {
        var today = now.UtcDateTime.ToString("yyyy-MM-dd");
        return state.DailyRisk?.DateUtc == today ? state.DailyRisk.RealizedPnlEur : 0m;
    }

    private static void RecordRealizedPnl(PortfolioState state, decimal realizedPnl, DateTimeOffset now)
    {
        var today = now.UtcDateTime.ToString("yyyy-MM-dd");
        if (state.DailyRisk is null || state.DailyRisk.DateUtc != today)
        {
            state.DailyRisk = new DailyRiskState { DateUtc = today };
        }

        state.DailyRisk.RealizedPnlEur += realizedPnl;
    }

    private static void RecordAction(PortfolioState state, string pair, bool buy, DateTimeOffset now)
    {
        var history = state.ActionHistory.FirstOrDefault(item => item.Pair.Equals(pair, StringComparison.OrdinalIgnoreCase));
        if (history is null)
        {
            history = new PairActionHistory { Pair = pair };
            state.ActionHistory.Add(history);
        }

        if (buy)
        {
            history.LastBuyAtUtc = now;
        }
        else
        {
            history.LastSellAtUtc = now;
        }
    }

    private static void RecordStopLoss(PortfolioState state, string pair, DateTimeOffset now)
    {
        var history = state.ActionHistory.FirstOrDefault(item => item.Pair.Equals(pair, StringComparison.OrdinalIgnoreCase));
        if (history is null)
        {
            history = new PairActionHistory { Pair = pair };
            state.ActionHistory.Add(history);
        }

        history.LastStopLossAtUtc = now;
    }

    // Correlation snapshot for a candidate pair against the CURRENTLY open positions.
    private CorrelationSnapshot BuildCorrelationSnapshot(PortfolioState state, string pair)
    {
        var group = CorrelationRiskResolver.ResolveGroup(_pairToGroup, pair);
        var isHighBeta = CorrelationRiskResolver.IsHighBeta(_highBetaGroups, group);

        var groupOpenPositions = 0;
        var groupExposureEur = 0m;
        var highBetaOpenPositions = 0;
        var highBetaExposureEur = 0m;

        foreach (var open in state.Positions)
        {
            var openGroup = CorrelationRiskResolver.ResolveGroup(_pairToGroup, open.Pair);
            if (string.Equals(openGroup, group, StringComparison.OrdinalIgnoreCase))
            {
                groupOpenPositions++;
                groupExposureEur += open.EntryNotionalEur;
            }

            if (CorrelationRiskResolver.IsHighBeta(_highBetaGroups, openGroup))
            {
                highBetaOpenPositions++;
                highBetaExposureEur += open.EntryNotionalEur;
            }
        }

        return new CorrelationSnapshot(group, isHighBeta, groupOpenPositions, groupExposureEur, highBetaOpenPositions, highBetaExposureEur);
    }

    // Correlation-risk rejection for a BUY candidate. Returns null when the candidate
    // passes. Order: per-group count, per-group exposure, then (for high-beta groups)
    // high-beta count and high-beta exposure. Every cap uses the 0 = disabled rule.
    private DryRunAction? EvaluateCorrelationRejection(
        DecisionProposal proposal,
        CorrelationSnapshot correlation,
        PortfolioPosition? position,
        decimal beforeCash,
        decimal beforeValue,
        PortfolioState state)
    {
        var target = proposal.TargetNotionalEur;

        if (_correlationRisk.MaxOpenPositionsPerGroup > 0 && correlation.GroupOpenPositions >= _correlationRisk.MaxOpenPositionsPerGroup)
        {
            return CorrelationBlock("CORRELATION_GROUP_LIMIT", $"correlation group {correlation.Group} already holds {correlation.GroupOpenPositions} position(s) >= max {_correlationRisk.MaxOpenPositionsPerGroup}", proposal, correlation, position, beforeCash, beforeValue, state);
        }

        if (_correlationRisk.MaxExposureEurPerGroup > 0m && correlation.GroupExposureEur + target > _correlationRisk.MaxExposureEurPerGroup)
        {
            return CorrelationBlock("CORRELATION_EXPOSURE_LIMIT", $"correlation group {correlation.Group} exposure EUR {correlation.GroupExposureEur + target:0.##} would exceed max EUR {_correlationRisk.MaxExposureEurPerGroup:0.##}", proposal, correlation, position, beforeCash, beforeValue, state);
        }

        if (correlation.IsHighBeta)
        {
            if (_correlationRisk.MaxHighBetaPositions > 0 && correlation.HighBetaOpenPositions >= _correlationRisk.MaxHighBetaPositions)
            {
                return CorrelationBlock("HIGH_BETA_LIMIT", $"high-beta open positions {correlation.HighBetaOpenPositions} >= max {_correlationRisk.MaxHighBetaPositions} (group {correlation.Group})", proposal, correlation, position, beforeCash, beforeValue, state);
            }

            if (_correlationRisk.MaxHighBetaExposureEur > 0m && correlation.HighBetaExposureEur + target > _correlationRisk.MaxHighBetaExposureEur)
            {
                return CorrelationBlock("HIGH_BETA_LIMIT", $"high-beta exposure EUR {correlation.HighBetaExposureEur + target:0.##} would exceed max EUR {_correlationRisk.MaxHighBetaExposureEur:0.##} (group {correlation.Group})", proposal, correlation, position, beforeCash, beforeValue, state);
            }
        }

        return null;
    }

    private static DryRunAction CorrelationBlock(
        string reasonCode,
        string reason,
        DecisionProposal proposal,
        CorrelationSnapshot correlation,
        PortfolioPosition? position,
        decimal beforeCash,
        decimal beforeValue,
        PortfolioState state)
    {
        var action = BuildAction("WOULD_BUY_BLOCKED", $"correlation-risk block: {reason}", proposal, position, beforeCash, beforeValue, state, holdReasonCode: reasonCode);
        action.CorrelationRejectedReason = reasonCode;
        return action;
    }

    private sealed record CorrelationSnapshot(
        string Group,
        bool IsHighBeta,
        int GroupOpenPositions,
        decimal GroupExposureEur,
        int HighBetaOpenPositions,
        decimal HighBetaExposureEur);

    // True when nowUtc falls in [FromHour:00, FromHour:00 + Minutes). Minutes = 0
    // disables the window. Pure/static so it can be unit tested at a fixed clock.
    internal static bool IsInEntryBlackout(DateTimeOffset nowUtc, ExecutionPolicyOptions executionPolicy)
    {
        if (executionPolicy.EntryBlackoutMinutes <= 0)
        {
            return false;
        }

        var utc = nowUtc.UtcDateTime;
        var windowStart = utc.Date.AddHours(executionPolicy.EntryBlackoutUtcFromHour);
        var minutesSinceStart = (utc - windowStart).TotalMinutes;
        return minutesSinceStart >= 0 && minutesSinceStart < executionPolicy.EntryBlackoutMinutes;
    }

    private static decimal CalculateTotalValue(PortfolioState state) =>
        state.CashEur + state.Positions.Sum(position => position.MarketValueEur);

    private decimal FeeRate => options.TakerFeeBps / 10_000m;

    private decimal SlippageRate => options.SlippageBps / 10_000m;

    private decimal CalculateBuyPrice(InstrumentMarketState marketState) =>
        marketState.BestAsk * (1m + SlippageRate);

    private decimal CalculateSellPrice(InstrumentMarketState marketState) =>
        marketState.BestBid * (1m - SlippageRate);

    private decimal CalculateLiquidationValue(decimal quantity, InstrumentMarketState marketState)
    {
        var grossValue = quantity * CalculateSellPrice(marketState);
        return grossValue - grossValue * FeeRate;
    }

    private static DryRunAction BuildAction(
        string action,
        string reason,
        DecisionProposal proposal,
        PortfolioPosition? position,
        decimal beforeCash,
        decimal beforeValue,
        PortfolioState afterState,
        decimal fillPrice = 0m,
        decimal feeEur = 0m,
        decimal grossNotionalEur = 0m,
        decimal netNotionalEur = 0m,
        string? holdReasonCode = null,
        string? exitReasonCode = null)
    {
        var afterValue = CalculateTotalValue(afterState);
        return new DryRunAction
        {
            Pair = proposal.Pair,
            Action = action,
            Reason = reason,
            HoldReasonCode = holdReasonCode,
            ExitReasonCode = exitReasonCode,
            DesiredPosition = proposal.DesiredPosition,
            TargetNotionalEur = proposal.TargetNotionalEur,
            Quantity = action == "WOULD_BUY"
                ? position?.Quantity ?? 0m
                : position?.Quantity ?? 0m,
            EntryPrice = position?.EntryPrice ?? 0m,
            LastPrice = position?.LastPrice ?? 0m,
            FillPrice = fillPrice,
            FeeEur = feeEur,
            GrossNotionalEur = grossNotionalEur,
            NetNotionalEur = netNotionalEur,
            CashBeforeEur = beforeCash,
            CashAfterEur = afterState.CashEur,
            PortfolioValueBeforeEur = beforeValue,
            PortfolioValueAfterEur = afterValue
        };
    }
}

internal sealed class PortfolioState
{
    public DateTimeOffset UpdatedAt { get; set; }
    public decimal CashEur { get; set; }
    public List<PortfolioPosition> Positions { get; set; } = new();

    // Per-pair record of the most recent buy/sell timestamps, used to enforce
    // cooldowns. Old state files that predate this field deserialize to an empty
    // list, so loading legacy state stays safe.
    public List<PairActionHistory> ActionHistory { get; set; } = new();

    // Realized PnL accumulated in the current UTC day, used to enforce the daily
    // loss cap. Null in legacy state files (counts as zero for today).
    public DailyRiskState? DailyRisk { get; set; }

    public decimal PositionsValueEur => Positions.Sum(position => position.MarketValueEur);
    public decimal TotalValueEur => CashEur + PositionsValueEur;

    public PortfolioState Clone() => new()
    {
        UpdatedAt = UpdatedAt,
        CashEur = CashEur,
        Positions = Positions.Select(position => position.Clone()).ToList(),
        ActionHistory = ActionHistory.Select(history => history.Clone()).ToList(),
        DailyRisk = DailyRisk?.Clone()
    };
}

internal sealed class DailyRiskState
{
    // UTC calendar day the counter belongs to, formatted yyyy-MM-dd.
    public string DateUtc { get; set; } = string.Empty;
    public decimal RealizedPnlEur { get; set; }

    public DailyRiskState Clone() => new()
    {
        DateUtc = DateUtc,
        RealizedPnlEur = RealizedPnlEur
    };
}

internal sealed class PortfolioPosition
{
    public string Pair { get; set; } = string.Empty;
    public string Side { get; set; } = "LONG";
    public decimal Quantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal EntryNotionalEur { get; set; }
    public decimal LastPrice { get; set; }
    public decimal MarketValueEur { get; set; }
    public decimal UnrealizedPnlEur { get; set; }
    public decimal UnrealizedPnlPercent { get; set; }

    // Nullable so legacy state files (which lack these fields) still load. When
    // OpenedAtUtc is null the position is treated as "old enough" for the
    // minimum-hold guard (see DryRunPortfolio.PositionAgeSeconds).
    public DateTimeOffset? OpenedAtUtc { get; set; }
    public DateTimeOffset? LastActionAtUtc { get; set; }

    // Highest conservative unrealized PnL percent seen while the position was held,
    // used by the trailing stop. Nullable so legacy state files load; a legacy
    // position only starts tracking once it is next marked to market, so it can never
    // fire the trailing stop until it establishes a fresh peak.
    public decimal? PeakPnlPercent { get; set; }

    // Strategy score at the moment the position was opened, used by the score-decay
    // defensive exit. Null for legacy positions (decay rules then stay inert).
    public decimal? EntryScore { get; set; }

    // Consecutive decision cycles in which the current score sat at or below the
    // configured defensive score level. Reset to 0 whenever the score recovers.
    public int LowScoreCycles { get; set; }

    public PortfolioPosition Clone() => new()
    {
        Pair = Pair,
        Side = Side,
        Quantity = Quantity,
        EntryPrice = EntryPrice,
        EntryNotionalEur = EntryNotionalEur,
        LastPrice = LastPrice,
        MarketValueEur = MarketValueEur,
        UnrealizedPnlEur = UnrealizedPnlEur,
        UnrealizedPnlPercent = UnrealizedPnlPercent,
        OpenedAtUtc = OpenedAtUtc,
        LastActionAtUtc = LastActionAtUtc,
        PeakPnlPercent = PeakPnlPercent,
        EntryScore = EntryScore,
        LowScoreCycles = LowScoreCycles
    };
}

internal sealed class PairActionHistory
{
    public string Pair { get; set; } = string.Empty;
    public DateTimeOffset? LastBuyAtUtc { get; set; }
    public DateTimeOffset? LastSellAtUtc { get; set; }

    // Timestamp of the most recent SELL_STOP_LOSS fill for this pair, used by the
    // per-pair post-stop-loss cooldown. Null in legacy state files (no cooldown).
    public DateTimeOffset? LastStopLossAtUtc { get; set; }

    public PairActionHistory Clone() => new()
    {
        Pair = Pair,
        LastBuyAtUtc = LastBuyAtUtc,
        LastSellAtUtc = LastSellAtUtc,
        LastStopLossAtUtc = LastStopLossAtUtc
    };
}

// Classifies why a LONG position would be closed. Only SignalFlip is produced by
// the current deterministic strategy. The hard-exit reasons below are declared so
// future risk logic can set them; hard exits MUST bypass the minimum-hold guard.
internal enum ExitReason
{
    SignalFlip,
    StopLoss,
    TakeProfit,
    MaxHold,
    TrailingStop,
    KillSwitch,
    EmergencyRisk,
    BrokerSafety,

    // Defensive exits: a high-score entry whose score collapsed (ScoreDecay), or a
    // fresh position that moved adversely while its signal stopped confirming
    // (PostEntryAdverse). Both bypass min-hold / min-profit guards.
    ScoreDecay,
    PostEntryAdverse
}

internal sealed class DryRunAction
{
    public string Pair { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;

    // Distinguishes the WOULD_HOLD / WOULD_BUY_BLOCKED variants:
    // "DESIRED_LONG"    -> holding because the desired position still matches.
    // "MIN_HOLD_BLOCK"  -> a signal-flip sell was suppressed by the minimum-hold guard.
    // "MIN_PROFIT_BLOCK"-> a signal-flip sell was suppressed by the min-profit guard.
    // "COOLDOWN_BLOCK"  -> a buy was suppressed by a buy/sell cooldown.
    // "STOPLOSS_COOLDOWN"      -> a re-buy was suppressed by the post-stop-loss cooldown.
    // "HOURLY_ENTRY_LIMIT"     -> a buy was suppressed by the rolling hourly entry cap.
    // "CORRELATION_GROUP_LIMIT"/"CORRELATION_EXPOSURE_LIMIT"/"HIGH_BETA_LIMIT"
    //                          -> a buy was rejected by the correlation-risk layer.
    // "FLIP_LOSS_FLOOR_BLOCK"  -> a confirmed bearish flip was below the loss floor.
    public string? HoldReasonCode { get; set; }

    // Distinguishes the WOULD_SELL variants: SELL_STOP_LOSS, SELL_TAKE_PROFIT,
    // SELL_MAX_HOLD, SELL_KILL_SWITCH, SELL_SIGNAL_FLIP, ...
    public string? ExitReasonCode { get; set; }

    // Correlation-risk diagnostics (nullable; populated for the resolved pair). These
    // are informational on every record; CorrelationRejectedReason is set only when
    // the correlation layer actually blocked a BUY.
    public string? CorrelationGroup { get; set; }
    public int? CorrelationGroupOpenPositions { get; set; }
    public decimal? CorrelationGroupExposureEur { get; set; }
    public string? CorrelationRejectedReason { get; set; }

    public string DesiredPosition { get; set; } = string.Empty;
    public decimal TargetNotionalEur { get; set; }
    public decimal Quantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal LastPrice { get; set; }
    public decimal FillPrice { get; set; }
    public decimal FeeEur { get; set; }
    public decimal GrossNotionalEur { get; set; }
    public decimal NetNotionalEur { get; set; }
    public decimal CashBeforeEur { get; set; }
    public decimal CashAfterEur { get; set; }
    public decimal PortfolioValueBeforeEur { get; set; }
    public decimal PortfolioValueAfterEur { get; set; }
}

internal sealed class DryRunCycleRecord
{
    public required string CycleId { get; init; }
    public required DateTimeOffset Utc { get; init; }
    public required string MarketDataMode { get; init; }
    public required string AiProvider { get; init; }
    public required WorkerBuildInfo Worker { get; init; }
    public required IReadOnlyList<string> ActivePairs { get; init; }
    public required IReadOnlyList<DryRunDecisionRecord> Decisions { get; init; }
    public required PortfolioState PortfolioBefore { get; init; }
    public required PortfolioState PortfolioAfter { get; init; }

    // Per-cycle entry funnel (candidate counts, top candidates, rejection reasons,
    // excluded pairs and the explicit no-trade reason). Null only in legacy records.
    public CycleEntryDiagnostics? EntryDiagnostics { get; init; }
}

internal sealed class DryRunDecisionRecord
{
    public required string Pair { get; init; }
    public required decimal Price { get; init; }
    public required decimal? FastEma { get; init; }
    public required decimal? SlowEma { get; init; }
    public required decimal? Rsi { get; init; }
    public required string DesiredPosition { get; init; }
    public required decimal Score { get; init; }
    public required bool RiskApproved { get; init; }
    public required IReadOnlyList<string> RiskReasons { get; init; }
    public required IReadOnlyList<SignalContribution> Contributions { get; init; }
    public required DryRunAction DryRunAction { get; init; }

    // Exchange verdict for actionable decisions: VALIDATED_OK / VALIDATE_REJECTED /
    // LIVE_SUBMITTED / LIVE_ERROR / SKIPPED. Null when no broker call was made.
    public string? Broker { get; init; }

    // Compact REJECT_* reason why this pair did not become a firm entry (null for
    // firm entries and held positions), plus entry-quality context.
    public string? EntryRejectionReason { get; init; }
    public decimal SpreadPercent { get; init; }
    public string? PriceActionDirection { get; init; }
    public decimal? PriceActionTrendPercent { get; init; }
    public bool Exploratory { get; init; }
}
