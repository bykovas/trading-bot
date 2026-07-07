namespace TradingBot.Core.Common;

public sealed class PortfolioState
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

public sealed class DailyRiskState
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

public sealed class PortfolioPosition
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
    public string? ExitMode { get; set; }
    public decimal? EntryAtr { get; set; }
    public decimal? StopLossPrice { get; set; }
    public decimal? TakeProfitPrice { get; set; }
    public decimal? RoundTripCostEstimatePct { get; set; }
    public decimal? ExpectedFundingPct { get; set; }
    public decimal? AtrPct { get; set; }
    public decimal? StopDistancePct { get; set; }
    public decimal? TakeProfitDistancePct { get; set; }

    // Consecutive decision cycles in which the current score sat at or below the
    // configured defensive score level. Reset to 0 whenever the score recovers.
    public int LowScoreCycles { get; set; }

    // Futures-only fields, nullable so spot rows and legacy state files are
    // untouched. For futures positions MarketValueEur carries the position's
    // equity contribution (initial margin + unrealized PnL), not asset value.
    public decimal? Leverage { get; set; }
    public decimal? InitialMarginEur { get; set; }
    public decimal? MarkPrice { get; set; }
    public decimal? LiquidationPrice { get; set; }
    public decimal? LiquidationDistancePercent { get; set; }
    public decimal? FundingPaidEur { get; set; }
    public string? TpOrderState { get; set; }
    public string? SlOrderState { get; set; }

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
        ExitMode = ExitMode,
        EntryAtr = EntryAtr,
        StopLossPrice = StopLossPrice,
        TakeProfitPrice = TakeProfitPrice,
        RoundTripCostEstimatePct = RoundTripCostEstimatePct,
        ExpectedFundingPct = ExpectedFundingPct,
        AtrPct = AtrPct,
        StopDistancePct = StopDistancePct,
        TakeProfitDistancePct = TakeProfitDistancePct,
        LowScoreCycles = LowScoreCycles,
        Leverage = Leverage,
        InitialMarginEur = InitialMarginEur,
        MarkPrice = MarkPrice,
        LiquidationPrice = LiquidationPrice,
        LiquidationDistancePercent = LiquidationDistancePercent,
        FundingPaidEur = FundingPaidEur,
        TpOrderState = TpOrderState,
        SlOrderState = SlOrderState
    };
}

public sealed class PairActionHistory
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
public enum ExitReason
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

public sealed class DryRunAction
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

    // Provenance of the committed fill on WOULD_BUY / WOULD_SELL records:
    // "REAL"    -> exchange-confirmed fill (QueryOrders) was committed;
    // "MODELED" -> the ask/bid+slippage simulation was committed (virtual mode,
    //              validate-only mode, or live fallback when the fill query failed).
    // Null on non-fill records. ModeledFillPrice/ModeledFeeEur keep the simulated
    // numbers alongside a REAL fill so model-vs-exchange drift is queryable per trade.
    public string? FillSource { get; set; }
    public decimal? ModeledFillPrice { get; set; }
    public decimal? ModeledFeeEur { get; set; }
    public decimal? RoundTripCostEstimatePct { get; set; }
    public decimal? ExpectedFundingPct { get; set; }
    public decimal? AtrPct { get; set; }
    public decimal? StopDistancePct { get; set; }
    public decimal? TakeProfitDistancePct { get; set; }
    public decimal? OpenRiskEur { get; set; }
    public decimal? QueueAheadEur { get; set; }
    public decimal? MakerOrderFilledEur { get; set; }
    public decimal? MakerFillRate { get; set; }
    public long? TimeToFillMs { get; set; }
    public int? RepegCount { get; set; }
    public string? FundingState { get; set; }
    public string? BtcRegimeState { get; set; }
    public string? ShortAllowed { get; set; }
    public decimal? RequestedNotionalEur { get; set; }
    public decimal? FilledNotionalEur { get; set; }

    // Futures-only fields, nullable so spot rows keep their exact shape. Side is
    // LONG/SHORT exposure, ReduceOnly marks simulated exits that may only shrink
    // a position, ExitTriggerSource records which price stream fired a TP/SL.
    public string? Side { get; set; }
    public bool? ReduceOnly { get; set; }
    public decimal? Leverage { get; set; }
    public string? ExitTriggerSource { get; set; }
}

public sealed class DryRunCycleRecord
{
    public required string CycleId { get; init; }
    public required string BotInstanceId { get; init; }
    public required string BotInstanceName { get; init; }
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

public sealed class DryRunDecisionRecord
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
    public bool HasBullishStructure { get; init; }
    public bool EmaFullyConfirmed { get; init; }
    public decimal? BullishEmaGapPercent { get; init; }
    public decimal? EmaGapVelocityPercent { get; init; }
    public bool EarlyEntryEligible { get; init; }
    public string? EarlyEntryReason { get; init; }
    public decimal EarlyEntryDiagnosticScore { get; init; }
    public decimal EarlyEntrySuggestedNotionalEur { get; init; }
}
