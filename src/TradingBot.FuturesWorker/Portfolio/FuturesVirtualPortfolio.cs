namespace TradingBot.FuturesWorker;

// Margin-ledger virtual portfolio for futures dry-run. NOT a reuse of the spot
// DryRunPortfolio (blueprint hard rule): cash here is margin collateral, a
// position's MarketValueEur is its equity contribution (initial margin +
// unrealized PnL), and SHORT positions are first-class. All simulated exits are
// reduce-only; a flip request is refused, never auto-split into close + open.
internal sealed class FuturesVirtualPortfolio(
    FuturesBotConfiguration config,
    IDryRunPortfolioStore store,
    IClock? clock = null)
{
    private readonly IClock _clock = clock ?? SystemClock.Instance;

    public IDryRunPortfolioStore Store => store;

    public PortfolioState Load() =>
        store.Load() ?? new PortfolioState
        {
            UpdatedAt = _clock.UtcNow,
            CashEur = config.Portfolio.StartingCashEur
        };

    public void Save(PortfolioState state)
    {
        state.UpdatedAt = _clock.UtcNow;
        store.Save(state);
    }

    public decimal UsedMarginEur(PortfolioState state) =>
        state.Positions.Sum(position => position.InitialMarginEur ?? 0m);

    // Applies one desired exposure to the ledger and returns the journal action.
    // reduceOnly requests may only shrink an existing position: a reduce-only
    // SELL never opens a short and a reduce-only BUY never opens a long.
    public FuturesFillResult Apply(
        PortfolioState state,
        string pair,
        FuturesDesiredExposure desired,
        decimal markPrice,
        decimal targetNotionalEur,
        decimal leverage,
        bool reduceOnly = false,
        string reason = "",
        string? exitTriggerSource = null,
        FuturesEntryPlan? entryPlan = null)
    {
        var position = state.Positions.FirstOrDefault(p => p.Pair == pair);
        var desiredSide = desired switch
        {
            FuturesDesiredExposure.Long => "LONG",
            FuturesDesiredExposure.Short => "SHORT",
            _ => null
        };

        if (markPrice <= 0m)
        {
            return NoOrder(state, pair, position, "mark price unavailable");
        }

        // Flat desire on a held position = reduce-only close of that position.
        if (desired == FuturesDesiredExposure.Flat)
        {
            return position is null
                ? NoOrder(state, pair, null, string.IsNullOrEmpty(reason) ? "no position and desired exposure is flat" : reason)
                : Close(state, position, markPrice, reason: string.IsNullOrEmpty(reason) ? "desired exposure flipped to flat" : reason, exitTriggerSource);
        }

        if (position is not null)
        {
            if (position.Side == desiredSide)
            {
                return Hold(state, position, "current position already matches desired exposure");
            }

            if (reduceOnly)
            {
                // Reduce-only against the opposite side closes, never re-opens.
                return Close(state, position, markPrice, reason: "reduce-only close against opposite desire", exitTriggerSource);
            }

            // Opposite desire without reduceOnly would be a flip; forbidden.
            return NoOrder(state, pair, position, $"flip {position.Side} -> {desiredSide} refused: AllowFlip=false");
        }

        if (reduceOnly)
        {
            // Reduce-only with no position must never open exposure.
            return NoOrder(state, pair, null, "reduce-only order with no open position skipped");
        }

        if (desired == FuturesDesiredExposure.Short && !config.Futures.AllowShorts)
        {
            return NoOrder(state, pair, null, "short entry refused: AllowShorts=false");
        }

        return Open(state, pair, desiredSide!, markPrice, targetNotionalEur, leverage, reason, entryPlan);
    }

    private FuturesFillResult Open(
        PortfolioState state,
        string pair,
        string side,
        decimal markPrice,
        decimal notionalEur,
        decimal leverage,
        string reason,
        FuturesEntryPlan? entryPlan)
    {
        var requestedNotionalEur = notionalEur;
        if (entryPlan is not null)
        {
            notionalEur = entryPlan.FilledNotionalEur;
        }

        leverage = Math.Clamp(leverage <= 0m ? 1m : leverage, 1m, config.Futures.MaxLeverage);
        var fillPrice = markPrice;
        var quantity = fillPrice <= 0m ? 0m : notionalEur / fillPrice;
        var fee = notionalEur * config.Fees.MakerPct / 100m;
        var initialMargin = leverage <= 0m ? notionalEur : notionalEur / leverage;

        if (quantity <= 0m)
        {
            return NoOrder(state, pair, null, "quantity resolves to zero");
        }

        if (state.CashEur < initialMargin + fee)
        {
            return NoOrder(state, pair, null, $"insufficient margin: need EUR {initialMargin + fee:0.####}, have EUR {state.CashEur:0.####}");
        }

        var before = Snapshot(state);
        state.CashEur -= initialMargin + fee;

        var liquidationPrice = FuturesMath.EstimateLiquidationPrice(side, fillPrice, leverage, config.Margin.MaintenanceMarginRatePercent);
        var tpDistancePct = entryPlan?.TakeProfitDistancePct ?? config.TpSl.TakeProfitPercent;
        var slDistancePct = entryPlan?.StopDistancePct ?? config.TpSl.StopLossPercent;
        var tpDistance = fillPrice * tpDistancePct / 100m;
        var slDistance = fillPrice * slDistancePct / 100m;
        state.Positions.Add(new PortfolioPosition
        {
            Pair = pair,
            Side = side,
            Quantity = quantity,
            EntryPrice = fillPrice,
            EntryNotionalEur = notionalEur,
            LastPrice = markPrice,
            MarkPrice = markPrice,
            MarketValueEur = initialMargin,
            OpenedAtUtc = _clock.UtcNow,
            LastActionAtUtc = _clock.UtcNow,
            Leverage = leverage,
            InitialMarginEur = initialMargin,
            LiquidationPrice = liquidationPrice,
            LiquidationDistancePercent = FuturesMath.LiquidationDistancePercent(markPrice, liquidationPrice),
            FundingPaidEur = 0m,
            StopLossPrice = config.TpSl.Enabled ? (side == "SHORT" ? fillPrice + slDistance : fillPrice - slDistance) : null,
            TakeProfitPrice = config.TpSl.Enabled ? (side == "SHORT" ? fillPrice - tpDistance : fillPrice + tpDistance) : null,
            EntryAtr = entryPlan?.AtrPct,
            RoundTripCostEstimatePct = entryPlan?.RoundTripCostEstimatePct,
            TpOrderState = config.TpSl.Enabled ? "SIMULATED_OPEN" : null,
            SlOrderState = config.TpSl.Enabled ? "SIMULATED_OPEN" : null
        });

        var action = BaseAction(pair, side == "SHORT" ? "WOULD_OPEN_SHORT" : "WOULD_OPEN_LONG",
            string.IsNullOrEmpty(reason)
                ? $"open virtual {side.ToLowerInvariant()}: notional EUR {notionalEur:0.####}, margin EUR {initialMargin:0.####}, fee EUR {fee:0.####}, fill {fillPrice:0.####}, leverage {leverage:0.#}x"
                : reason);
        action.Side = side;
        action.ReduceOnly = false;
        action.Leverage = leverage;
        action.TargetNotionalEur = notionalEur;
        action.RequestedNotionalEur = requestedNotionalEur;
        action.FilledNotionalEur = notionalEur;
        action.Quantity = quantity;
        action.EntryPrice = fillPrice;
        action.FillPrice = fillPrice;
        action.LastPrice = markPrice;
        action.FeeEur = fee;
        action.GrossNotionalEur = notionalEur;
        action.FillSource = "MODELED_MAKER";
        if (entryPlan is not null)
        {
            action.RoundTripCostEstimatePct = entryPlan.RoundTripCostEstimatePct;
            action.ExpectedFundingPct = entryPlan.ExpectedFundingPct;
            action.AtrPct = entryPlan.AtrPct;
            action.StopDistancePct = entryPlan.StopDistancePct;
            action.TakeProfitDistancePct = entryPlan.TakeProfitDistancePct;
            action.OpenRiskEur = entryPlan.OpenRiskEur;
            action.QueueAheadEur = entryPlan.QueueAheadEur;
            action.MakerOrderFilledEur = entryPlan.FilledNotionalEur;
            action.MakerFillRate = entryPlan.MakerFillRate;
            action.TimeToFillMs = entryPlan.TimeToFillMs;
            action.RepegCount = entryPlan.RepegCount;
            action.FundingState = entryPlan.FundingState;
            action.BtcRegimeState = entryPlan.BtcRegimeState;
            action.ShortAllowed = entryPlan.ShortAllowed;
        }
        StampOpenHistory(state, pair, _clock.UtcNow);
        FillLedger(action, before, state);
        return new FuturesFillResult(action, PositionOpened: true, PositionClosed: false);
    }

    private FuturesFillResult Close(
        PortfolioState state,
        PortfolioPosition position,
        decimal markPrice,
        string reason,
        string? exitTriggerSource)
    {
        var slippage = markPrice * config.DryRun.SlippageBps / 10_000m;
        var fillPrice = position.Side == "SHORT" ? markPrice + slippage : markPrice - slippage;
        var grossExit = fillPrice * position.Quantity;
        var fee = grossExit * config.DryRun.TakerFeeBps / 10_000m;
        var pnl = FuturesMath.UnrealizedPnlEur(position.Side, position.EntryPrice, fillPrice, position.Quantity);

        var before = Snapshot(state);
        state.CashEur += (position.InitialMarginEur ?? 0m) + pnl - fee;
        state.Positions.Remove(position);
        StampCloseHistory(state, position.Pair, _clock.UtcNow, reason.Contains("STOP_LOSS", StringComparison.OrdinalIgnoreCase));

        var action = BaseAction(position.Pair, "WOULD_CLOSE",
            $"{reason}: close virtual {position.Side.ToLowerInvariant()}, fill {fillPrice:0.####}, realized PnL EUR {pnl:0.####}, fee EUR {fee:0.####}");
        action.Side = position.Side;
        action.ReduceOnly = true;
        action.Leverage = position.Leverage;
        action.Quantity = position.Quantity;
        action.EntryPrice = position.EntryPrice;
        action.FillPrice = fillPrice;
        action.LastPrice = markPrice;
        action.FeeEur = fee;
        action.GrossNotionalEur = grossExit;
        action.NetNotionalEur = pnl;
        action.ExitTriggerSource = exitTriggerSource ?? config.TpSl.TriggerSource;
        FillLedger(action, before, state);
        return new FuturesFillResult(action, PositionOpened: false, PositionClosed: true);
    }

    private FuturesFillResult Hold(PortfolioState state, PortfolioPosition position, string reason)
    {
        var action = BaseAction(position.Pair, "WOULD_HOLD", reason);
        action.Side = position.Side;
        action.Leverage = position.Leverage;
        action.Quantity = position.Quantity;
        action.EntryPrice = position.EntryPrice;
        action.LastPrice = position.MarkPrice ?? position.LastPrice;
        FillLedger(action, Snapshot(state), state);
        return new FuturesFillResult(action, PositionOpened: false, PositionClosed: false);
    }

    private FuturesFillResult NoOrder(PortfolioState state, string pair, PortfolioPosition? position, string reason)
    {
        var action = BaseAction(pair, "NO_ORDER", reason);
        action.Side = position?.Side;
        FillLedger(action, Snapshot(state), state);
        return new FuturesFillResult(action, PositionOpened: false, PositionClosed: false);
    }

    public void MarkToMarket(PortfolioState state, string pair, decimal markPrice)
    {
        var position = state.Positions.FirstOrDefault(p => p.Pair == pair);
        if (position is null || markPrice <= 0m)
        {
            return;
        }

        position.LastPrice = markPrice;
        position.MarkPrice = markPrice;
        var pnl = FuturesMath.UnrealizedPnlEur(position.Side, position.EntryPrice, markPrice, position.Quantity);
        position.UnrealizedPnlEur = decimal.Round(pnl, 10);
        position.UnrealizedPnlPercent = position.EntryNotionalEur == 0m
            ? 0m
            : decimal.Round(pnl / position.EntryNotionalEur * 100m, 2);
        position.MarketValueEur = decimal.Round((position.InitialMarginEur ?? 0m) + pnl, 10);
        if (position.LiquidationPrice is { } liquidation)
        {
            position.LiquidationDistancePercent = FuturesMath.LiquidationDistancePercent(markPrice, liquidation);
        }
    }

    private static (decimal Cash, decimal Total) Snapshot(PortfolioState state) =>
        (state.CashEur, state.TotalValueEur);

    private static void FillLedger(DryRunAction action, (decimal Cash, decimal Total) before, PortfolioState state)
    {
        action.CashBeforeEur = before.Cash;
        action.CashAfterEur = state.CashEur;
        action.PortfolioValueBeforeEur = before.Total;
        action.PortfolioValueAfterEur = state.TotalValueEur;
    }

    private static DryRunAction BaseAction(string pair, string kind, string reason) => new()
    {
        Pair = pair,
        Action = kind,
        Reason = reason
    };

    private static void StampOpenHistory(PortfolioState state, string pair, DateTimeOffset now)
    {
        var history = HistoryFor(state, pair);
        history.LastBuyAtUtc = now;
    }

    private static void StampCloseHistory(PortfolioState state, string pair, DateTimeOffset now, bool stopLoss)
    {
        var history = HistoryFor(state, pair);
        history.LastSellAtUtc = now;
        if (stopLoss)
        {
            history.LastStopLossAtUtc = now;
        }
    }

    private static PairActionHistory HistoryFor(PortfolioState state, string pair)
    {
        state.ActionHistory ??= new List<PairActionHistory>();
        var history = state.ActionHistory.FirstOrDefault(item => item.Pair.Equals(pair, StringComparison.OrdinalIgnoreCase));
        if (history is null)
        {
            history = new PairActionHistory { Pair = pair };
            state.ActionHistory.Add(history);
        }

        return history;
    }
}
