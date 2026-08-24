using System.Text.Json;

namespace TradingBot.FuturesWorker;

internal sealed class FuturesBotConfiguration
{
    public BotInstanceOptions BotInstance { get; set; } = new();
    public WorkerOptions Worker { get; set; } = new();
    public HttpOptions Http { get; set; } = new();
    public KrakenOptions Kraken { get; set; } = new()
    {
        // Kraken Futures is a separate venue with its own host and auth scheme.
        MarketDataMode = "sample",
        BaseUrl = "https://futures.kraken.com"
    };
    public LoggingOptions Logging { get; set; } = new();
    public TradingOptions Trading { get; set; } = new();
    public StrategyOptions Strategy { get; set; } = new();
    public FuturesOptions Futures { get; set; } = new();
    public MarginOptions Margin { get; set; } = new();
    public FuturesFeesOptions Fees { get; set; } = new();
    public FundingOptions Funding { get; set; } = new();
    public FuturesEntryOptions Entry { get; set; } = new();
    public FuturesFreshnessOptions Freshness { get; set; } = new();
    public FuturesDipBounceOptions Dip { get; set; } = new();
    public FuturesFilterOptions Filters { get; set; } = new();
    public FuturesExitOptions Exits { get; set; } = new();
    public FuturesRegimeOptions Regime { get; set; } = new();
    public FuturesShortOptions Shorts { get; set; } = new();
    public FuturesRiskOptions Risk { get; set; } = new();
    public FuturesExecutionPolicyOptions ExecutionPolicy { get; set; } = new();
    public FuturesEntryMirrorOptions EntryMirror { get; set; } = new();

    public TelegramNotificationOptions Telegram { get; set; } = new();
    public CorrelationRiskOptions CorrelationRisk { get; set; } = new();
    public TpSlOptions TpSl { get; set; } = new();
    public FuturesPortfolioOptions Portfolio { get; set; } = new();
    public DryRunOptions DryRun { get; set; } = new();
    public DatabaseOptions Database { get; set; } = new();
    public MarketDataConsumerOptions MarketDataConsumer { get; set; } = new() { Venue = MarketDataVenue.Futures };
    public UniverseDiscoveryOptions UniverseDiscovery { get; set; } = new();
    public List<InstrumentOptions> CandidateUniverse { get; set; } = new();

