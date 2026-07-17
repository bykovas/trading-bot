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
    public List<PendingFuturesOrder> PendingFuturesOrders { get; set; } = new();

    // Realized PnL accumulated in the current UTC day, used to enforce the daily
    // loss cap. Null in legacy state files (counts as zero for today).
    public DailyRiskState? DailyRisk { get; set; }

    // Cumulative P&L from activity NOT attributable to the bot: manual trades,
    // external deposits/withdrawals, or positions the bot doesn't track.
    // Computed each cycle by comparing Kraken balances to the bot's virtual state.
    public decimal ExternalPnlEur { get; set; }

    public decimal PositionsValueEur => Positions.Sum(position => position.MarketValueEur);
    public decimal TotalValueEur => CashEur + PositionsValueEur;

    public PortfolioState Clone() => new()
    {
        UpdatedAt = UpdatedAt,
        CashEur = CashEur,
        Positions = Positions.Select(position => position.Clone()).ToList(),
        ActionHistory = ActionHistory.Select(history => history.Clone()).ToList(),
        PendingFuturesOrders = PendingFuturesOrders.Select(order => order.Clone()).ToList(),
        DailyRisk = DailyRisk?.Clone(),
        ExternalPnlEur = ExternalPnlEur
    };
}

public sealed class PendingFuturesOrder
{
    public string Pair { get; set; } = string.Empty;
    public string? ExchangeOrderId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public decimal? RequestedQuantity { get; set; }
    public decimal? SubmittedLimitPrice { get; set; }

    public PendingFuturesOrder Clone() => new()
    {
        Pair = Pair,
        ExchangeOrderId = ExchangeOrderId,
        CreatedAtUtc = CreatedAtUtc,
        RequestedQuantity = RequestedQuantity,
        SubmittedLimitPrice = SubmittedLimitPrice
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

    // Futures provenance. BOT means the worker opened the position itself.
    // KRAKEN_SYNC means the position was adopted from the exchange during live
    // reconciliation, so soft strategy exits must not assume ownership.
    public string? Origin { get; set; }

    // Which entry channel opened this position: Standard / Continuation / Breakout /
    // DipBounce. Null for legacy positions and spot rows. Carried to the close
    // action so realized-PnL-per-channel is queryable in SQL.
    public string? EntryChannel { get; set; }

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
        SlOrderState = SlOrderState,
        Origin = Origin,
        EntryChannel = EntryChannel
    };
}

public static class PositionOrigins
{
    public const string Bot = "BOT";
    public const string KrakenSync = "KRAKEN_SYNC";
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
    public decimal? EntryFreshnessPositionIn24hRangePct { get; set; }
    public decimal? EntryFreshnessDistanceFromRecentHighPct { get; set; }
    public decimal? EntryFreshnessLastSnapshotStepPct { get; set; }
    public decimal? EntryFreshnessShortSnapshotSlopePct { get; set; }
    public int? EntryFreshnessPositiveStepsInLast3 { get; set; }
    public bool? EntryFreshnessIsNearHigh { get; set; }
    public bool? EntryFreshnessHasFreshUpwardTape { get; set; }
    public bool? EntryFreshnessHasFreshBreakout { get; set; }
    public string? EntryFreshnessBlockReason { get; set; }
    // Recent 15m candle momentum (percent over the freshness lookback) used to
    // qualify continuation and dip-bounce entries. Persisted so a losing entry's
    // momentum context is queryable after the fact.
    public decimal? EntryFreshnessRecentCandleMomentumPct { get; set; }

    // LONG 24h-range guard diagnostics (FuturesLongRangeGuard). Null on SHORT rows and
    // when the guard is disabled. entryPriceSource/range24hSource make the inputs
    // auditable; entryBlockedBy24hRange + block reason code make the verdict queryable.
    public decimal? LongRangeEntryPrice { get; set; }
    public string? LongRangeEntryPriceSource { get; set; }
    public decimal? LongRangeAbsoluteLow24h { get; set; }
    public decimal? LongRangeAbsoluteHigh24h { get; set; }
    public decimal? LongRangeRobustLow24h { get; set; }
    public decimal? LongRangeRobustHigh24h { get; set; }
    public string? LongRange24hSource { get; set; }
    public int? LongRange24hSampleCount { get; set; }
    public decimal? LongRange24hPositionRaw { get; set; }
    public decimal? LongRange24hPosition { get; set; }
    public decimal? LongRangeMaxPositionForLong { get; set; }
    public decimal? LongRangeDistanceFrom24hLowPct { get; set; }
    public int? LongRangeRisingSnapshotCount { get; set; }
    public bool? EntryBlockedBy24hRange { get; set; }
    public string? LongRangeBlockReasonCode { get; set; }

