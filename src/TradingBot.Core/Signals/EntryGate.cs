namespace TradingBot.Core.Signals;

// Compact machine-readable rejection reasons for entry decisioning. One of these is
// attached to every evaluated no-position pair that did NOT become a firm entry, so
// dashboards and cycle summaries never have to parse free text.
public static class EntryRejection
{
    public const string ScoreBelowThreshold = "REJECT_SCORE_BELOW_THRESHOLD";
    public const string SpreadTooWide = "REJECT_SPREAD_TOO_WIDE";
    public const string NegativeRecentPriceAction = "REJECT_NEGATIVE_RECENT_PRICE_ACTION";
    public const string NoVolumeConfirmation = "REJECT_NO_VOLUME_CONFIRMATION";
    public const string Cooldown = "REJECT_COOLDOWN";
    public const string MaxPositions = "REJECT_MAX_POSITIONS";
    public const string DailyRisk = "REJECT_DAILY_RISK";
    public const string AlreadyHolding = "REJECT_ALREADY_HOLDING";
    public const string ActivePairFilter = "REJECT_ACTIVE_PAIR_FILTER";
    public const string SignalDecay = "REJECT_SIGNAL_DECAY";
    public const string LowLiquidity = "REJECT_LOW_LIQUIDITY";
    public const string NoBullishSignal = "REJECT_NO_BULLISH_SIGNAL";
    public const string PairUnavailable = "REJECT_PAIR_UNAVAILABLE";
    public const string NoCapacity = "REJECT_NO_CAPACITY";
    public const string ExploratoryRank = "REJECT_EXPLORATORY_RANK";
    public const string RiskLimits = "REJECT_RISK_LIMITS";
    public const string CyclePositionLimit = "REJECT_CYCLE_POSITION_LIMIT";
    public const string EntryBlackout = "REJECT_ENTRY_BLACKOUT";
    public const string CorrelationLimit = "REJECT_CORRELATION_LIMIT";
    public const string InvalidMarketData = "REJECT_INVALID_MARKET_DATA";
    public const string InsufficientPriceHistory = "REJECT_INSUFFICIENT_PRICE_HISTORY";
    public const string PriceActionUnknown = "REJECT_PRICE_ACTION_UNKNOWN";
    public const string ExploratoryRequiresPositivePriceAction = "REJECT_EXPLORATORY_REQUIRES_POSITIVE_PRICE_ACTION";
    public const string NoMomentumConfirmation = "REJECT_NO_MOMENTUM_CONFIRMATION";
    public const string PriceExtended = "REJECT_PRICE_EXTENDED";
    public const string EarlyEntryRank = "REJECT_EARLY_ENTRY_RANK";
    public const string MarketRegime = "REJECT_MARKET_REGIME";
    public const string FrictionTooHigh = "REJECT_FRICTION_TOO_HIGH";

    // Maps a DryRunPortfolio hold-reason code (a portfolio-level buy block) onto the
    // compact rejection vocabulary for cycle diagnostics.
    public static string? FromHoldReasonCode(string? holdReasonCode) => holdReasonCode switch
    {
        null => null,
        "COOLDOWN_BLOCK" or "STOPLOSS_COOLDOWN" => Cooldown,
        "DAILY_LOSS_BLOCK" => DailyRisk,
        "HOURLY_ENTRY_LIMIT" or "CYCLE_POSITION_LIMIT" => CyclePositionLimit,
        "ENTRY_BLACKOUT" => EntryBlackout,
        "CORRELATION_GROUP_LIMIT" or "CORRELATION_EXPOSURE_LIMIT" or "HIGH_BETA_LIMIT" => CorrelationLimit,
        "EXPLORATORY_RANK" => ExploratoryRank,
        "EARLY_ENTRY_RANK" => EarlyEntryRank,
        "MARKET_REGIME" => MarketRegime,
        "FRICTION_BLOCK" => FrictionTooHigh,
        "MAX_POSITIONS" => MaxPositions,
        _ => RiskLimits
    };
}