    public static FuturesBotConfiguration Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            path = Path.Combine("src", "TradingBot.FuturesWorker", "appsettings.json");
        }

        var config = File.Exists(path)
            ? JsonSerializer.Deserialize<FuturesBotConfiguration>(
                  File.ReadAllText(path),
                  new JsonSerializerOptions
                  {
                      PropertyNameCaseInsensitive = true,
                      ReadCommentHandling = JsonCommentHandling.Skip,
                      AllowTrailingCommas = true
                  }) ?? new FuturesBotConfiguration()
            : new FuturesBotConfiguration();

        ApplyEnvironment(config);
        config.Normalize();
        return config;
    }

    private static void ApplyEnvironment(FuturesBotConfiguration config)
    {
        SetIfPresent("TRADINGBOT_BOT_INSTANCE_ID", value => config.BotInstance.Id = value);
        SetIfPresent("TRADINGBOT_BOT_INSTANCE_NAME", value => config.BotInstance.Name = value);
        SetIfPresent("TRADINGBOT_MARKET_DATA_MODE", value => config.Kraken.MarketDataMode = value);
        SetIfPresent("TRADINGBOT_KRAKEN_BASE_URL", value => config.Kraken.BaseUrl = value);
        SetIfPresent("TRADINGBOT_LOOP_INTERVAL_SECONDS", value => config.Worker.LoopIntervalSeconds = ParseInt(value, config.Worker.LoopIntervalSeconds));
        SetIfPresent("TRADINGBOT_RUN_ONCE", value => config.Worker.RunOnce = ParseBool(value, config.Worker.RunOnce));
        SetIfPresent("TRADINGBOT_TIMEFRAME_MINUTES", value => config.Trading.TimeframeMinutes = ParseInt(value, config.Trading.TimeframeMinutes));
        SetIfPresent("TRADINGBOT_MAX_ACTIVE_INSTRUMENTS", value => config.Trading.MaxActiveInstruments = ParseInt(value, config.Trading.MaxActiveInstruments));
        SetIfPresent("TRADINGBOT_STRONG_MOVER_MIN_CHANGE_PERCENT", value => config.Trading.StrongMoverMinChangePercent = ParseDecimal(value, config.Trading.StrongMoverMinChangePercent));
        SetIfPresent("TRADINGBOT_STRONG_MOVER_MIN_DAILY_VOLUME_EUR", value => config.Trading.StrongMoverMinDailyVolumeEur = ParseDecimal(value, config.Trading.StrongMoverMinDailyVolumeEur));
        SetIfPresent("TRADINGBOT_LOG_DIRECTORY", value => config.Logging.Directory = value);
        SetIfPresent("TRADINGBOT_DATABASE_ENABLED", value => config.Database.Enabled = ParseBool(value, config.Database.Enabled));
        SetIfPresent("TRADINGBOT_DATABASE_CONNECTION_STRING", value => config.Database.ConnectionString = value);
        SetIfPresent("TRADINGBOT_MARKET_DATA_FALLBACK_ENABLED", value => config.MarketDataConsumer.FallbackEnabled = ParseBool(value, config.MarketDataConsumer.FallbackEnabled));
        SetIfPresent("TRADINGBOT_MARKET_DATA_MAX_QUOTE_AGE_SECONDS", value => config.MarketDataConsumer.MaxQuoteAgeSeconds = ParseInt(value, config.MarketDataConsumer.MaxQuoteAgeSeconds));
        SetIfPresent("TRADINGBOT_MARKET_DATA_MAX_CANDLE_AGE_MINUTES", value => config.MarketDataConsumer.MaxCandleAgeMinutes = ParseInt(value, config.MarketDataConsumer.MaxCandleAgeMinutes));
        SetIfPresent("TRADINGBOT_UNIVERSE_DISCOVERY_ENABLED", value => config.UniverseDiscovery.Enabled = ParseBool(value, config.UniverseDiscovery.Enabled));
        SetIfPresent("TRADINGBOT_UNIVERSE_DISCOVERY_REFRESH_SECONDS", value => config.UniverseDiscovery.RefreshSeconds = ParseInt(value, config.UniverseDiscovery.RefreshSeconds));
        SetIfPresent("TRADINGBOT_UNIVERSE_INCLUDE_CONFIGURED", value => config.UniverseDiscovery.IncludeConfiguredUniverse = ParseBool(value, config.UniverseDiscovery.IncludeConfiguredUniverse));
        SetIfPresent("TRADINGBOT_UNIVERSE_FORCE_INCLUDE", value => config.UniverseDiscovery.ForceInclude = ParseCsv(value));
        SetIfPresent("TRADINGBOT_UNIVERSE_BLACKLIST", value => config.UniverseDiscovery.Blacklist = ParseCsv(value));
        SetIfPresent("TRADINGBOT_FUTURES_MAX_LEVERAGE", value => config.Futures.MaxLeverage = ParseDecimal(value, config.Futures.MaxLeverage));
        SetIfPresent("TRADINGBOT_FUTURES_DEFAULT_LEVERAGE", value => config.Futures.DefaultLeverage = ParseDecimal(value, config.Futures.DefaultLeverage));
        SetIfPresent("TRADINGBOT_FUTURES_BACKFILL_CLOSURE_DAYS", value => config.Futures.BackfillClosureDays = ParseInt(value, config.Futures.BackfillClosureDays));
        SetIfPresent("TRADINGBOT_FUTURES_OWN_SIGNAL_ENTRIES_ENABLED", value => config.Futures.OwnSignalEntriesEnabled = ParseBool(value, config.Futures.OwnSignalEntriesEnabled));
        SetIfPresent("TRADINGBOT_FUTURES_MAX_POSITIONS", value => config.Futures.MaxPositions = ParseInt(value, config.Futures.MaxPositions));
        SetIfPresent("TRADINGBOT_FUTURES_ALLOW_SHORTS", value => config.Futures.AllowShorts = ParseBool(value, config.Futures.AllowShorts));
        SetIfPresent("TRADINGBOT_FUTURES_FLIP_LONG_ENTRIES", value => config.Futures.FlipLongEntries = ParseBool(value, config.Futures.FlipLongEntries));
        SetIfPresent("TRADINGBOT_FUTURES_STOP_ATR_MULT", value => config.Exits.StopAtrMult = ParseDecimal(value, config.Exits.StopAtrMult));
        SetIfPresent("TRADINGBOT_FUTURES_STOP_DISTANCE_CAP_PERCENT", value => config.Exits.StopDistanceCapPct = ParseDecimal(value, config.Exits.StopDistanceCapPct));
        SetIfPresent("TRADINGBOT_FUTURES_MIN_REWARD_RISK_MULTIPLE", value => config.Exits.MinRewardRiskMultiple = ParseDecimal(value, config.Exits.MinRewardRiskMultiple));
        SetIfPresent("TRADINGBOT_FUTURES_MIN_TP_VS_COST_MULT", value => config.Exits.MinTpVsCostMult = ParseDecimal(value, config.Exits.MinTpVsCostMult));
        SetIfPresent("TRADINGBOT_FUTURES_FAST_EXIT_CHECK_SECONDS", value => config.Futures.FastExitCheckSeconds = ParseInt(value, config.Futures.FastExitCheckSeconds));
        SetIfPresent("TRADINGBOT_FUTURES_ENTRY_MAX_PRICE_DEVIATION_PERCENT", value => config.Entry.MaxEntryPriceDeviationPct = ParseDecimal(value, config.Entry.MaxEntryPriceDeviationPct));
        SetIfPresent("TRADINGBOT_FUTURES_FRESHNESS_FRESH_CONTINUATION_MIN_24H_RANGE_POSITION_PERCENT", value => config.Freshness.FreshContinuationMin24hRangePositionPct = ParseDecimal(value, config.Freshness.FreshContinuationMin24hRangePositionPct));
        SetIfPresent("TRADINGBOT_FUTURES_FRESHNESS_MAX_CONTINUATION_24H_RANGE_POSITION_PERCENT", value => config.Freshness.MaxContinuationRangePositionPct = ParseDecimal(value, config.Freshness.MaxContinuationRangePositionPct));
        SetIfPresent("TRADINGBOT_FUTURES_LONG_RANGE_GUARD_ENABLED", value => config.Freshness.LongRangeGuardEnabled = ParseBool(value, config.Freshness.LongRangeGuardEnabled));
        SetIfPresent("TRADINGBOT_FUTURES_LONG_MAX_24H_RANGE_POSITION_PERCENT", value => config.Freshness.Max24hRangePositionForLong = ParseDecimal(value, config.Freshness.Max24hRangePositionForLong));
        SetIfPresent("TRADINGBOT_FUTURES_LONG_MIN_REBOUND_FROM_24H_LOW_PERCENT", value => config.Freshness.MinReboundFrom24hLowPct = ParseDecimal(value, config.Freshness.MinReboundFrom24hLowPct));
        SetIfPresent("TRADINGBOT_FUTURES_LONG_REQUIRED_RISING_SNAPSHOT_COUNT", value => config.Freshness.RequiredRisingSnapshotCount = ParseInt(value, config.Freshness.RequiredRisingSnapshotCount));
        SetIfPresent("TRADINGBOT_FUTURES_LONG_REQUIRE_POSITIVE_SHORT_SLOPE", value => config.Freshness.RequirePositiveShortSlope = ParseBool(value, config.Freshness.RequirePositiveShortSlope));
        SetIfPresent("TRADINGBOT_FUTURES_LONG_REQUIRE_FRESH_TAPE_FOR_LOW_RANGE", value => config.Freshness.RequireFreshTapeForLowRangeLong = ParseBool(value, config.Freshness.RequireFreshTapeForLowRangeLong));
        SetIfPresent("TRADINGBOT_FUTURES_LONG_ROBUST_RANGE_MIN_SAMPLE_COUNT", value => config.Freshness.RobustRangeMinSampleCount = ParseInt(value, config.Freshness.RobustRangeMinSampleCount));
        SetIfPresent("TRADINGBOT_FUTURES_LONG_MIN_24H_RANGE_WIDTH_PERCENT", value => config.Freshness.Min24hRangeWidthPct = ParseDecimal(value, config.Freshness.Min24hRangeWidthPct));
        SetIfPresent("TRADINGBOT_FUTURES_FRESHNESS_NEAR_HIGH_MIN_24H_RANGE_POSITION_PERCENT", value => config.Freshness.NearHighMin24hRangePositionPct = ParseDecimal(value, config.Freshness.NearHighMin24hRangePositionPct));
        SetIfPresent("TRADINGBOT_FUTURES_FRESHNESS_NEAR_HIGH_MAX_DISTANCE_FROM_RECENT_HIGH_PERCENT", value => config.Freshness.NearHighMaxDistanceFromRecentHighPct = ParseDecimal(value, config.Freshness.NearHighMaxDistanceFromRecentHighPct));
        SetIfPresent("TRADINGBOT_FUTURES_FRESHNESS_RECENT_HIGH_LOOKBACK_CANDLES", value => config.Freshness.RecentHighLookbackCandles = ParseInt(value, config.Freshness.RecentHighLookbackCandles));
        SetIfPresent("TRADINGBOT_FUTURES_FRESHNESS_FRESH_TAPE_SNAPSHOT_COUNT", value => config.Freshness.FreshTapeSnapshotCount = ParseInt(value, config.Freshness.FreshTapeSnapshotCount));
        SetIfPresent("TRADINGBOT_FUTURES_FRESHNESS_FRESH_TAPE_MIN_SLOPE_PERCENT", value => config.Freshness.FreshTapeMinSlopePct = ParseDecimal(value, config.Freshness.FreshTapeMinSlopePct));
        SetIfPresent("TRADINGBOT_FUTURES_FRESHNESS_FRESH_TAPE_MIN_POSITIVE_STEPS", value => config.Freshness.FreshTapeMinPositiveSteps = ParseInt(value, config.Freshness.FreshTapeMinPositiveSteps));
        SetIfPresent("TRADINGBOT_FUTURES_FRESHNESS_BREAKOUT_MIN_ABOVE_RECENT_HIGH_PERCENT", value => config.Freshness.BreakoutMinAboveRecentHighPct = ParseDecimal(value, config.Freshness.BreakoutMinAboveRecentHighPct));
        SetIfPresent("TRADINGBOT_FUTURES_FRESHNESS_CONTINUATION_CANDLE_MOMENTUM_LOOKBACK", value => config.Freshness.ContinuationCandleMomentumLookback = ParseInt(value, config.Freshness.ContinuationCandleMomentumLookback));
        SetIfPresent("TRADINGBOT_FUTURES_FRESHNESS_MIN_CONTINUATION_CANDLE_MOMENTUM_PERCENT", value => config.Freshness.MinContinuationCandleMomentumPct = ParseDecimal(value, config.Freshness.MinContinuationCandleMomentumPct));
        SetIfPresent("TRADINGBOT_FUTURES_FRESHNESS_LOCAL_HIGH_LOOKBACK_CLOSED_CANDLES", value => config.Freshness.LocalHighLookbackClosedCandles = ParseInt(value, config.Freshness.LocalHighLookbackClosedCandles));
        SetIfPresent("TRADINGBOT_FUTURES_FRESHNESS_MAX_ENTRY_DISTANCE_FROM_LOCAL_HIGH_PERCENT", value => config.Freshness.MaxEntryDistanceFromLocalHighPct = ParseDecimal(value, config.Freshness.MaxEntryDistanceFromLocalHighPct));
        SetIfPresent("TRADINGBOT_FUTURES_FRESHNESS_BREAKOUT_HOLD_SNAPSHOT_COUNT", value => config.Freshness.BreakoutHoldSnapshotCount = ParseInt(value, config.Freshness.BreakoutHoldSnapshotCount));
        SetIfPresent("TRADINGBOT_FUTURES_FRESHNESS_MAX_ENTRY_DRIFT_FROM_SIGNAL_PERCENT", value => config.Freshness.MaxEntryDriftFromSignalPct = ParseDecimal(value, config.Freshness.MaxEntryDriftFromSignalPct));
        SetIfPresent("TRADINGBOT_FUTURES_ANTI_CHASE_MIN_RANGE_POSITION_PERCENT", value => config.Freshness.AntiChaseMinRangePositionPct = ParseDecimal(value, config.Freshness.AntiChaseMinRangePositionPct));
        SetIfPresent("TRADINGBOT_FUTURES_LOW_RANGE_MIN_CONFIRMATIONS", value => config.Freshness.LowRangeMinConfirmations = ParseInt(value, config.Freshness.LowRangeMinConfirmations));
        SetIfPresent("TRADINGBOT_FUTURES_DRIFT_ATR_MULTIPLE", value => config.Freshness.DriftAtrMultiple = ParseDecimal(value, config.Freshness.DriftAtrMultiple));
        SetIfPresent("TRADINGBOT_FUTURES_LOW_RANGE_REQUIRE_STRONG_CONFIRMATION", value => config.Freshness.LowRangeRequireStrongConfirmation = ParseBool(value, config.Freshness.LowRangeRequireStrongConfirmation));
        SetIfPresent("TRADINGBOT_FUTURES_UPPER_BREAKOUT_MIN_FOLLOW_THROUGH_PERCENT", value => config.Freshness.UpperBreakoutMinFollowThroughPct = ParseDecimal(value, config.Freshness.UpperBreakoutMinFollowThroughPct));
        SetIfPresent("TRADINGBOT_FUTURES_MID_RANGE_RECLAIM_MIN_PRICE_ACTION_TREND_PERCENT", value => config.Freshness.MidRangeReclaimMinPriceActionTrendPct = ParseDecimal(value, config.Freshness.MidRangeReclaimMinPriceActionTrendPct));
        SetIfPresent("TRADINGBOT_FUTURES_SHORT_ANTI_CHASE_MAX_RANGE_POSITION_PERCENT", value => config.Shorts.AntiChaseMaxRangePositionPct = ParseDecimal(value, config.Shorts.AntiChaseMaxRangePositionPct));
        SetIfPresent("TRADINGBOT_FUTURES_RELATIVE_STRENGTH_GATE_ENABLED", value => config.Regime.RelativeStrengthGateEnabled = ParseBool(value, config.Regime.RelativeStrengthGateEnabled));
        SetIfPresent("TRADINGBOT_FUTURES_MIN_RELATIVE_STRENGTH_PERCENT", value => config.Regime.MinRelativeStrengthPct = ParseDecimal(value, config.Regime.MinRelativeStrengthPct));
        SetIfPresent("TRADINGBOT_FUTURES_DIP_BOUNCE_ENABLED", value => config.Dip.Enabled = ParseBool(value, config.Dip.Enabled));
        SetIfPresent("TRADINGBOT_FUTURES_DIP_BOUNCE_NEAR_LOW_MAX_24H_RANGE_POSITION_PERCENT", value => config.Dip.NearLowMax24hRangePositionPct = ParseDecimal(value, config.Dip.NearLowMax24hRangePositionPct));
        SetIfPresent("TRADINGBOT_FUTURES_DIP_BOUNCE_MIN_SCORE", value => config.Dip.MinScore = ParseDecimal(value, config.Dip.MinScore));
        SetIfPresent("TRADINGBOT_FUTURES_DIP_BOUNCE_MIN_CANDLE_MOMENTUM_PERCENT", value => config.Dip.MinCandleMomentumPct = ParseDecimal(value, config.Dip.MinCandleMomentumPct));
        SetIfPresent("TRADINGBOT_MINIMUM_EMA_GAP_PERCENT", value => config.Strategy.MinimumEmaGapPercent = ParseDecimal(value, config.Strategy.MinimumEmaGapPercent));
        SetIfPresent("TRADINGBOT_STRATEGY_MINIMUM_EMA_GAP_PERCENT", value => config.Strategy.MinimumEmaGapPercent = ParseDecimal(value, config.Strategy.MinimumEmaGapPercent));
        SetIfPresent("TRADINGBOT_STRATEGY_MINIMUM_LONG_SCORE", value => config.Strategy.MinimumLongScore = ParseDecimal(value, config.Strategy.MinimumLongScore));
        SetIfPresent("TRADINGBOT_STRATEGY_MAX_ENTRY_SPREAD_PERCENT", value => config.Strategy.MaxEntrySpreadPercent = ParseDecimal(value, config.Strategy.MaxEntrySpreadPercent));
        SetIfPresent("TRADINGBOT_STRATEGY_MAX_ENTRY_EXTENSION_PERCENT", value => config.Strategy.MaxEntryExtensionPercent = ParseDecimal(value, config.Strategy.MaxEntryExtensionPercent));
        SetIfPresent("TRADINGBOT_STRATEGY_MAX_ENTRY_RUNUP_PERCENT", value => config.Strategy.MaxEntryRunupPercent = ParseDecimal(value, config.Strategy.MaxEntryRunupPercent));
        SetIfPresent("TRADINGBOT_STRATEGY_REQUIRE_PRICE_ACTION_DATA", value => config.Strategy.RequirePriceActionData = ParseBool(value, config.Strategy.RequirePriceActionData));
        SetIfPresent("TRADINGBOT_FUTURES_MIN_SHORT_SCORE", value => config.Shorts.MinShortScore = ParseDecimal(value, config.Shorts.MinShortScore));
        SetIfPresent("TRADINGBOT_FUTURES_BTC_REGIME_LONG_OVERRIDE_MIN_SCORE", value => config.Regime.LongOverrideMinScore = ParseDecimal(value, config.Regime.LongOverrideMinScore));
        SetIfPresent("TRADINGBOT_FUTURES_BTC_REGIME_SHORT_OVERRIDE_MIN_SCORE", value => config.Regime.ShortOverrideMinScore = ParseDecimal(value, config.Regime.ShortOverrideMinScore));
        SetIfPresent("TRADINGBOT_FUTURES_EXITS_MAX_HOLD_MIN_STOP_PROGRESS_PERCENT", value => config.Exits.MaxHoldMinStopProgressPct = ParseDecimal(value, config.Exits.MaxHoldMinStopProgressPct));
        SetIfPresent("TRADINGBOT_TPSL_TAKE_PROFIT_PERCENT", value => config.TpSl.TakeProfitPercent = ParseDecimal(value, config.TpSl.TakeProfitPercent));
        SetIfPresent("TRADINGBOT_TPSL_STOP_LOSS_PERCENT", value => config.TpSl.StopLossPercent = ParseDecimal(value, config.TpSl.StopLossPercent));
        SetIfPresent("TRADINGBOT_TPSL_EXCHANGE_PROTECTION_MULTIPLIER_PERCENT", value => config.TpSl.ExchangeProtectionMultiplierPercent = ParseDecimal(value, config.TpSl.ExchangeProtectionMultiplierPercent));
        SetIfPresent("TRADINGBOT_TPSL_TRAILING_STOP_PERCENT", value => config.TpSl.TrailingStopPercent = ParseDecimal(value, config.TpSl.TrailingStopPercent));
        SetIfPresent("TRADINGBOT_FUTURES_LIVE_TRADING_ENABLED", value => config.Futures.LiveTradingEnabled = ParseBool(value, config.Futures.LiveTradingEnabled));
        SetIfPresent("TRADINGBOT_KRAKEN_FUTURES_API_KEY", value => config.Kraken.ApiKey = value);
        SetIfPresent("TRADINGBOT_KRAKEN_FUTURES_API_SECRET", value => config.Kraken.ApiSecret = value);
        SetIfPresent("TRADINGBOT_FUTURES_MIN_LIQUIDATION_DISTANCE_PERCENT", value => config.Margin.MinLiquidationDistancePercent = ParseDecimal(value, config.Margin.MinLiquidationDistancePercent));
        SetIfPresent("TRADINGBOT_FUTURES_MAX_MARGIN_UTILIZATION_PERCENT", value => config.Margin.MaxAccountMarginUtilizationPercent = ParseDecimal(value, config.Margin.MaxAccountMarginUtilizationPercent));
        SetIfPresent("TRADINGBOT_FUTURES_DEAD_MAN_SWITCH_ENABLED", value => config.Futures.DeadManSwitchEnabled = ParseBool(value, config.Futures.DeadManSwitchEnabled));
        SetIfPresent("TRADINGBOT_FUTURES_DEAD_MAN_SWITCH_SECONDS", value => config.Futures.DeadManSwitchSeconds = ParseInt(value, config.Futures.DeadManSwitchSeconds));
        SetIfPresent("TRADINGBOT_TELEGRAM_BOT_TOKEN", value => config.Telegram.BotToken = value);
        SetIfPresent("TRADINGBOT_TELEGRAM_CHAT_ID", value => config.Telegram.ChatId = value);
        SetIfPresent("TRADINGBOT_TELEGRAM_ENABLED", value => config.Telegram.Enabled = ParseBool(value, config.Telegram.Enabled));
    }

    private void Normalize()
    {
        BotInstance.Id = BotInstanceId.Normalize(BotInstance.Id);
        BotInstance.Name = string.IsNullOrWhiteSpace(BotInstance.Name) ? BotInstance.Id : BotInstance.Name.Trim();
        Worker.LoopIntervalSeconds = Math.Max(10, Worker.LoopIntervalSeconds);
        Http.TimeoutSeconds = Math.Clamp(Http.TimeoutSeconds, 5, 120);
        Trading.TimeframeMinutes = Trading.TimeframeMinutes <= 0 ? 5 : Trading.TimeframeMinutes;
        Trading.MaxActiveInstruments = Math.Max(1, Trading.MaxActiveInstruments);
        Trading.StrongMoverMinChangePercent = Math.Max(0m, Trading.StrongMoverMinChangePercent);
        Trading.StrongMoverMinDailyVolumeEur = Math.Max(0m, Trading.StrongMoverMinDailyVolumeEur);
        Portfolio.StartingCashUsd = Portfolio.StartingCashUsd <= 0m ? 100m : Portfolio.StartingCashUsd;

        // Blueprint safety defaults: dry-run only, no flip, and a small portfolio
        // cap. Normalize clamps rather than trusts config so a typo cannot widen the
        // risk envelope. The hard leverage ceiling is 10x — a value above that is a
        // typo, not an intent; the per-symbol margin preference set on Kraken and the
        // liquidation-distance gate still apply on top of this cap.
        Futures.MaxLeverage = Math.Clamp(Futures.MaxLeverage <= 0m ? 10m : Futures.MaxLeverage, 1m, 10m);
        Futures.DefaultLeverage = Math.Clamp(Futures.DefaultLeverage <= 0m ? 1m : Futures.DefaultLeverage, 1m, Futures.MaxLeverage);
        Futures.MaxPositions = Math.Clamp(Futures.MaxPositions <= 0 ? 3 : Futures.MaxPositions, 1, 3);
        Futures.AllowFlip = false;

        // The flipped-logic experiment opens real shorts, so it cannot run with
        // shorts disabled; drop the flip rather than silently opening nothing.
        if (Futures.FlipLongEntries && !Futures.AllowShorts)
        {
            Futures.FlipLongEntries = false;
            Console.WriteLine("config-validation: Futures.FlipLongEntries=true requires Futures.AllowShorts=true; flip disabled.");
        }

        if (Futures.FlipMaxPair24hRisePercent < 0m || Futures.FlipMaxPair24hRisePercent > 100m)
        {
            Console.WriteLine($"config-validation: Futures.FlipMaxPair24hRisePercent={Futures.FlipMaxPair24hRisePercent} is out of [0,100]; reset to 3.");
            Futures.FlipMaxPair24hRisePercent = 3m;
        }

        if (Futures.FlipMaxBtc24hRisePercent < -100m || Futures.FlipMaxBtc24hRisePercent > 100m)
        {
            Console.WriteLine($"config-validation: Futures.FlipMaxBtc24hRisePercent={Futures.FlipMaxBtc24hRisePercent} is out of [-100,100]; reset to 0.");
            Futures.FlipMaxBtc24hRisePercent = 0m;
        }

        if (Futures.FlipLongEntries)
        {
            Console.WriteLine(
                $"config-warning: Futures.FlipLongEntries=true — approved LONG entries execute as SHORT only when pair24h <= {Futures.FlipMaxPair24hRisePercent:0.###}% and btc24h <= {Futures.FlipMaxBtc24hRisePercent:0.###}%; otherwise the original LONG is preserved.");
        }

        // Sizing migration: the old TargetNotionalUsd meant NOTIONAL; the new
        // TargetMarginUsd means MARGIN. If only the legacy value is set, derive the
        // margin that PRESERVES the old notional exposure (notional / leverage) and
        // warn — never silently 10x the position by reinterpreting the number.
        if (Futures.TargetMarginUsd <= 0m && Futures.TargetNotionalUsd is { } legacyNotional && legacyNotional > 0m)
        {
            Futures.TargetMarginUsd = legacyNotional / Math.Max(1m, Futures.DefaultLeverage);
            Console.WriteLine(
                $"config-migration: Futures.TargetNotionalUsd={legacyNotional:0.####} is deprecated; interpreted as legacy NOTIONAL and migrated to TargetMarginUsd={Futures.TargetMarginUsd:0.####} (notional/leverage) to preserve exposure. Set TargetMarginUsd explicitly.");
        }
        Futures.TargetMarginUsd = Futures.TargetMarginUsd <= 0m ? 10m : Futures.TargetMarginUsd;
        Futures.MaxNotionalUsd = Futures.MaxNotionalUsd < 0m ? 0m : Futures.MaxNotionalUsd;
        Futures.MaxMarginPerPositionUsd = Futures.MaxMarginPerPositionUsd < 0m ? 0m : Futures.MaxMarginPerPositionUsd;
        Futures.MaxTotalNotionalUsd = Futures.MaxTotalNotionalUsd < 0m ? 0m : Futures.MaxTotalNotionalUsd;
        Futures.TargetNotionalUsd = null;
        Futures.FastExitCheckSeconds = Math.Clamp(Futures.FastExitCheckSeconds <= 0 ? 10 : Futures.FastExitCheckSeconds, 5, Worker.LoopIntervalSeconds);
        Futures.DeadManSwitchSeconds = Math.Max(10, Futures.DeadManSwitchSeconds);
        // DMS must outlive the gap between live-cycle refreshes (loop sleep + cycle work).
        var minDeadManSwitchSeconds = Worker.LoopIntervalSeconds * 2;
        if (Futures.DeadManSwitchSeconds < minDeadManSwitchSeconds)
        {
            Futures.DeadManSwitchSeconds = minDeadManSwitchSeconds;
        }

        EntryMirror.PublishToBotInstanceId = NormalizeOptionalInstanceId(EntryMirror.PublishToBotInstanceId);
        EntryMirror.FollowSourceBotInstanceId = NormalizeOptionalInstanceId(EntryMirror.FollowSourceBotInstanceId);
        EntryMirror.MaxCommandAgeSeconds = Math.Clamp(
            EntryMirror.MaxCommandAgeSeconds <= 0 ? 60 : EntryMirror.MaxCommandAgeSeconds,
            10,
            300);
        EntryMirror.MaxAttempts = Math.Clamp(
            EntryMirror.MaxAttempts <= 0 ? 3 : EntryMirror.MaxAttempts,
            1,
            10);

        if (!string.IsNullOrWhiteSpace(EntryMirror.PublishToBotInstanceId)
            && !string.IsNullOrWhiteSpace(EntryMirror.FollowSourceBotInstanceId))
        {
            Console.WriteLine("config-validation: EntryMirror cannot publish and follow in the same worker; mirror configuration disabled.");
            EntryMirror.PublishToBotInstanceId = null;
            EntryMirror.FollowSourceBotInstanceId = null;
        }

        if (EntryMirror.PublishToBotInstanceId?.Equals(BotInstance.Id, StringComparison.OrdinalIgnoreCase) == true
            || EntryMirror.FollowSourceBotInstanceId?.Equals(BotInstance.Id, StringComparison.OrdinalIgnoreCase) == true)
        {
            Console.WriteLine("config-validation: EntryMirror peer must differ from BotInstance.Id; mirror configuration disabled.");
            EntryMirror.PublishToBotInstanceId = null;
            EntryMirror.FollowSourceBotInstanceId = null;
        }

        if (Futures.LiveTradingEnabled
            && EntryMirror.IsConfigured
            && (!Database.Enabled || string.IsNullOrWhiteSpace(Database.ConnectionString)))
        {
            throw new InvalidOperationException("Live futures entry mirroring requires the shared Postgres database.");
        }

        if (Margin.MaintenanceMarginRatePercent is <= 0m or > 50m)
        {
            Console.WriteLine(
                $"config-validation: Margin.MaintenanceMarginRatePercent={Margin.MaintenanceMarginRatePercent:0.####} is invalid; reset to 5.");
            Margin.MaintenanceMarginRatePercent = 5m;
        }
        Margin.MinLiquidationDistancePercent = Math.Max(0m, Margin.MinLiquidationDistancePercent);
        Margin.MaxAccountMarginUtilizationPercent = Margin.MaxAccountMarginUtilizationPercent <= 0m
            ? 50m
            : Math.Clamp(Margin.MaxAccountMarginUtilizationPercent, 1m, 100m);

        Fees.MakerPct = Fees.MakerPct <= 0m ? 0.02m : Fees.MakerPct;
        Fees.TakerPct = Fees.TakerPct <= 0m ? 0.05m : Fees.TakerPct;
        Funding.MaxAbsFundingRatePercentForEntry = Funding.MaxAbsFundingRatePercentForEntry <= 0m
            ? 0.03m
            : Funding.MaxAbsFundingRatePercentForEntry;
        Funding.FundingLookbackHours = Math.Max(1, Funding.FundingLookbackHours);

        Entry.MakerFillTimeoutSec = Math.Max(1, Entry.MakerFillTimeoutSec);
        Entry.MakerRepegs = Math.Max(0, Entry.MakerRepegs);
        Entry.MaxQueueAheadMultiple = Entry.MaxQueueAheadMultiple <= 0m ? 5m : Entry.MaxQueueAheadMultiple;
        Entry.MaxEntryPriceDeviationPct = Entry.MaxEntryPriceDeviationPct <= 0m
            ? 0.35m
            : Math.Clamp(Entry.MaxEntryPriceDeviationPct, 0.05m, 2m);
        Freshness.FreshContinuationMin24hRangePositionPct = Math.Clamp(Freshness.FreshContinuationMin24hRangePositionPct < 0m ? 50m : Freshness.FreshContinuationMin24hRangePositionPct, 0m, 100m);
        Freshness.MaxContinuationRangePositionPct = Math.Clamp(Freshness.MaxContinuationRangePositionPct <= 0m ? 80m : Freshness.MaxContinuationRangePositionPct, 50m, 100m);
        // LONG 24h-range gate validation. Invalid production values are reset to safe
        // defaults (never silently left in a dangerous state).
        if (Freshness.Max24hRangePositionForLong < 0m || Freshness.Max24hRangePositionForLong > 100m)
        {
            Console.WriteLine($"config-validation: Freshness.Max24hRangePositionForLong={Freshness.Max24hRangePositionForLong} is out of [0,100]; reset to 30.");
            Freshness.Max24hRangePositionForLong = 30m;
        }
        Freshness.MinReboundFrom24hLowPct = Math.Max(0m, Freshness.MinReboundFrom24hLowPct);
        Freshness.RequiredRisingSnapshotCount = Math.Max(1, Freshness.RequiredRisingSnapshotCount);
        Freshness.RobustRangeMinSampleCount = Math.Clamp(Freshness.RobustRangeMinSampleCount <= 0 ? 20 : Freshness.RobustRangeMinSampleCount, 2, 96);
        Freshness.Min24hRangeWidthPct = Math.Max(0m, Freshness.Min24hRangeWidthPct);
        Freshness.NearHighMin24hRangePositionPct = Math.Clamp(Freshness.NearHighMin24hRangePositionPct <= 0m ? 88m : Freshness.NearHighMin24hRangePositionPct, 50m, 100m);
        Freshness.NearHighMaxDistanceFromRecentHighPct = Math.Clamp(Freshness.NearHighMaxDistanceFromRecentHighPct <= 0m ? 0.5m : Freshness.NearHighMaxDistanceFromRecentHighPct, 0m, 10m);
        Freshness.RecentHighLookbackCandles = Math.Clamp(Freshness.RecentHighLookbackCandles <= 0 ? 12 : Freshness.RecentHighLookbackCandles, 2, 96);
        Freshness.FreshTapeSnapshotCount = Math.Clamp(Freshness.FreshTapeSnapshotCount <= 0 ? 3 : Freshness.FreshTapeSnapshotCount, 2, 10);
        Freshness.FreshTapeMinSlopePct = Math.Clamp(Freshness.FreshTapeMinSlopePct <= 0m ? 0.05m : Freshness.FreshTapeMinSlopePct, 0m, 5m);
        Freshness.FreshTapeMinPositiveSteps = Math.Clamp(Freshness.FreshTapeMinPositiveSteps <= 0 ? 2 : Freshness.FreshTapeMinPositiveSteps, 1, Freshness.FreshTapeSnapshotCount - 1);
        Freshness.BreakoutMinAboveRecentHighPct = Math.Clamp(Freshness.BreakoutMinAboveRecentHighPct <= 0m ? 0.05m : Freshness.BreakoutMinAboveRecentHighPct, 0m, 5m);
        Freshness.ContinuationCandleMomentumLookback = Math.Clamp(Freshness.ContinuationCandleMomentumLookback <= 0 ? 4 : Freshness.ContinuationCandleMomentumLookback, 1, 50);
        Freshness.MinContinuationCandleMomentumPct = Math.Max(0m, Freshness.MinContinuationCandleMomentumPct);
        Freshness.LocalHighLookbackClosedCandles = Math.Clamp(Freshness.LocalHighLookbackClosedCandles <= 0 ? 2 : Freshness.LocalHighLookbackClosedCandles, 1, 8);
        Freshness.MaxEntryDistanceFromLocalHighPct = Math.Clamp(Freshness.MaxEntryDistanceFromLocalHighPct <= 0m ? 0.12m : Freshness.MaxEntryDistanceFromLocalHighPct, 0m, 2m);
        Freshness.BreakoutHoldSnapshotCount = Math.Clamp(Freshness.BreakoutHoldSnapshotCount <= 0 ? 2 : Freshness.BreakoutHoldSnapshotCount, 1, Freshness.FreshTapeSnapshotCount);
        Freshness.MaxEntryDriftFromSignalPct = Math.Clamp(Freshness.MaxEntryDriftFromSignalPct <= 0m ? 0.10m : Freshness.MaxEntryDriftFromSignalPct, 0m, 2m);
        if (Freshness.AntiChaseMinRangePositionPct < 0m || Freshness.AntiChaseMinRangePositionPct > 100m)
        {
            Console.WriteLine($"config-validation: Freshness.AntiChaseMinRangePositionPct={Freshness.AntiChaseMinRangePositionPct} is out of [0,100]; reset to 35.");
            Freshness.AntiChaseMinRangePositionPct = 35m;
        }

        if (Freshness.LowRangeMinConfirmations < 1 || Freshness.LowRangeMinConfirmations > 4)
        {
            Console.WriteLine($"config-validation: Freshness.LowRangeMinConfirmations={Freshness.LowRangeMinConfirmations} is out of [1,4]; reset to 2.");
            Freshness.LowRangeMinConfirmations = 2;
        }

        if (Freshness.DriftAtrMultiple < 0m || Freshness.DriftAtrMultiple > 5m)
        {
            Console.WriteLine($"config-validation: Freshness.DriftAtrMultiple={Freshness.DriftAtrMultiple} is out of [0,5]; reset to 0.25.");
            Freshness.DriftAtrMultiple = 0.25m;
        }

        if (Freshness.UpperBreakoutMinFollowThroughPct < 0m || Freshness.UpperBreakoutMinFollowThroughPct > 5m)
        {
            Console.WriteLine($"config-validation: Freshness.UpperBreakoutMinFollowThroughPct={Freshness.UpperBreakoutMinFollowThroughPct} is out of [0,5]; reset to 0.60.");
            Freshness.UpperBreakoutMinFollowThroughPct = 0.60m;
        }

        if (Freshness.MidRangeReclaimMinPriceActionTrendPct < 0m || Freshness.MidRangeReclaimMinPriceActionTrendPct > 5m)
        {
            Console.WriteLine($"config-validation: Freshness.MidRangeReclaimMinPriceActionTrendPct={Freshness.MidRangeReclaimMinPriceActionTrendPct} is out of [0,5]; reset to 0.50.");
            Freshness.MidRangeReclaimMinPriceActionTrendPct = 0.50m;
        }

        if (Freshness.DirectionalEfficiencyLookbackCandles < 8 || Freshness.DirectionalEfficiencyLookbackCandles > 120)
        {
            Console.WriteLine($"config-validation: Freshness.DirectionalEfficiencyLookbackCandles={Freshness.DirectionalEfficiencyLookbackCandles} is out of [8,120]; reset to 96.");
            Freshness.DirectionalEfficiencyLookbackCandles = 96;
        }

        if (Freshness.MinMidRangeDirectionalEfficiencyPct < 0m || Freshness.MinMidRangeDirectionalEfficiencyPct > 100m)
        {
            Console.WriteLine($"config-validation: Freshness.MinMidRangeDirectionalEfficiencyPct={Freshness.MinMidRangeDirectionalEfficiencyPct} is out of [0,100]; reset to 5.");
            Freshness.MinMidRangeDirectionalEfficiencyPct = 5m;
        }

        Regime.MinRelativeStrengthPct = Math.Clamp(Regime.MinRelativeStrengthPct, 0m, 100m);

        if (Shorts.AntiChaseMaxRangePositionPct < 0m || Shorts.AntiChaseMaxRangePositionPct > 100m)
        {
            Console.WriteLine($"config-validation: Shorts.AntiChaseMaxRangePositionPct={Shorts.AntiChaseMaxRangePositionPct} is out of [0,100]; reset to 65.");
            Shorts.AntiChaseMaxRangePositionPct = 65m;
        }

        // Dip-bounce channel: near-low zone is the lower band of the 24h range; the
        // relaxed entry score must stay below the firm long bar or the channel is a
        // no-op (a score already >= MinimumLongScore is a normal entry).
        Dip.NearLowMax24hRangePositionPct = Math.Clamp(Dip.NearLowMax24hRangePositionPct <= 0m ? 25m : Dip.NearLowMax24hRangePositionPct, 0m, 100m);
        Dip.MinScore = Math.Clamp(Dip.MinScore <= 0m ? 0.70m : Dip.MinScore, 0m, 1m);
        Dip.MinCandleMomentumPct = Math.Max(0m, Dip.MinCandleMomentumPct);
        Filters.MinQuoteVolume24h = Filters.MinQuoteVolume24h <= 0m ? 50_000m : Filters.MinQuoteVolume24h;
        Filters.MinExitDepthMultiple = Filters.MinExitDepthMultiple <= 0m ? 5m : Filters.MinExitDepthMultiple;
        Filters.MaxExitImpactPct = Filters.MaxExitImpactPct <= 0m ? 0.5m : Filters.MaxExitImpactPct;
        Exits.StopAtrMult = Exits.StopAtrMult <= 0m ? 1m : Exits.StopAtrMult;
        Exits.MinStopAtrFloor = Exits.MinStopAtrFloor <= 0m ? 1.5m : Exits.MinStopAtrFloor;
        Exits.TakeProfitAtrMult = Exits.TakeProfitAtrMult <= 0m ? 3m : Exits.TakeProfitAtrMult;
        Exits.MinRewardRiskMultiple = Exits.MinRewardRiskMultiple <= 0m ? 2m : Exits.MinRewardRiskMultiple;
        Exits.MinTpVsCostMult = Exits.MinTpVsCostMult <= 0m ? 3m : Exits.MinTpVsCostMult;
        Exits.StopDistanceCapPct = Exits.StopDistanceCapPct <= 0m ? 3m : Exits.StopDistanceCapPct;
        Exits.TakeProfitCapPct = Math.Max(0m, Exits.TakeProfitCapPct);
        Exits.SlippageBufferPct = Exits.SlippageBufferPct < 0m ? 0.10m : Exits.SlippageBufferPct;
        Exits.MaxHoldMinutes = Exits.MaxHoldMinutes <= 0 ? 360 : Exits.MaxHoldMinutes;
        Exits.MaxHoldMinStopProgressPct = Math.Clamp(Exits.MaxHoldMinStopProgressPct <= 0m ? 60m : Exits.MaxHoldMinStopProgressPct, 0m, 100m);
        Exits.TrailingActivationBufferPct = Math.Max(0m, Exits.TrailingActivationBufferPct);
        Regime.BtcTrendMa = Regime.BtcTrendMa <= 0 ? 50 : Regime.BtcTrendMa;
        Regime.BtcSlopeLookback = Regime.BtcSlopeLookback <= 0 ? 3 : Regime.BtcSlopeLookback;
        Regime.BtcCrashLookback = Regime.BtcCrashLookback <= 0 ? 4 : Regime.BtcCrashLookback;
        Regime.BtcCrashPct = Regime.BtcCrashPct <= 0m ? 2m : Regime.BtcCrashPct;
        Regime.LongOverrideMinScore = Math.Clamp(Regime.LongOverrideMinScore <= 0m ? 0.85m : Regime.LongOverrideMinScore, 0m, 1m);
        Regime.ShortOverrideMinScore = Math.Clamp(Regime.ShortOverrideMinScore <= 0m ? 0.85m : Regime.ShortOverrideMinScore, 0m, 1m);
        Shorts.MaxChaseDrawdownPct = Shorts.MaxChaseDrawdownPct <= 0m ? 3m : Shorts.MaxChaseDrawdownPct;
        Shorts.MinShortScore = Shorts.MinShortScore <= 0m ? 0.90m : Shorts.MinShortScore;
        // SHORT range/anti-knife gate (mirror of the LONG Freshness thresholds).
        if (Shorts.Min24hRangePositionForShort < 0m || Shorts.Min24hRangePositionForShort > 100m)
        {
            Console.WriteLine($"config-validation: Shorts.Min24hRangePositionForShort={Shorts.Min24hRangePositionForShort} is out of [0,100]; reset to 70.");
            Shorts.Min24hRangePositionForShort = 70m;
        }
        Shorts.MinPullbackFrom24hHighPct = Math.Max(0m, Shorts.MinPullbackFrom24hHighPct);
        Shorts.RequiredFallingSnapshotCount = Math.Max(1, Shorts.RequiredFallingSnapshotCount);
        // Risk-based sizing: TargetRiskUsd is the USD stop-distance budget per entry.
        // Default 1.00 USD = 1% of ~100 USD virtual equity per position. Worked example
        // (leverage 10x, stop floor 0.75%): notional = 1.00 / 0.0075 ≈ 133 USD, margin
        // ≈ 13.3 USD; three concurrent slots = 3 USD stop heat (3% equity) and ≈ 40 USD
        // margin (40% utilization). MaxConcurrentOpenRisk defaults to
        // TargetRiskUsd * MaxPositions so all slots can hold one full-budget position.
        Risk.TargetRiskUsd = Risk.TargetRiskUsd <= 0m ? 1m : Risk.TargetRiskUsd;
        var perPositionOpenRisk = Risk.TargetRiskUsd;
        if (Risk.MaxConcurrentOpenRiskUsd <= 0m || Risk.MaxConcurrentOpenRiskUsd < perPositionOpenRisk)
        {
            Risk.MaxConcurrentOpenRiskUsd = decimal.Round(perPositionOpenRisk * Futures.MaxPositions, 4);
        }
        Risk.EstimatedEmergencyExitCostPct = Math.Max(0m, Risk.EstimatedEmergencyExitCostPct);
        ExecutionPolicy.CooldownAfterCloseSeconds = Math.Max(0, ExecutionPolicy.CooldownAfterCloseSeconds);
        ExecutionPolicy.CooldownAfterStopLossSeconds = Math.Max(0, ExecutionPolicy.CooldownAfterStopLossSeconds);
        ExecutionPolicy.MinHoldSeconds = Math.Max(0, ExecutionPolicy.MinHoldSeconds);
        ExecutionPolicy.EntryBlackoutUtcFromHour = Math.Clamp(ExecutionPolicy.EntryBlackoutUtcFromHour, 0, 23);
        ExecutionPolicy.EntryBlackoutMinutes = Math.Max(0, ExecutionPolicy.EntryBlackoutMinutes);
        CorrelationRisk.MaxOpenPositionsPerGroup = CorrelationRisk.MaxOpenPositionsPerGroup <= 0 ? 1 : CorrelationRisk.MaxOpenPositionsPerGroup;
        // Per-group exposure is NOTIONAL exposure, so it defaults to one derived
        // position notional (margin * leverage), not the margin figure.
        CorrelationRisk.MaxExposureUsdPerGroup = CorrelationRisk.MaxExposureUsdPerGroup <= 0m ? Futures.DerivedNotionalUsd(Futures.DefaultLeverage) : CorrelationRisk.MaxExposureUsdPerGroup;
        UniverseDiscovery.RefreshSeconds = Math.Max(60, UniverseDiscovery.RefreshSeconds);
        UniverseDiscovery.ForceInclude = NormalizeStringList(UniverseDiscovery.ForceInclude);
        UniverseDiscovery.Blacklist = NormalizeStringList(UniverseDiscovery.Blacklist);

        // Entry market-quality gate (spot parity): spread limit, anti-lag price
        // action, anti-extension. Same clamps as the spot worker.
        Strategy.MinimumEmaGapPercent = Math.Max(0m, Strategy.MinimumEmaGapPercent);
        Strategy.MinimumLongScore = Math.Clamp(Strategy.MinimumLongScore, 0m, 1m);
        Strategy.MaxEntrySpreadPercent = Math.Max(0m, Strategy.MaxEntrySpreadPercent);
        Strategy.MaxEntryExtensionPercent = Math.Max(0m, Strategy.MaxEntryExtensionPercent);
        Strategy.MaxEntryRunupPercent = Math.Max(0m, Strategy.MaxEntryRunupPercent);
        Strategy.PriceActionLookbackSnapshots = Math.Max(1, Strategy.PriceActionLookbackSnapshots);
        Strategy.PriceActionMinSnapshots = Math.Max(2, Strategy.PriceActionMinSnapshots);
        Strategy.PriceActionMaxDeclinePercent = Math.Max(0m, Strategy.PriceActionMaxDeclinePercent);
        Strategy.PriceActionMaxNonRisingSnapshots = Math.Max(0, Strategy.PriceActionMaxNonRisingSnapshots);
        Strategy.NegativePriceActionPenalty = Math.Max(0m, Strategy.NegativePriceActionPenalty);
        Strategy.NegativePriceActionPenaltyThresholdPercent = Math.Max(0m, Strategy.NegativePriceActionPenaltyThresholdPercent);
        Strategy.PriceActionMaxSampleAgeMinutes = Math.Max(0, Strategy.PriceActionMaxSampleAgeMinutes);
        Strategy.PriceActionHydrationMinutes = Math.Max(0, Strategy.PriceActionHydrationMinutes);
        // Live futures entries must never bypass the anti-lag guard via missing
        // history — mirror of the spot worker's live warm-up rule.
        if (Futures.LiveTradingEnabled && !Strategy.AllowEntriesWithoutPriceActionInLive)
        {
            Strategy.RequirePriceActionData = true;
        }

        TpSl.Enabled = true;
        TpSl.TakeProfitPercent = TpSl.TakeProfitPercent <= 0m ? 3m : TpSl.TakeProfitPercent;
        TpSl.StopLossPercent = TpSl.StopLossPercent <= 0m ? 1m : TpSl.StopLossPercent;
        TpSl.ExchangeProtectionMultiplierPercent = TpSl.ExchangeProtectionMultiplierPercent <= 0m ? 200m : TpSl.ExchangeProtectionMultiplierPercent;
        TpSl.TrailingStopPercent = TpSl.TrailingStopPercent <= 0m ? 2m : TpSl.TrailingStopPercent;
        TpSl.FlippedTakeProfitPercent = TpSl.FlippedTakeProfitPercent <= 0m ? 1.5m : TpSl.FlippedTakeProfitPercent;
        TpSl.FlippedTrailingStopPercent = TpSl.FlippedTrailingStopPercent <= 0m ? 0.75m : TpSl.FlippedTrailingStopPercent;
        TpSl.ExternalTrailingActivationProgressPercent = Math.Clamp(TpSl.ExternalTrailingActivationProgressPercent, 0m, 100m);
        TpSl.TriggerSource = string.IsNullOrWhiteSpace(TpSl.TriggerSource) ? "mark" : TpSl.TriggerSource;

        // Decoupled sizer floors inherit the legacy TpSl percents when unset (0), giving a
        // clean, explicit name (MinStopDistancePct = minimum stop, not fixed stop) with a
        // backward-compatible mapping from old appsettings/env.
        Exits.MinStopDistancePct = Exits.MinStopDistancePct <= 0m ? TpSl.StopLossPercent : Exits.MinStopDistancePct;
        Exits.MinTakeProfitPct = Exits.MinTakeProfitPct <= 0m ? TpSl.TakeProfitPercent : Exits.MinTakeProfitPct;
    }

    private static void SetIfPresent(string name, Action<string> apply)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            apply(value);
        }
    }

    private static int ParseInt(string value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;

    private static decimal ParseDecimal(string value, decimal fallback) =>
        decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static bool ParseBool(string value, bool fallback) =>
        bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static List<string> ParseCsv(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();

    private static List<string> NormalizeStringList(IEnumerable<string>? values) =>
        (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? NormalizeOptionalInstanceId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : BotInstanceId.Normalize(value);
}

// Where the bot announces an entry. The token is a credential and only ever arrives
// through the environment; the chat id is not secret - without the token it opens
// nothing - so it lives in appsettings where it can be read and changed in the open.
internal sealed class TelegramNotificationOptions
{
    public bool Enabled { get; set; }
    public string? BotToken { get; set; }
    public string? ChatId { get; set; }

    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(BotToken)
        && !string.IsNullOrWhiteSpace(ChatId);
}

internal sealed class FuturesEntryMirrorOptions
{
    public string? PublishToBotInstanceId { get; set; }
    public string? FollowSourceBotInstanceId { get; set; }
    // Permission to turn a copied entry around, not an order to do it: when true the
    // 24h BTC regime decides per trade (see FuturesMirrorFlipGate). When false nothing
    // is ever inverted and the gate is never consulted.
    public bool InvertSide { get; set; } = true;

    // Invert while BTC's 24h change is at or below this. Zero means "invert only when
    // BTC is not rising" - the same threshold the own-signal flip uses, kept separate
    // because the two experiments are not the same and should not move together.
    public decimal InvertMaxBtc24hRisePercent { get; set; }
    public int MaxCommandAgeSeconds { get; set; } = 60;
    public int MaxAttempts { get; set; } = 3;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(PublishToBotInstanceId)
        || !string.IsNullOrWhiteSpace(FollowSourceBotInstanceId);
}

internal sealed class FuturesPortfolioOptions
{
    // Starting margin collateral for the virtual ledger, not spot cash.
    private decimal _startingCashUsd = 100m;

    public decimal StartingCashUsd
    {
        get => _startingCashUsd;
        set => _startingCashUsd = value;
    }

    // Compatibility alias for older tests and private settings. Futures accounting is USD.
    public decimal StartingCashEur
    {
        get => _startingCashUsd;
        set => _startingCashUsd = value;
    }
}

internal sealed class FuturesOptions
{
    public decimal MaxLeverage { get; set; } = 10m;
    public decimal DefaultLeverage { get; set; } = 10m;
    // One-shot repair, off by default. Set to a number of days to have the worker walk
    // Kraken's fills on startup and write journal entries for closures it never saw -
    // every position the exchange closed while this was missing has an opening with no
    // exit. Set it back to 0 once the gap is filled; a second run is harmless anyway,
    // since already-recorded order ids are skipped.
    public int BackfillClosureDays { get; set; }

    public int MaxPositions { get; set; } = 3;
    public bool AllowShorts { get; set; } = true;

    // Flips (long -> short in one step) are forbidden by the blueprint; Normalize
    // forces this to false regardless of config.
    public bool AllowFlip { get; set; }

    // Contrarian experiment ("flipped logic"): a fully approved LONG is executed
    // as a SHORT only when the closed-candle 24h regime is countertrend-friendly.
    // Outside that regime the original LONG is preserved, so the gate changes side
    // selection rather than suppressing an otherwise approved trade.
    public bool FlipLongEntries { get; set; }

    // Off makes the account mirror-only: it opens nothing from its own signals and
    // trades exactly what the mirror source sends. Positions it already holds are
    // still managed and closed as normal - this gates new entries, never exits,
    // and an account that could not close what it opened would be a trap.
    public bool OwnSignalEntriesEnabled { get; set; } = true;
    public decimal FlipMaxPair24hRisePercent { get; set; } = 3m;
    public decimal FlipMaxBtc24hRisePercent { get; set; } = 0m;

    private decimal _targetMarginUsd = 10m;
    private decimal _maxNotionalUsd;
    private decimal _maxTotalNotionalUsd;
    private decimal _maxMarginPerPositionUsd;

    // Independent portfolio caps (all USD, 0 = disabled/derive). These are hard backstops
    // that bound gap/slippage/stop-failure and correlation tail risk INDEPENDENTLY of the
    // open-risk (stop-distance) budget — the open-risk cap does not replace them.
    //   MaxNotionalUsd           : max notional for a single position.
    //   MaxTotalNotionalUsd      : max aggregate notional across all open positions.
    //   MaxMarginPerPositionUsd  : max initial margin committed by a single position.
    // Total USED margin is bounded by Margin.MaxAccountMarginUtilizationPercent (equity %).
    public decimal TargetMarginUsd { get => _targetMarginUsd; set => _targetMarginUsd = value; }
    public decimal MaxNotionalUsd { get => _maxNotionalUsd; set => _maxNotionalUsd = value; }
    public decimal MaxTotalNotionalUsd { get => _maxTotalNotionalUsd; set => _maxTotalNotionalUsd = value; }
    public decimal MaxMarginPerPositionUsd { get => _maxMarginPerPositionUsd; set => _maxMarginPerPositionUsd = value; }

    // Compatibility aliases. Persisted generic portfolio fields still use an *Eur suffix,
    // but futures values stored in them are denominated in USD.
    public decimal TargetMarginEur { get => _targetMarginUsd; set => _targetMarginUsd = value; }
    public decimal MaxNotionalEur { get => _maxNotionalUsd; set => _maxNotionalUsd = value; }
    public decimal MaxTotalNotionalEur { get => _maxTotalNotionalUsd; set => _maxTotalNotionalUsd = value; }
    public decimal MaxMarginPerPositionEur { get => _maxMarginPerPositionUsd; set => _maxMarginPerPositionUsd = value; }

    // Legacy alias (deprecated): previously this value was the position NOTIONAL.
    // Kept only so old appsettings/env do not silently change the risk envelope on
    // upgrade — Normalize migrates it to TargetMarginUsd = legacyNotional / leverage
    // (preserving the old exposure) and logs a one-time warning. Null when unset.
    public decimal? TargetNotionalUsd { get; set; }

    public decimal? TargetNotionalEur
    {
        get => TargetNotionalUsd;
        set => TargetNotionalUsd = value;
    }

    public int FastExitCheckSeconds { get; set; } = 10;
    public bool LiveTradingEnabled { get; set; }
    public bool DeadManSwitchEnabled { get; set; }
    public int DeadManSwitchSeconds { get; set; } = 90;

    // Derived position notional in USD for a new entry at the given leverage.
    public decimal DerivedNotionalUsd(decimal leverage) => TargetMarginUsd * Math.Max(1m, leverage);

    public decimal DerivedNotionalEur(decimal leverage) => DerivedNotionalUsd(leverage);
}

internal sealed class MarginOptions
{
    public decimal MaintenanceMarginRatePercent { get; set; } = 5m;
    public decimal MinLiquidationDistancePercent { get; set; } = 15m;
    public decimal MaxAccountMarginUtilizationPercent { get; set; } = 80m;
}

internal sealed class FuturesFeesOptions
{
    public decimal MakerPct { get; set; } = 0.02m;
    public decimal TakerPct { get; set; } = 0.05m;
}

internal sealed class FundingOptions
{
    // Tune later: per funding period percent. Positive Kraken funding means longs
    // pay shorts; negative means shorts pay longs.
    public decimal MaxAbsFundingRatePercentForEntry { get; set; } = 0.03m;
    public int FundingLookbackHours { get; set; } = 8;
}

internal sealed class FuturesEntryOptions
{
    public int MakerFillTimeoutSec { get; set; } = 60;
    public int MakerRepegs { get; set; } = 1;
    public decimal MaxQueueAheadMultiple { get; set; } = 5m;
    public decimal MaxEntryPriceDeviationPct { get; set; } = 0.35m;
}

internal sealed class FuturesFreshnessOptions
{
    public decimal FreshContinuationMin24hRangePositionPct { get; set; } = 50m;

    // Upper band of the 24h range above which a fresh continuation tape is NOT enough
    // to admit a LONG — only a confirmed breakout (price above the recent high, held
    // over BreakoutHoldSnapshotCount snapshots) may enter. Below this band a fresh
    // continuation tape still admits. This closes the "buy the last green micro-tick
    // near the daily high" hole (the VIRTUAL case: pos24 ~86.7%, fresh 3-snapshot
    // rebound off a pullback, last 15m candle red, not a breakout — yet admitted).
    public decimal MaxContinuationRangePositionPct { get; set; } = 80m;

    public decimal NearHighMin24hRangePositionPct { get; set; } = 88m;
    public decimal NearHighMaxDistanceFromRecentHighPct { get; set; } = 0.5m;
    public int RecentHighLookbackCandles { get; set; } = 12;
    public int FreshTapeSnapshotCount { get; set; } = 3;
    public decimal FreshTapeMinSlopePct { get; set; } = 0.05m;
    public int FreshTapeMinPositiveSteps { get; set; } = 2;
    public decimal BreakoutMinAboveRecentHighPct { get; set; } = 0.05m;

    // A fresh micro-tape (last few snapshots ticking up) must not, by itself,
    // rescue a continuation LONG whose underlying 15m candles are rolling over
    // (the DOGE case: fresh snapshot tape while the 4-candle change was -0.9% and
    // price action was FALLING). In the continuation/near-high zone the tape only
    // counts as fresh when the recent candle momentum over
    // ContinuationCandleMomentumLookback bars is at least
    // MinContinuationCandleMomentumPct. Momentum that cannot be computed abstains
    // (does not block). A genuine breakout above the recent high is unaffected.
    public int ContinuationCandleMomentumLookback { get; set; } = 4;
    public decimal MinContinuationCandleMomentumPct { get; set; } = 0m;
    public int LocalHighLookbackClosedCandles { get; set; } = 2;
    public decimal MaxEntryDistanceFromLocalHighPct { get; set; } = 0.12m;
    public int BreakoutHoldSnapshotCount { get; set; } = 2;
    public decimal MaxEntryDriftFromSignalPct { get; set; } = 0.10m;
    public decimal AntiChaseMinRangePositionPct { get; set; } = 35m;
    public int LowRangeMinConfirmations { get; set; } = 2;
    public decimal DriftAtrMultiple { get; set; } = 0.25m;

    // Upper-range breakouts need evidence that the breakout is being accepted, not
    // merely touched. Either recent candle momentum or snapshot price-action trend
    // may satisfy this floor.
    public decimal UpperBreakoutMinFollowThroughPct { get; set; } = 0.60m;

    // Mid-range reclaims are the common "looks bullish but goes nowhere" loss shape.
    // They need stronger live price-action follow-through than low-zone rebounds.
    public decimal MidRangeReclaimMinPriceActionTrendPct { get; set; } = 0.50m;

    // Directional efficiency separates a sustained move from price travelling back
    // and forth inside the same range. It only vetoes MID-zone non-breakout LONGs;
    // LOW rebounds and confirmed UPPER breakouts keep their existing rules.
    public int DirectionalEfficiencyLookbackCandles { get; set; } = 96;
    public decimal MinMidRangeDirectionalEfficiencyPct { get; set; } = 5m;

    // Low-range confirmations are not equally strong: a fresh upward tape (3 snapshots)
    // and the multi-candle momentum are structural, while a single positive snapshot
    // step and a single green candle are one-observation signals. Without this rule the
    // required count can be met by the two weakest signals alone, which is exactly the
    // shape of a dead-cat bounce inside a downtrend. When enabled, at least one of the
    // two structural confirmations must be present on top of the count.
    public bool LowRangeRequireStrongConfirmation { get; set; } = true;

    // LONG context/anti-knife gate (FuturesLongRangeGuard). Wick 24h position is
    // diagnostic only — mid-range reclaim after wide spikes is allowed. Late-entry
    // protection is FuturesEntryFreshnessGuard. Percent fields: 0.20 == 0.20%.
    public bool LongRangeGuardEnabled { get; set; } = true;

    // Soft diagnostic band for labeling / SQL (legacy name kept). No longer a hard
    // admit veto; breakouts and mid-range reclaim may pass above this level.
    public decimal Max24hRangePositionForLong { get; set; } = 30m;

    // Minimum confirmed rebound of the executable entry above the absolute 24h low,
    // in percent, so the bot does not buy while the low is still being made.
    public decimal MinReboundFrom24hLowPct { get; set; } = 0.20m;

    // Minimum consecutive rising snapshot steps required to confirm the reversal.
    public int RequiredRisingSnapshotCount { get; set; } = 2;

    // Whether the short-term snapshot slope must be strictly positive.
    public bool RequirePositiveShortSlope { get; set; } = true;

    // Whether a fresh upward tape (rising snapshots + non-negative candle momentum) is
    // mandatory for a lower-range LONG — an old candle-based signal alone is not enough.
    public bool RequireFreshTapeForLowRangeLong { get; set; } = true;

    // Minimum closed-candle sample before the robust percentile 24h range is used;
    // below it the guard falls back to the absolute 24h range and tags the source.
    public int RobustRangeMinSampleCount { get; set; } = 20;

    // Minimum 24h range width (percent of the range low). A narrower range makes the
    // position meaningless, so a LONG is rejected as LONG_24H_RANGE_TOO_NARROW.
    public decimal Min24hRangeWidthPct { get; set; } = 0.50m;
}

internal sealed class FuturesDipBounceOptions
{
    // Dip-bounce entry channel. When a LONG candidate's score sits below the firm
    // MinimumLongScore but at or above MinScore, and price is in the lower
    // NearLowMax24hRangePositionPct band of its 24h range (near the 24h low) WITH a
    // confirmed bounce (fresh upward snapshot tape + non-negative 15m candle
    // momentum — the same freshness the continuation channel uses), the entry is
    // promoted to a LONG. It never catches a falling knife: without the fresh
    // tape+momentum the candidate stays flat. Disable to fall back to the firm bar.
    public bool Enabled { get; set; } = true;

    // Upper bound of the "near the value-area low" zone using close-percentile
    // (0 = among the lowest recent closes, 100 = among the highest). Wick 24h
    // high-low is no longer the dip admit basis.
    public decimal NearLowMax24hRangePositionPct { get; set; } = 25m;

    // Relaxed minimum long score for this channel. Tunable without a recompile so
    // the sweet spot (0.70 / 0.72 / 0.75 / 0.78) can be searched from config/DB.
    public decimal MinScore { get; set; } = 0.70m;

    // Minimum recent 15m candle momentum (percent over
    // Freshness.ContinuationCandleMomentumLookback bars) required to admit a
    // dip-bounce. A LOWER-score entry needs a real up-tick, not merely a flat "not
    // falling" candle, so this is a small POSITIVE floor (default 0.10%) rather than
    // the continuation channel's non-negative >= 0. Tunable from config/DB.
    public decimal MinCandleMomentumPct { get; set; } = 0.10m;
}

internal sealed class FuturesFilterOptions
{
    // Tune later: minimum quoted 24h volume in USD for new entries.
    public decimal MinQuoteVolume24h { get; set; } = 50_000m;
    public decimal MinExitDepthMultiple { get; set; } = 5m;
    public decimal MaxExitImpactPct { get; set; } = 0.5m;
}

internal sealed class FuturesExitOptions
{
    // ATR stop multiplier: stopPct = clamp(StopAtrMult * atrPct, floor, cap).
    // Default 1.0 so a 0.75% ATR at the legacy floor stays ~flat vs old fixed 0.75% SL
    // when TargetRiskEur is calibrated to that risk; volatile names widen stop and shrink size.
    public decimal StopAtrMult { get; set; } = 1m;

    // Legacy name kept for older appsettings; Normalize maps it into StopDistanceCapPct
    // when the new cap is unset. Prefer StopDistanceCapPct going forward.
    public decimal MinStopAtrFloor { get; set; } = 1.5m;

    public decimal TakeProfitAtrMult { get; set; } = 3m;
    public decimal MinTpVsCostMult { get; set; } = 3m;

    // Minimum TP as a multiple of stop distance (R:R). Applied together with MinTpVsCostMult.
    public decimal MinRewardRiskMultiple { get; set; } = 2m;

    // Maximum ALLOWED stop distance (%). If StopAtrMult * atrPct exceeds this, the entry is
    // BLOCKED (STOP_DISTANCE_TOO_LARGE) — the sizer never silently shrinks the stop into the
    // instrument's own volatility.
    public decimal StopDistanceCapPct { get; set; } = 3m;

    // Optional TP cap (%). 0 = no cap beyond R-multiple / cost floor / TP floor.
    public decimal TakeProfitCapPct { get; set; } = 0m;

    // Stop/TP FLOORS (%). 0 = inherit the legacy TpSl.StopLossPercent / TpSl.TakeProfitPercent
    // (see Normalize). These decouple the risk-sizer floors from the legacy fixed-percent
    // names so config is unambiguous: MinStopDistancePct is a MINIMUM stop, not a fixed stop.
    public decimal MinStopDistancePct { get; set; } = 0m;
    public decimal MinTakeProfitPct { get; set; } = 0m;

    public decimal SlippageBufferPct { get; set; } = 0.10m;
    public int MaxHoldMinutes { get; set; } = 360;
    public decimal MaxHoldMinStopProgressPct { get; set; } = 60m;
    public bool MaxHoldForFlippedEntriesEnabled { get; set; }
    public decimal TrailingActivationBufferPct { get; set; } = 0m;
}

internal sealed class FuturesRegimeOptions
{
    public string BtcPair { get; set; } = "XBT/USD";
    public int BtcTrendMa { get; set; } = 50;
    public int BtcSlopeLookback { get; set; } = 3;
    public int BtcCrashLookback { get; set; } = 4;
    public decimal BtcCrashPct { get; set; } = 2.0m;
    public decimal LongOverrideMinScore { get; set; } = 0.85m;
    public decimal ShortOverrideMinScore { get; set; } = 0.85m;

    // Relative-strength gate for LOW-zone longs while the BTC regime blocks longs.
    // The point is to separate "this pair is genuinely flying while the market bleeds"
    // (a scalp worth taking) from "this pair is merely drifting down with everything
    // else" (a bounce inside a market-wide selloff).
    //
    // SHIPPED DISABLED ON PURPOSE. Relative strength is measured and recorded on every
    // decision, but it vetoes nothing until this flag is turned on, so current entry
    // behaviour is byte-for-byte unchanged. Turn it on only after the recorded data
    // shows it actually separates winners from losers.
    public bool RelativeStrengthGateEnabled { get; set; }

    // Minimum outperformance versus BTC over the shared candle lookback, in percent.
    // Only consulted when RelativeStrengthGateEnabled is true.
    public decimal MinRelativeStrengthPct { get; set; } = 0.5m;
}

internal sealed class FuturesShortOptions
{
    public decimal MaxChaseDrawdownPct { get; set; } = 3.0m;
    public decimal MinShortScore { get; set; } = 0.90m;

    // SHORT entry context + anti-knife gate (FuturesShortEntryGuard) — the mirror of the
    // LONG range/freshness guards. Wick 24h position is diagnostic only (mid-range
    // reclaim-down after wide spikes is allowed). Late-entry / rising-knife protection is
    // structural. Mechanical lookbacks / tape counts / breakdown buffer / candle momentum
    // are shared with Freshness (same magnitudes, mirrored direction). Percent fields:
    // 0.20 == 0.20%.
    public bool RangeGuardEnabled { get; set; } = true;

    // Mirror of Freshness.AntiChaseMinRangePositionPct (35) for the short side: anti-chase
    // (too close to the local LOW, drifted too far BELOW the signal) only makes sense in
    // the lower part of the range. At the TOP of the range there is nothing to chase
    // downwards, so above this position the two vetoes do not apply. Default 65 = the
    // mirror of the long threshold. Percent position in the primary range, 0..100.
    public decimal AntiChaseMaxRangePositionPct { get; set; } = 65m;

    // Diagnostic band for labeling (mirror of Max24hRangePositionForLong=30): a healthy
    // short usually sits in the UPPER band of the range, so this is a MIN position. Not a
    // hard veto — breakdowns and mid-range reclaim-down may pass below it.
    public decimal Min24hRangePositionForShort { get; set; } = 70m;

    // Minimum confirmed pullback of the executable entry BELOW the absolute 24h high, so
    // the bot does not sell while the high is still being made (mirror of rebound-from-low).
    public decimal MinPullbackFrom24hHighPct { get; set; } = 0.20m;

    // Minimum consecutive FALLING snapshot steps to confirm the down move.
    public int RequiredFallingSnapshotCount { get; set; } = 2;

    // Whether the short-term snapshot slope must be strictly negative.
    public bool RequireNegativeShortSlope { get; set; } = true;

    // Whether a fresh downward tape (falling snapshots + non-positive candle momentum) is
    // mandatory for an upper-range SHORT — an old candle-based signal alone is not enough.
    public bool RequireFreshTapeForHighRangeShort { get; set; } = true;
}

internal sealed class FuturesRiskOptions
{
    private decimal _targetRiskUsd = 1m;
    private decimal _maxConcurrentOpenRiskUsd = 3m;

    // USD stop-distance risk budget per new entry (stopPct × notional). Drives risk-based
    // sizing together with the ATR stop. Default 1.00 USD = 1% of the ~100 USD virtual
    // equity per position (a conservative live default; the old 3 USD ≈ 3% was too hot).
    public decimal TargetRiskUsd { get => _targetRiskUsd; set => _targetRiskUsd = value; }

    // Concurrent portfolio heat cap = pure stop-distance loss summed over open positions.
    // Normalize defaults it to TargetRiskUsd * MaxPositions so every slot can hold one
    // full-budget position and no more. Execution/slippage cost is bounded separately by
    // the notional caps and reported per trade (see FuturesPositionSizer.ProjectedOpenRiskEur).
    public decimal MaxConcurrentOpenRiskUsd { get => _maxConcurrentOpenRiskUsd; set => _maxConcurrentOpenRiskUsd = value; }

    // Compatibility aliases for the legacy generic persistence vocabulary.
    public decimal TargetRiskEur { get => _targetRiskUsd; set => _targetRiskUsd = value; }
    public decimal MaxConcurrentOpenRisk { get => _maxConcurrentOpenRiskUsd; set => _maxConcurrentOpenRiskUsd = value; }
    public decimal EstimatedEmergencyExitCostPct { get; set; } = 0m;
}

internal sealed class FuturesExecutionPolicyOptions
{
    public int CooldownAfterCloseSeconds { get; set; } = 1800;
    public int CooldownAfterStopLossSeconds { get; set; } = 14400;
    public int MinHoldSeconds { get; set; } = 1800;
    public int EntryBlackoutUtcFromHour { get; set; } = 22;
    public int EntryBlackoutMinutes { get; set; } = 360;
}

internal sealed class TpSlOptions
{
    public bool Enabled { get; set; } = true;

    // Bot-owned futures working TP/SL policy. The sizer may still use these as
    // floors when decoupled Exits.* values are unset, but open-position exits use
    // the fixed values below rather than ATR-derived entry-plan distances.
    public decimal TakeProfitPercent { get; set; } = 3m;
    public decimal StopLossPercent { get; set; } = 1m;

    // Exchange-side emergency protection is placed farther than the bot's working
    // levels. Example: working TP 3 and multiplier 200 => Kraken TP 6.
    public decimal ExchangeProtectionMultiplierPercent { get; set; } = 200m;

    // Once the bot-owned live position reaches the working TP, protective orders
    // are replaced with a reduce-only Kraken trailing stop at this distance.
    public decimal TrailingStopPercent { get; set; } = 2m;

    // Flipped LONG-to-SHORT entries use a separately calibrated profit handoff.
    // Their working stop remains StopLossPercent and exchange protection still
    // uses ExchangeProtectionMultiplierPercent.
    public decimal FlippedTakeProfitPercent { get; set; } = 1.5m;
    public decimal FlippedTrailingStopPercent { get; set; } = 0.75m;

    public decimal WorkingTakeProfitPercent(bool flippedEntry) =>
        flippedEntry ? FlippedTakeProfitPercent : TakeProfitPercent;

    public decimal WorkingTrailingStopPercent(bool flippedEntry) =>
        flippedEntry ? FlippedTrailingStopPercent : TrailingStopPercent;

    // For KRAKEN_SYNC / externally opened positions only: when the closeable live
    // price has travelled this percent of the way from entry to the existing
    // exchange TP order, replace existing reduce-only TP/SL with a trailing stop.
    // Set 0 to disable.
    public decimal ExternalTrailingActivationProgressPercent { get; set; } = 80m;

    // Which price stream triggers simulated TP/SL: "mark" | "index" | "last".
    // Only "mark"/"last" are meaningful for the virtual portfolio today.
    public string TriggerSource { get; set; } = "mark";

    // All simulated TP/SL exits are reduce-only by design; there is intentionally
    // no configuration switch for this (blueprint hard rule).
}