    public decimal? EntryDistanceFromLocalHighPct { get; set; }
    public string? LocalHighSource { get; set; }
    public decimal? BreakoutBufferPct { get; set; }
    public decimal? LivePriceVsSignalClosePct { get; set; }
    public decimal? PostFillEntryDistanceFromLocalHighPct { get; set; }
    public decimal? PostFillLivePriceVsSignalClosePct { get; set; }
    public decimal? SignalPrice { get; set; }
    public decimal? PreSubmitBid { get; set; }
    public decimal? PreSubmitAsk { get; set; }
    public decimal? SubmittedLimitPrice { get; set; }
    public decimal? RequestedQuantity { get; set; }
    public decimal? FilledQuantity { get; set; }
    public decimal? AverageFillPrice { get; set; }
    public decimal? EntryDeviationFromSignalPct { get; set; }
    public decimal? EntryDeviationFromAskPct { get; set; }
    public string? ExchangeOrderId { get; set; }
    public DateTimeOffset? ExchangeFillTimestamp { get; set; }

    // Margin / risk-based sizing telemetry (futures).
    public decimal? RequestedMarginEur { get; set; }
    public decimal? RequestedLeverage { get; set; }
    public decimal? ActualInitialMarginEur { get; set; }
    public decimal? ActualEffectiveLeverage { get; set; }
    public decimal? TargetRiskEur { get; set; }
    public decimal? SizedNotionalEur { get; set; }
    public decimal? RequiredMarginEur { get; set; }
    public decimal? EffectiveLeverage { get; set; }
    public decimal? ProjectedStopLossEur { get; set; }
    public string? ExecutionCostModel { get; set; }
    public string? StopSource { get; set; }
    public string? NotionalCapReason { get; set; }
    public string? RangeBasis { get; set; }
    public decimal? ClosePercentile { get; set; }
    public decimal? RecentSwingPosition { get; set; }

    // Futures-only fields, nullable so spot rows keep their exact shape. Side is
    // LONG/SHORT exposure, ReduceOnly marks simulated exits that may only shrink
    // a position, ExitTriggerSource records which price stream fired a TP/SL.
    public string? Side { get; set; }
    public bool? ReduceOnly { get; set; }
    public decimal? Leverage { get; set; }
    public string? ExitTriggerSource { get; set; }

    // Entry channel that opened (or, on a close, that had opened) the position:
    // Standard / Continuation / Breakout / DipBounce. Null on non-position rows and
    // on spot rows. Enables per-channel win-rate / avg-PnL comparison in SQL.
    public string? EntryChannel { get; set; }

    // The relaxed Dip.MinScore threshold that admitted a DipBounce entry (null on
    // every other channel). Recorded alongside the actual score so a bad dip entry's
    // admission conditions are fully reconstructable from the decision row.
    public decimal? DipBounceMinScoreApplied { get; set; }

    // Spot BUY maker-then-IOC execution telemetry (null on every non-entry row and
    // on futures rows). Kept as a nested object so the flat action shape is unchanged.
    public EntryExecutionDiagnostics? EntryExecution { get; set; }
}

// Full trace of the spot BUY entry attempt: the maker phase, the optional IOC
// taker fallback, and the reconciled final fill. Every field is nullable; only the
// stages that actually ran are populated. FillSource is the coarse provenance:
// MAKER, MAKER_PARTIAL, IOC_FALLBACK, MODELED_MAKER_FILL (virtual/validate-only), or
// NONE (nothing filled).
public sealed class EntryExecutionDiagnostics
{
    public string? ExecutionMode { get; set; }

    public decimal? OriginalMakerBid { get; set; }
    public decimal? OriginalMakerAsk { get; set; }
    public string? MakerOrderId { get; set; }
    public decimal? MakerSubmittedPrice { get; set; }
    public decimal? MakerExecutedVolume { get; set; }
    public decimal? MakerAverageFillPrice { get; set; }
    public decimal? MakerFeeEur { get; set; }
    public long? MakerWaitMilliseconds { get; set; }
    public int? MakerRepegs { get; set; }
    public string? MakerFinalStatus { get; set; }

    public bool? FallbackAttempted { get; set; }
    public decimal? FallbackBid { get; set; }
    public decimal? FallbackAsk { get; set; }
    public decimal? FallbackSpreadPercent { get; set; }
    // Market movement between the maker phase and the fallback, both referenced to the
    // original maker bid: BidMovement = (fallbackBid - originalMakerBid)/originalMakerBid;
    // AskDisplacement = (fallbackAsk - originalMakerBid)/originalMakerBid (what the taker
    // actually pays away from the original passive price). Percent units.
    public decimal? FallbackBidMovementPercent { get; set; }
    public decimal? FallbackAskDisplacementPercent { get; set; }
    public decimal? FallbackMaxAllowedPrice { get; set; }
    public decimal? FallbackSubmittedPrice { get; set; }
    public string? FallbackOrderId { get; set; }
    public decimal? FallbackExecutedVolume { get; set; }
    public decimal? FallbackAverageFillPrice { get; set; }
    public decimal? FallbackFeeEur { get; set; }
    public string? FallbackFinalStatus { get; set; }

    public decimal? FinalExecutedVolume { get; set; }
    public decimal? FinalAverageFillPrice { get; set; }
    public decimal? FinalFeeEur { get; set; }
    public string? FillSource { get; set; }
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