// Result of the layered NEW-entry gate. DesiredPosition is LONG_MICRO only when the
// hard safety layer AND the quality layer both pass; Exploratory marks candidates
// admitted at the (dry-run) exploratory threshold rather than the firm one;
// EarlyEntry marks candidates admitted on a forming (not yet fully confirmed) EMA
// cross via the early-entry channel.
public sealed record EntryGateResult(
    string DesiredPosition,
    bool Exploratory,
    string? RejectionReason,
    decimal SpreadPercent,
    IReadOnlyList<SignalContribution> Notes,
    bool EarlyEntry = false);

// Layered decisioning for NEW long entries.
//
//   Layer A - HARD SAFETY FILTERS (pair-level): pair tradability, max spread,
//     minimum liquidity. Portfolio-level hard filters (daily loss cap, max open
//     positions, exposure, cooldowns, min notional) stay in DryRunPortfolio because
//     they need portfolio state; their blocks are mapped onto the same rejection
//     vocabulary by EntryRejection.FromHoldReasonCode.
//
//   Layer B - QUALITY FILTERS: bullish signal present, score threshold (firm or
//     exploratory), anti-lag recent-price-action guard, volume-confirmation rules.
//
//   Layer C - RANKING runs downstream in DecisionWorker: eligible candidates are
//     ranked and the per-cycle/max-open limits are consumed best-first; exploratory
//     candidates additionally must rank in the configured top N.
public static class EntryGate
{
    private static readonly HashSet<string> NonTradableStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "cancel_only",
        "post_only",
        "reduce_only",
        "limit_only",
        "maintenance",
        "delisted",
        "offline",
        "disabled"
    };

    public static EntryGateResult Evaluate(
        TechnicalSignal signal,
        InstrumentMarketState marketState,
        PriceActionAssessment? priceAction,
        StrategyOptions strategy,
        bool liveMode = false,
        IndicatorSnapshot? indicators = null)
    {
        var notes = new List<SignalContribution>();
        var spreadPercent = SpreadPercentOf(marketState);

        // ---- Layer A: hard safety filters ----
        // Live mode is strict on metadata: an entry that would move real money needs
        // pair rules, an explicit "online" status, and a valid bid/ask book. Dry-run
        // stays lenient (synthetic sources have no real metadata) but still rejects
        // KNOWN non-tradable Kraken statuses.
        if (liveMode)
        {
            if (marketState.PairRules is not { } liveRules)
            {
                notes.Add(new SignalContribution("HardFilter", 0m, "live entry rejected: pair metadata (rules/status) is missing"));
                return Reject(EntryRejection.PairUnavailable, spreadPercent, notes);
            }

            if (!liveRules.Status.Equals("online", StringComparison.OrdinalIgnoreCase))
            {
                notes.Add(new SignalContribution("HardFilter", 0m, $"live entry rejected: pair status is '{liveRules.Status}', not online"));
                return Reject(EntryRejection.PairUnavailable, spreadPercent, notes);
            }

            if (marketState.Quote is not { } liveQuote || liveQuote.Bid <= 0m || liveQuote.Ask <= 0m || liveQuote.Ask < liveQuote.Bid)
            {
                notes.Add(new SignalContribution("HardFilter", 0m, "live entry rejected: bid/ask is missing or invalid, real spread cannot be verified"));
                return Reject(EntryRejection.InvalidMarketData, spreadPercent, notes);
            }
        }
        else if (marketState.PairRules is { } rules && NonTradableStatuses.Contains(rules.Status))
        {
            notes.Add(new SignalContribution("HardFilter", 0m, $"entry rejected: pair status is '{rules.Status}', not tradable"));
            return Reject(EntryRejection.PairUnavailable, spreadPercent, notes);
        }

        if (strategy.MaxEntrySpreadPercent > 0m && spreadPercent > strategy.MaxEntrySpreadPercent)
        {
            // Note name "Friction" is kept for continuity with existing dashboards.
            notes.Add(new SignalContribution("Friction", 0m, $"entry skipped: spread {spreadPercent:0.###}% exceeds max {strategy.MaxEntrySpreadPercent:0.###}%"));
            return Reject(EntryRejection.SpreadTooWide, spreadPercent, notes);
        }

        if (strategy.MinDailyVolumeEur > 0m && marketState.Quote is { } quote)
        {
            var dailyVolumeEur = quote.VolumeToday * marketState.LastPrice;
            if (dailyVolumeEur < strategy.MinDailyVolumeEur)
            {
                notes.Add(new SignalContribution("HardFilter", 0m, $"entry rejected: est. 24h volume EUR {dailyVolumeEur:0} below minimum EUR {strategy.MinDailyVolumeEur:0}"));
                return Reject(EntryRejection.LowLiquidity, spreadPercent, notes);
            }
        }

        // ---- Layer B: quality filters ----
        if (!signal.AllowsLong)
        {
            // Early-entry channel: a forming (not yet fully confirmed) EMA cross that
            // is still WIDENING, with RSI in the ideal band and price action no worse
            // than a mild pullback. This is the deliberate answer to buying late: the
            // entry happens while the cross develops instead of 30-60 minutes after
            // the full confirmation stack agrees at the top of the move.
            if (EarlyEntryEligible(signal, priceAction, indicators, strategy))
            {
                var earlyExtension = ExtensionRejection(marketState, priceAction, indicators, strategy, notes);
                if (earlyExtension is not null)
                {
                    return earlyExtension;
                }

                notes.Add(new SignalContribution(
                    "EarlyEntry",
                    0m,
                    $"early entry: forming EMA cross gap {signal.BullishEmaGapPercent:0.###}% widening at {signal.EmaGapVelocityPercent:+0.###;-0.###;0}%/bar, RSI {indicators?.Rsi:0.#} in band, price action {priceAction!.TrendPercent:+0.###;-0.###;0}%; requires top-{strategy.EarlyEntryMaxRank} rank"));
                return new EntryGateResult("LONG_MICRO", false, null, spreadPercent, notes, EarlyEntry: true);
            }

            return Reject(EntryRejection.NoBullishSignal, spreadPercent, notes);
        }

        // Warm-up block: when configured (forced on in live mode), a NEW entry needs a
        // sufficient snapshot history so the anti-lag guard can actually judge it. This
        // closes the restart window where the guard would otherwise silently abstain.
        if (strategy.RequirePriceActionData && priceAction is not { DataSufficient: true })
        {
            var seen = priceAction?.SnapshotCount ?? 0;
            notes.Add(new SignalContribution(
                "PriceActionGuard",
                0m,
                $"entry rejected: price-action history is {PriceActionAssessment.WarmupStateOf(priceAction)} ({seen}/{strategy.PriceActionMinSnapshots} snapshots); UNKNOWN price action never passes for safe"));
            return Reject(EntryRejection.InsufficientPriceHistory, spreadPercent, notes);
        }

        var firm = signal.Score >= strategy.MinimumLongScore;
        var exploratory = false;

        if (!firm)
        {
            // Exploratory admission (dry-run sampling mode): a near-threshold score is
            // allowed through ONLY with KNOWN positive recent price action and a spread
            // within the stricter exploratory limit; ranking downstream additionally
            // requires a top-N slot. Live mode keeps this disabled unless explicitly
            // configured.
            var exploratoryBand =
                strategy.ExploratoryEntriesEnabled
                && signal.Score >= strategy.ExploratoryMinimumLongScore;
            var exploratorySpreadOk =
                strategy.MaxExploratorySpreadPercent <= 0m
                || spreadPercent <= strategy.MaxExploratorySpreadPercent;
            var exploratoryEligible =
                exploratoryBand
                && priceAction is { IsPositive: true }
                && priceAction.TrendPercent >= strategy.ExploratoryMinPriceActionTrendPercent
                && exploratorySpreadOk;

            if (!exploratoryEligible)
            {
                // A near-threshold candidate gets the PRECISE first blocker, not a
                // generic score rejection: unknown price action, non-positive price
                // action, exploratory spread, then missing volume/momentum.
                if (exploratoryBand)
                {
                    if (priceAction is not { DataSufficient: true })
                    {
                        notes.Add(new SignalContribution(
                            "Exploratory",
                            0m,
                            $"exploratory entry refused: price action is {PriceActionAssessment.WarmupStateOf(priceAction)} ({priceAction?.SnapshotCount ?? 0}/{strategy.PriceActionMinSnapshots} snapshots); known POSITIVE price action required"));
                        return Reject(EntryRejection.PriceActionUnknown, spreadPercent, notes);
                    }

                    if (!priceAction.IsPositive)
                    {
                        notes.Add(new SignalContribution("Exploratory", 0m, $"exploratory entry refused: recent price action is {priceAction.Direction}, positive required"));
                        return Reject(EntryRejection.ExploratoryRequiresPositivePriceAction, spreadPercent, notes);
                    }

                    if (priceAction.TrendPercent < strategy.ExploratoryMinPriceActionTrendPercent)
                    {
                        notes.Add(new SignalContribution(
                            "Exploratory",
                            0m,
                            $"exploratory entry refused: recent price action trend {priceAction.TrendPercent:0.###}% is below exploratory minimum {strategy.ExploratoryMinPriceActionTrendPercent:0.###}%"));
                        return Reject(EntryRejection.ExploratoryRequiresPositivePriceAction, spreadPercent, notes);
                    }

                    if (!exploratorySpreadOk)
                    {
                        notes.Add(new SignalContribution("Exploratory", 0m, $"exploratory entry refused: spread {spreadPercent:0.###}% exceeds exploratory max {strategy.MaxExploratorySpreadPercent:0.###}%"));
                        return Reject(EntryRejection.SpreadTooWide, spreadPercent, notes);
                    }
                }

                // A score that only missed the firm bar because the missing-volume cap
                // pulled it down gets the explicit volume rejection, not a generic one.
                if (signal.UncappedScore >= strategy.MinimumLongScore && !signal.VolumeConfirmed)
                {
                    return Reject(EntryRejection.NoVolumeConfirmation, spreadPercent, notes);
                }

                // Near-threshold candidates blocked by a missing confirmation report it
                // explicitly instead of a bare score rejection (also when exploratory
                // mode itself is disabled, e.g. in live mode).
                if (signal.Score >= strategy.ExploratoryMinimumLongScore)
                {
                    if (!HasPositiveContribution(signal, "Momentum"))
                    {
                        return Reject(EntryRejection.NoMomentumConfirmation, spreadPercent, notes);
                    }

                    if (!signal.VolumeConfirmed)
                    {
                        return Reject(EntryRejection.NoVolumeConfirmation, spreadPercent, notes);
                    }
                }

                return Reject(EntryRejection.ScoreBelowThreshold, spreadPercent, notes);
            }

            exploratory = true;
            notes.Add(new SignalContribution(
                "Exploratory",
                0m,
                $"exploratory candidate: score {signal.Score:0.##} >= {strategy.ExploratoryMinimumLongScore:0.##} with {priceAction!.Direction} price action ({priceAction.TrendPercent:0.###}%); requires top-{strategy.ExploratoryMaxRank} rank"));
        }

        // Anti-lag price-action guard: even a firm-threshold score is rejected when the
        // snapshot series shows the price already rolling over. This is the guard that
        // blocks buying a lagging EMA breakout into visible weakness.
        var priceActionRejection = PriceActionGuard.RejectLongEntryReason(priceAction, strategy);
        if (priceActionRejection is not null)
        {
            notes.Add(new SignalContribution("PriceActionGuard", 0m, $"entry rejected: {priceActionRejection}"));
            return Reject(EntryRejection.NegativeRecentPriceAction, spreadPercent, notes);
        }

        // Anti-extension guard: by the time the full confirmation stack agrees, a
        // price that already ran away from its own fast EMA (or gained several
        // percent over the snapshot lookback) is a chase, not an entry.
        var extensionRejection = ExtensionRejection(marketState, priceAction, indicators, strategy, notes);
        if (extensionRejection is not null)
        {
            return extensionRejection;
        }

        return new EntryGateResult("LONG_MICRO", exploratory, null, spreadPercent, notes);
    }

    // Rejects a would-be LONG entry whose price is already extended: either trading
    // MaxEntryExtensionPercent above its own fast EMA, or up more than
    // MaxEntryRunupPercent over the recent snapshot lookback. Returns null when the
    // entry is not extended (or the guards are disabled / lack data).
    private static EntryGateResult? ExtensionRejection(
        InstrumentMarketState marketState,
        PriceActionAssessment? priceAction,
        IndicatorSnapshot? indicators,
        StrategyOptions strategy,
        List<SignalContribution> notes)
    {
        var spreadPercent = SpreadPercentOf(marketState);

        if (strategy.MaxEntryExtensionPercent > 0m
            && indicators?.FastEma is { } fastEma
            && fastEma > 0m
            && marketState.LastPrice > fastEma * (1m + strategy.MaxEntryExtensionPercent / 100m))
        {
            var extensionPercent = (marketState.LastPrice - fastEma) / fastEma * 100m;
            notes.Add(new SignalContribution(
                "ExtensionGuard",
                0m,
                $"entry rejected: price {marketState.LastPrice:0.######} is {extensionPercent:0.###}% above fast EMA {fastEma:0.######}, max extension {strategy.MaxEntryExtensionPercent:0.###}%"));
            return Reject(EntryRejection.PriceExtended, spreadPercent, notes);
        }

        if (strategy.MaxEntryRunupPercent > 0m
            && priceAction is { DataSufficient: true }
            && priceAction.TrendPercent > strategy.MaxEntryRunupPercent)
        {
            notes.Add(new SignalContribution(
                "ExtensionGuard",
                0m,
                $"entry rejected: recent snapshot run-up {priceAction.TrendPercent:0.###}% exceeds max {strategy.MaxEntryRunupPercent:0.###}% (the move already happened)"));
            return Reject(EntryRejection.PriceExtended, spreadPercent, notes);
        }

        return null;
    }

    // Early-entry channel eligibility: a forming bullish EMA cross (gap above the
    // early floor but below full confirmation) that is still widening, with RSI in
    // the ideal band and KNOWN price action no worse than a mild pullback.
    private static bool EarlyEntryEligible(
        TechnicalSignal signal,
        PriceActionAssessment? priceAction,
        IndicatorSnapshot? indicators,
        StrategyOptions strategy)
    {
        return strategy.EarlyEntryEnabled
            && signal.HasBullishStructure
            && signal.Score >= strategy.EarlyEntryMinScore
            && signal.BullishEmaGapPercent is { } gap
            && gap >= strategy.EarlyEntryMinEmaGapPercent
            && signal.EmaGapVelocityPercent is { } velocity
            && velocity > strategy.EarlyEntryMinGapVelocityPercent
            && indicators?.Rsi is { } rsi
            && rsi >= strategy.RsiIdealMin
            && rsi <= strategy.RsiIdealMax
            && priceAction is { DataSufficient: true }
            && priceAction.TrendPercent >= strategy.EarlyEntryMinPriceActionTrendPercent;
    }

    private static bool HasPositiveContribution(TechnicalSignal signal, string name) =>
        signal.Contributions.Any(contribution =>
            contribution.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && contribution.Value > 0m);

    private static EntryGateResult Reject(string reason, decimal spreadPercent, List<SignalContribution> notes) =>
        new("NONE", false, reason, spreadPercent, notes);

    public static decimal SpreadPercentOf(InstrumentMarketState marketState)
    {
        var bid = marketState.BestBid;
        var ask = marketState.BestAsk;
        if (bid <= 0m || ask <= 0m || ask < bid)
        {
            return 0m;
        }

        var mid = (bid + ask) / 2m;
        return mid == 0m ? 0m : (ask - bid) / mid * 100m;
    }
}
