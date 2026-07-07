using System.Text.Json;

namespace TradingBot.SpotWorker;

internal sealed class BotConfiguration
{
    public BotInstanceOptions BotInstance { get; set; } = new();
    public WorkerOptions Worker { get; set; } = new();
    public HttpOptions Http { get; set; } = new();
    public KrakenOptions Kraken { get; set; } = new();
    public AiOptions Ai { get; set; } = new();
    public LoggingOptions Logging { get; set; } = new();
    public TradingOptions Trading { get; set; } = new();
    public RiskOptions Risk { get; set; } = new();
    public StrategyOptions Strategy { get; set; } = new();
    public PositionSizingOptions PositionSizing { get; set; } = new();
    public PortfolioOptions Portfolio { get; set; } = new();
    public DryRunOptions DryRun { get; set; } = new();
    public DatabaseOptions Database { get; set; } = new();
    public ExecutionPolicyOptions ExecutionPolicy { get; set; } = new();
    public PositionExitOptions PositionExit { get; set; } = new();
    public CorrelationRiskOptions CorrelationRisk { get; set; } = new();
    public List<InstrumentOptions> CandidateUniverse { get; set; } = DefaultCandidateUniverse();

    public static BotConfiguration Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            path = Path.Combine(Directory.GetCurrentDirectory(), "src", "TradingBot.SpotWorker", "appsettings.json");
        }

        BotConfiguration config;
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            config = JsonSerializer.Deserialize<BotConfiguration>(json, JsonOptions()) ?? new BotConfiguration();
        }
        else
        {
            config = new BotConfiguration();
        }

        ApplyEnvironment(config);
        config.Normalize();
        return config;
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static void ApplyEnvironment(BotConfiguration config)
    {
        SetIfPresent("TRADINGBOT_BOT_INSTANCE_ID", value => config.BotInstance.Id = value);
        SetIfPresent("TRADINGBOT_BOT_INSTANCE_NAME", value => config.BotInstance.Name = value);
        SetIfPresent("TRADINGBOT_MARKET_DATA_MODE", value => config.Kraken.MarketDataMode = value);
        SetIfPresent("TRADINGBOT_KRAKEN_BASE_URL", value => config.Kraken.BaseUrl = value);
        SetIfPresent("TRADINGBOT_KRAKEN_API_KEY", value => config.Kraken.ApiKey = value);
        SetIfPresent("TRADINGBOT_KRAKEN_API_SECRET", value => config.Kraken.ApiSecret = value);
        SetIfPresent("TRADINGBOT_RUN_ONCE", value => config.Worker.RunOnce = ParseBool(value, config.Worker.RunOnce));
        SetIfPresent("TRADINGBOT_LOOP_INTERVAL_SECONDS", value => config.Worker.LoopIntervalSeconds = ParseInt(value, config.Worker.LoopIntervalSeconds));
        SetIfPresent("TRADINGBOT_TIMEFRAME_MINUTES", value => config.Trading.TimeframeMinutes = ParseInt(value, config.Trading.TimeframeMinutes));
        SetIfPresent("TRADINGBOT_MAX_ACTIVE_INSTRUMENTS", value => config.Trading.MaxActiveInstruments = ParseInt(value, config.Trading.MaxActiveInstruments));
        SetIfPresent("TRADINGBOT_LIVE_TRADING_ENABLED", value => config.Trading.LiveTradingEnabled = ParseBool(value, config.Trading.LiveTradingEnabled));
        SetIfPresent("TRADINGBOT_STRONG_MOVER_BACKFILL_ENABLED", value => config.Trading.StrongMoverBackfillEnabled = ParseBool(value, config.Trading.StrongMoverBackfillEnabled));
        SetIfPresent("TRADINGBOT_STRONG_MOVER_MIN_CHANGE_PERCENT", value => config.Trading.StrongMoverMinChangePercent = ParseDecimal(value, config.Trading.StrongMoverMinChangePercent));
        SetIfPresent("TRADINGBOT_STRONG_MOVER_MAX_SPREAD_PERCENT", value => config.Trading.StrongMoverMaxSpreadPercent = ParseDecimal(value, config.Trading.StrongMoverMaxSpreadPercent));
        SetIfPresent("TRADINGBOT_STRONG_MOVER_MIN_DAILY_VOLUME_EUR", value => config.Trading.StrongMoverMinDailyVolumeEur = ParseDecimal(value, config.Trading.StrongMoverMinDailyVolumeEur));
        SetIfPresent("TRADINGBOT_STRONG_MOVER_MAX_BACKFILL_PAIRS", value => config.Trading.StrongMoverMaxBackfillPairs = ParseInt(value, config.Trading.StrongMoverMaxBackfillPairs));
        SetIfPresent("TRADINGBOT_MAX_ORDER_EUR", value => config.Risk.MaxOrderEur = ParseDecimal(value, config.Risk.MaxOrderEur));
        SetIfPresent("TRADINGBOT_MINIMUM_EMA_GAP_PERCENT", value => config.Strategy.MinimumEmaGapPercent = ParseDecimal(value, config.Strategy.MinimumEmaGapPercent));
        SetIfPresent("TRADINGBOT_STARTING_CASH_EUR", value => config.Portfolio.StartingCashEur = ParseDecimal(value, config.Portfolio.StartingCashEur));
        SetIfPresent("TRADINGBOT_DRY_RUN_ENABLED", value => config.DryRun.Enabled = ParseBool(value, config.DryRun.Enabled));
        SetIfPresent("TRADINGBOT_DRY_RUN_APPLY_VIRTUAL_FILLS", value => config.DryRun.ApplyVirtualFills = ParseBool(value, config.DryRun.ApplyVirtualFills));
        SetIfPresent("TRADINGBOT_DRY_RUN_OUTPUT_DIRECTORY", value => config.DryRun.OutputDirectory = value);
        SetIfPresent("TRADINGBOT_DRY_RUN_TAKER_FEE_BPS", value => config.DryRun.TakerFeeBps = ParseDecimal(value, config.DryRun.TakerFeeBps));
        SetIfPresent("TRADINGBOT_DRY_RUN_SLIPPAGE_BPS", value => config.DryRun.SlippageBps = ParseDecimal(value, config.DryRun.SlippageBps));
        SetIfPresent("TRADINGBOT_DATABASE_ENABLED", value => config.Database.Enabled = ParseBool(value, config.Database.Enabled));
        SetIfPresent("TRADINGBOT_DATABASE_CONNECTION_STRING", value => config.Database.ConnectionString = value);
        SetIfPresent("TRADINGBOT_AI_PROVIDER", value => config.Ai.Provider = value);
        SetIfPresent("TRADINGBOT_AI_BASE_URL", value => config.Ai.BaseUrl = value);
        SetIfPresent("TRADINGBOT_OPENAI_API_KEY", value => config.Ai.ApiKey = value);
        SetIfPresent("TRADINGBOT_AI_MODEL", value => config.Ai.Model = value);
        SetIfPresent("TRADINGBOT_AI_MAX_RECOMMENDATIONS", value => config.Ai.MaxRecommendations = ParseInt(value, config.Ai.MaxRecommendations));
        SetIfPresent("TRADINGBOT_AI_WATCHLIST_REFRESH_SECONDS", value => config.Ai.WatchlistRefreshSeconds = ParseInt(value, config.Ai.WatchlistRefreshSeconds));
        SetIfPresent("TRADINGBOT_LOG_DIRECTORY", value => config.Logging.Directory = value);
        SetIfPresent("TRADINGBOT_EXECUTION_COOLDOWN_AFTER_BUY_SECONDS", value => config.ExecutionPolicy.CooldownAfterBuySeconds = ParseInt(value, config.ExecutionPolicy.CooldownAfterBuySeconds));
        SetIfPresent("TRADINGBOT_EXECUTION_COOLDOWN_AFTER_SELL_SECONDS", value => config.ExecutionPolicy.CooldownAfterSellSeconds = ParseInt(value, config.ExecutionPolicy.CooldownAfterSellSeconds));
        SetIfPresent("TRADINGBOT_EXECUTION_MIN_HOLD_SECONDS", value => config.ExecutionPolicy.MinHoldSeconds = ParseInt(value, config.ExecutionPolicy.MinHoldSeconds));
        SetIfPresent("TRADINGBOT_EXECUTION_ALLOW_IMMEDIATE_EXIT_ON_SIGNAL_FLIP", value => config.ExecutionPolicy.AllowImmediateExitOnSignalFlip = ParseBool(value, config.ExecutionPolicy.AllowImmediateExitOnSignalFlip));
        SetIfPresent("TRADINGBOT_POSITION_EXIT_MIN_PROFIT_ON_SIGNAL_FLIP_PERCENT", value => config.PositionExit.MinProfitToExitOnSignalFlipPercent = ParseDecimal(value, config.PositionExit.MinProfitToExitOnSignalFlipPercent));
        SetIfPresent("TRADINGBOT_POSITION_EXIT_STOP_LOSS_PERCENT", value => config.PositionExit.StopLossPercent = ParseDecimal(value, config.PositionExit.StopLossPercent ?? config.PositionExit.FixedStopLossPercent));
        SetIfPresent("TRADINGBOT_POSITION_EXIT_TAKE_PROFIT_PERCENT", value => config.PositionExit.TakeProfitPercent = ParseDecimal(value, config.PositionExit.TakeProfitPercent ?? config.PositionExit.FixedTakeProfitPercent));
        SetIfPresent("TRADINGBOT_POSITION_EXIT_MAX_HOLD_MINUTES", value => config.PositionExit.MaxHoldMinutes = ParseInt(value, config.PositionExit.MaxHoldMinutes));
        SetIfPresent("TRADINGBOT_STRATEGY_EXIT_EMA_GAP_PERCENT", value => config.Strategy.ExitEmaGapPercent = ParseDecimal(value, config.Strategy.ExitEmaGapPercent));
        SetIfPresent("TRADINGBOT_POSITION_EXIT_TRAILING_ACTIVATION_PERCENT", value => config.PositionExit.TrailingActivationPercent = ParseDecimal(value, config.PositionExit.TrailingActivationPercent));
        SetIfPresent("TRADINGBOT_POSITION_EXIT_TRAILING_DISTANCE_PERCENT", value => config.PositionExit.TrailingDistancePercent = ParseDecimal(value, config.PositionExit.TrailingDistancePercent));
        SetIfPresent("TRADINGBOT_STRATEGY_MIN_TAKE_PROFIT_TO_FRICTION_RATIO", value => config.Strategy.MinTakeProfitToFrictionRatio = ParseDecimal(value, config.Strategy.MinTakeProfitToFrictionRatio));
        SetIfPresent("TRADINGBOT_EXECUTION_ENTRY_BLACKOUT_UTC_FROM_HOUR", value => config.ExecutionPolicy.EntryBlackoutUtcFromHour = ParseInt(value, config.ExecutionPolicy.EntryBlackoutUtcFromHour));
        SetIfPresent("TRADINGBOT_EXECUTION_ENTRY_BLACKOUT_MINUTES", value => config.ExecutionPolicy.EntryBlackoutMinutes = ParseInt(value, config.ExecutionPolicy.EntryBlackoutMinutes));
        SetIfPresent("TRADINGBOT_EXECUTION_MAX_NEW_POSITIONS_PER_HOUR", value => config.ExecutionPolicy.MaxNewPositionsPerHour = ParseInt(value, config.ExecutionPolicy.MaxNewPositionsPerHour));
        SetIfPresent("TRADINGBOT_EXECUTION_COOLDOWN_AFTER_STOP_LOSS_SECONDS", value => config.ExecutionPolicy.CooldownAfterStopLossSeconds = ParseInt(value, config.ExecutionPolicy.CooldownAfterStopLossSeconds));
        SetIfPresent("TRADINGBOT_POSITION_EXIT_MAX_SIGNAL_FLIP_LOSS_EXIT_PERCENT", value => config.PositionExit.MaxSignalFlipLossExitPercent = ParseDecimal(value, config.PositionExit.MaxSignalFlipLossExitPercent));
        SetIfPresent("TRADINGBOT_STRATEGY_MINIMUM_LONG_SCORE", value => config.Strategy.MinimumLongScore = ParseDecimal(value, config.Strategy.MinimumLongScore));
        SetIfPresent("TRADINGBOT_STRATEGY_EXPLORATORY_ENTRIES_ENABLED", value => config.Strategy.ExploratoryEntriesEnabled = ParseBool(value, config.Strategy.ExploratoryEntriesEnabled));
        SetIfPresent("TRADINGBOT_STRATEGY_EXPLORATORY_MINIMUM_LONG_SCORE", value => config.Strategy.ExploratoryMinimumLongScore = ParseDecimal(value, config.Strategy.ExploratoryMinimumLongScore));
        SetIfPresent("TRADINGBOT_STRATEGY_EXPLORATORY_MIN_BULLISH_EMA_GAP_PERCENT", value => config.Strategy.ExploratoryMinBullishEmaGapPercent = ParseDecimal(value, config.Strategy.ExploratoryMinBullishEmaGapPercent));
        SetIfPresent("TRADINGBOT_STRATEGY_EXPLORATORY_MIN_EMA_GAP_VELOCITY_PERCENT", value => config.Strategy.ExploratoryMinEmaGapVelocityPercent = ParseDecimal(value, config.Strategy.ExploratoryMinEmaGapVelocityPercent));
        SetIfPresent("TRADINGBOT_STRATEGY_EXPLORATORY_MIN_PRICE_ACTION_TREND_PERCENT", value => config.Strategy.ExploratoryMinPriceActionTrendPercent = ParseDecimal(value, config.Strategy.ExploratoryMinPriceActionTrendPercent));
        SetIfPresent("TRADINGBOT_STRATEGY_PRICE_ACTION_MAX_DECLINE_PERCENT", value => config.Strategy.PriceActionMaxDeclinePercent = ParseDecimal(value, config.Strategy.PriceActionMaxDeclinePercent));
        SetIfPresent("TRADINGBOT_STRATEGY_MISSING_VOLUME_SCORE_CAP", value => config.Strategy.MissingVolumeScoreCap = ParseDecimal(value, config.Strategy.MissingVolumeScoreCap));
        SetIfPresent("TRADINGBOT_STRATEGY_PRICE_ACTION_LOOKBACK_SNAPSHOTS", value => config.Strategy.PriceActionLookbackSnapshots = ParseInt(value, config.Strategy.PriceActionLookbackSnapshots));
        SetIfPresent("TRADINGBOT_STRATEGY_PRICE_ACTION_MIN_SNAPSHOTS", value => config.Strategy.PriceActionMinSnapshots = ParseInt(value, config.Strategy.PriceActionMinSnapshots));
        SetIfPresent("TRADINGBOT_STRATEGY_PRICE_ACTION_MAX_NON_RISING_SNAPSHOTS", value => config.Strategy.PriceActionMaxNonRisingSnapshots = ParseInt(value, config.Strategy.PriceActionMaxNonRisingSnapshots));
        SetIfPresent("TRADINGBOT_STRATEGY_NEGATIVE_PRICE_ACTION_PENALTY", value => config.Strategy.NegativePriceActionPenalty = ParseDecimal(value, config.Strategy.NegativePriceActionPenalty));
        SetIfPresent("TRADINGBOT_STRATEGY_MIN_DAILY_VOLUME_EUR", value => config.Strategy.MinDailyVolumeEur = ParseDecimal(value, config.Strategy.MinDailyVolumeEur));
        SetIfPresent("TRADINGBOT_STRATEGY_EXPLORATORY_MAX_RANK", value => config.Strategy.ExploratoryMaxRank = ParseInt(value, config.Strategy.ExploratoryMaxRank));
        SetIfPresent("TRADINGBOT_STRATEGY_EXPLORATORY_ALLOWED_IN_LIVE", value => config.Strategy.ExploratoryAllowedInLive = ParseBool(value, config.Strategy.ExploratoryAllowedInLive));
        SetIfPresent("TRADINGBOT_STRATEGY_EARLY_ENTRY_ENABLED", value => config.Strategy.EarlyEntryEnabled = ParseBool(value, config.Strategy.EarlyEntryEnabled));
        SetIfPresent("TRADINGBOT_STRATEGY_EARLY_ENTRY_ALLOWED_IN_LIVE", value => config.Strategy.EarlyEntryAllowedInLive = ParseBool(value, config.Strategy.EarlyEntryAllowedInLive));
        SetIfPresent("TRADINGBOT_STRATEGY_EARLY_ENTRY_MIN_SCORE", value => config.Strategy.EarlyEntryMinScore = ParseDecimal(value, config.Strategy.EarlyEntryMinScore));
        SetIfPresent("TRADINGBOT_STRATEGY_EARLY_ENTRY_MIN_EMA_GAP_PERCENT", value => config.Strategy.EarlyEntryMinEmaGapPercent = ParseDecimal(value, config.Strategy.EarlyEntryMinEmaGapPercent));
        SetIfPresent("TRADINGBOT_STRATEGY_EARLY_ENTRY_MIN_GAP_VELOCITY_PERCENT", value => config.Strategy.EarlyEntryMinGapVelocityPercent = ParseDecimal(value, config.Strategy.EarlyEntryMinGapVelocityPercent));
        SetIfPresent("TRADINGBOT_STRATEGY_EARLY_ENTRY_MIN_PRICE_ACTION_TREND_PERCENT", value => config.Strategy.EarlyEntryMinPriceActionTrendPercent = ParseDecimal(value, config.Strategy.EarlyEntryMinPriceActionTrendPercent));
        SetIfPresent("TRADINGBOT_STRATEGY_EARLY_ENTRY_MAX_RANK", value => config.Strategy.EarlyEntryMaxRank = ParseInt(value, config.Strategy.EarlyEntryMaxRank));
        SetIfPresent("TRADINGBOT_STRATEGY_MAX_ENTRY_EXTENSION_PERCENT", value => config.Strategy.MaxEntryExtensionPercent = ParseDecimal(value, config.Strategy.MaxEntryExtensionPercent));
        SetIfPresent("TRADINGBOT_STRATEGY_MAX_ENTRY_RUNUP_PERCENT", value => config.Strategy.MaxEntryRunupPercent = ParseDecimal(value, config.Strategy.MaxEntryRunupPercent));
        SetIfPresent("TRADINGBOT_STRATEGY_NEGATIVE_PRICE_ACTION_PENALTY_THRESHOLD_PERCENT", value => config.Strategy.NegativePriceActionPenaltyThresholdPercent = ParseDecimal(value, config.Strategy.NegativePriceActionPenaltyThresholdPercent));
        SetIfPresent("TRADINGBOT_STRATEGY_REGIME_FILTER_MAX_BTC_DECLINE_PERCENT", value => config.Strategy.RegimeFilterMaxBtcDeclinePercent = ParseDecimal(value, config.Strategy.RegimeFilterMaxBtcDeclinePercent));
        SetIfPresent("TRADINGBOT_STRATEGY_REGIME_FILTER_MIN_BREADTH_PERCENT", value => config.Strategy.RegimeFilterMinBreadthPercent = ParseDecimal(value, config.Strategy.RegimeFilterMinBreadthPercent));
        SetIfPresent("TRADINGBOT_STRATEGY_REGIME_FILTER_REFERENCE_PAIR", value => config.Strategy.RegimeFilterReferencePair = value);
        SetIfPresent("TRADINGBOT_STRATEGY_REQUIRE_PRICE_ACTION_DATA", value => config.Strategy.RequirePriceActionData = ParseBool(value, config.Strategy.RequirePriceActionData));
        SetIfPresent("TRADINGBOT_STRATEGY_MAX_EXPLORATORY_SPREAD_PERCENT", value => config.Strategy.MaxExploratorySpreadPercent = ParseDecimal(value, config.Strategy.MaxExploratorySpreadPercent));
        SetIfPresent("TRADINGBOT_STRATEGY_PRICE_ACTION_MAX_SAMPLE_AGE_MINUTES", value => config.Strategy.PriceActionMaxSampleAgeMinutes = ParseInt(value, config.Strategy.PriceActionMaxSampleAgeMinutes));
        SetIfPresent("TRADINGBOT_STRATEGY_PRICE_ACTION_HYDRATION_MINUTES", value => config.Strategy.PriceActionHydrationMinutes = ParseInt(value, config.Strategy.PriceActionHydrationMinutes));
        SetIfPresent("TRADINGBOT_STRATEGY_ALLOW_ENTRIES_WITHOUT_PRICE_ACTION_IN_LIVE", value => config.Strategy.AllowEntriesWithoutPriceActionInLive = ParseBool(value, config.Strategy.AllowEntriesWithoutPriceActionInLive));
        SetIfPresent("TRADINGBOT_POSITION_EXIT_SCORE_DECAY_MIN_ENTRY_SCORE", value => config.PositionExit.ScoreDecayMinEntryScore = ParseDecimal(value, config.PositionExit.ScoreDecayMinEntryScore));
        SetIfPresent("TRADINGBOT_POSITION_EXIT_SCORE_DECAY_DEFENSIVE_SCORE", value => config.PositionExit.ScoreDecayDefensiveScore = ParseDecimal(value, config.PositionExit.ScoreDecayDefensiveScore));
        SetIfPresent("TRADINGBOT_POSITION_EXIT_SCORE_DECAY_DEFENSIVE_CYCLES", value => config.PositionExit.ScoreDecayDefensiveCycles = ParseInt(value, config.PositionExit.ScoreDecayDefensiveCycles));
        SetIfPresent("TRADINGBOT_POSITION_EXIT_SCORE_DECAY_IMMEDIATE_SCORE", value => config.PositionExit.ScoreDecayImmediateScore = ParseDecimal(value, config.PositionExit.ScoreDecayImmediateScore));
        SetIfPresent("TRADINGBOT_POSITION_EXIT_POST_ENTRY_ADVERSE_WINDOW_MINUTES", value => config.PositionExit.PostEntryAdverseWindowMinutes = ParseInt(value, config.PositionExit.PostEntryAdverseWindowMinutes));
        SetIfPresent("TRADINGBOT_POSITION_EXIT_POST_ENTRY_ADVERSE_LOSS_PERCENT", value => config.PositionExit.PostEntryAdverseLossPercent = ParseDecimal(value, config.PositionExit.PostEntryAdverseLossPercent));
    }

    private void Normalize()
    {
        Worker.LoopIntervalSeconds = Math.Max(10, Worker.LoopIntervalSeconds);
        BotInstance.Id = BotInstanceId.Normalize(BotInstance.Id);
        BotInstance.Name = string.IsNullOrWhiteSpace(BotInstance.Name) ? BotInstance.Id : BotInstance.Name.Trim();
        Http.TimeoutSeconds = Math.Clamp(Http.TimeoutSeconds, 5, 120);
        Trading.TimeframeMinutes = Trading.TimeframeMinutes <= 0 ? 5 : Trading.TimeframeMinutes;
        Trading.MaxActiveInstruments = Math.Max(1, Trading.MaxActiveInstruments);
        Trading.TargetOrderEur = Trading.TargetOrderEur <= 0 ? 3m : Trading.TargetOrderEur;
        Trading.StrongMoverMinChangePercent = Math.Max(0m, Trading.StrongMoverMinChangePercent);
        Trading.StrongMoverMaxSpreadPercent = Math.Max(0m, Trading.StrongMoverMaxSpreadPercent);
        Trading.StrongMoverMinDailyVolumeEur = Math.Max(0m, Trading.StrongMoverMinDailyVolumeEur);
        Trading.StrongMoverMaxBackfillPairs = Math.Max(0, Trading.StrongMoverMaxBackfillPairs);
        Risk.MaxOrderEur = Risk.MaxOrderEur <= 0 ? 3m : Risk.MaxOrderEur;
        Risk.MaxDailyLossEur = Risk.MaxDailyLossEur <= 0 ? 10m : Risk.MaxDailyLossEur;
        Risk.MaxOpenPositions = Math.Max(1, Risk.MaxOpenPositions);
        Risk.MaxTotalExposureEur = Math.Max(0m, Risk.MaxTotalExposureEur);
        Strategy.MinimumEmaGapPercent = Math.Max(0m, Strategy.MinimumEmaGapPercent);
        Strategy.MinimumLongScore = Math.Clamp(Strategy.MinimumLongScore, 0m, 1m);
        // Keep the ideal band inside the hardcoded acceptable band (35..68) so a
        // misconfigured ideal range can never carve out a silent RSI dead zone that
        // scores neither ideal nor acceptable.
        Strategy.RsiIdealMin = Math.Clamp(Strategy.RsiIdealMin, 35m, 68m);
        Strategy.RsiIdealMax = Math.Clamp(Strategy.RsiIdealMax, Strategy.RsiIdealMin, 68m);
        Strategy.MomentumLookbackBars = Math.Max(1, Strategy.MomentumLookbackBars);
        Strategy.MomentumMinPercent = Math.Max(0m, Strategy.MomentumMinPercent);
        Strategy.TrendFilterMaPeriod = Math.Max(2, Strategy.TrendFilterMaPeriod);
        Strategy.VolumeConfirmationMultiple = Math.Max(1m, Strategy.VolumeConfirmationMultiple);
        Strategy.MaxEntrySpreadPercent = Math.Max(0m, Strategy.MaxEntrySpreadPercent);
        Strategy.ExitEmaGapPercent = Math.Max(0m, Strategy.ExitEmaGapPercent);
        Strategy.PriceActionLookbackSnapshots = Math.Max(1, Strategy.PriceActionLookbackSnapshots);
        Strategy.PriceActionMinSnapshots = Math.Max(2, Strategy.PriceActionMinSnapshots);
        Strategy.PriceActionMaxDeclinePercent = Math.Max(0m, Strategy.PriceActionMaxDeclinePercent);
        Strategy.PriceActionMaxNonRisingSnapshots = Math.Max(0, Strategy.PriceActionMaxNonRisingSnapshots);
        Strategy.NegativePriceActionPenalty = Math.Max(0m, Strategy.NegativePriceActionPenalty);
        Strategy.MissingVolumeScoreCap = Math.Clamp(Strategy.MissingVolumeScoreCap, 0m, 1m);
        Strategy.MinDailyVolumeEur = Math.Max(0m, Strategy.MinDailyVolumeEur);
        Strategy.ExploratoryMinimumLongScore = Math.Clamp(Strategy.ExploratoryMinimumLongScore, 0m, Strategy.MinimumLongScore);
        Strategy.ExploratoryMinBullishEmaGapPercent = Math.Max(0m, Strategy.ExploratoryMinBullishEmaGapPercent);
        Strategy.ExploratoryMaxRank = Math.Max(1, Strategy.ExploratoryMaxRank);
        Strategy.MaxExploratorySpreadPercent = Math.Max(0m, Strategy.MaxExploratorySpreadPercent);
        Strategy.ExploratoryMinPriceActionTrendPercent = Math.Max(0m, Strategy.ExploratoryMinPriceActionTrendPercent);
        Strategy.PriceActionMaxSampleAgeMinutes = Math.Max(0, Strategy.PriceActionMaxSampleAgeMinutes);
        Strategy.PriceActionHydrationMinutes = Math.Max(0, Strategy.PriceActionHydrationMinutes);
        Strategy.RegimeFilterMaxBtcDeclinePercent = Math.Max(0m, Strategy.RegimeFilterMaxBtcDeclinePercent);
        Strategy.RegimeFilterMinBreadthPercent = Math.Clamp(Strategy.RegimeFilterMinBreadthPercent, 0m, 100m);
        Strategy.RegimeFilterReferencePair = string.IsNullOrWhiteSpace(Strategy.RegimeFilterReferencePair) ? "XBT/EUR" : Strategy.RegimeFilterReferencePair.Trim();
        Strategy.EarlyEntryMinScore = Math.Clamp(Strategy.EarlyEntryMinScore, 0m, 1m);
        Strategy.EarlyEntryMinEmaGapPercent = Math.Max(0m, Strategy.EarlyEntryMinEmaGapPercent);
        Strategy.EarlyEntryMaxRank = Math.Max(1, Strategy.EarlyEntryMaxRank);
        Strategy.MaxEntryExtensionPercent = Math.Max(0m, Strategy.MaxEntryExtensionPercent);
        Strategy.MaxEntryRunupPercent = Math.Max(0m, Strategy.MaxEntryRunupPercent);
        Strategy.NegativePriceActionPenaltyThresholdPercent = Math.Max(0m, Strategy.NegativePriceActionPenaltyThresholdPercent);
        // Exploratory sampling is a dry-run tool. Live trading keeps the firm
        // threshold unless the operator explicitly opts in.
        if (Trading.LiveTradingEnabled && !Strategy.ExploratoryAllowedInLive)
        {
            Strategy.ExploratoryEntriesEnabled = false;
        }
        // The early-entry channel needs its own explicit live opt-in: it is designed
        // for live use (that is the whole point of entering earlier) but must never
        // reach real money by accident of a copied config.
        if (Trading.LiveTradingEnabled && !Strategy.EarlyEntryAllowedInLive)
        {
            Strategy.EarlyEntryEnabled = false;
        }
        // Live entries must never bypass the anti-lag guard via missing history: force
        // the warm-up requirement on whenever live trading is enabled. The ONLY way
        // out is the explicit AllowEntriesWithoutPriceActionInLive override — UNKNOWN
        // price action never silently becomes a safe condition.
        if (Trading.LiveTradingEnabled && !Strategy.AllowEntriesWithoutPriceActionInLive)
        {
            Strategy.RequirePriceActionData = true;
        }
        NormalizePositionSizing();
        Portfolio.StartingCashEur = Portfolio.StartingCashEur < 0 ? 0m : Portfolio.StartingCashEur;
        Logging.Directory = string.IsNullOrWhiteSpace(Logging.Directory) ? "logs" : Logging.Directory.Trim();
        DryRun.OutputDirectory = string.IsNullOrWhiteSpace(DryRun.OutputDirectory) ? "data/dry-run" : DryRun.OutputDirectory.Trim();
        DryRun.StateFile = string.IsNullOrWhiteSpace(DryRun.StateFile) ? "portfolio-state.json" : DryRun.StateFile.Trim();
        DryRun.EventsFile = string.IsNullOrWhiteSpace(DryRun.EventsFile) ? "events.jsonl" : DryRun.EventsFile.Trim();
        DryRun.TakerFeeBps = Math.Max(0m, DryRun.TakerFeeBps);
        DryRun.SlippageBps = Math.Max(0m, DryRun.SlippageBps);
        Database.ConnectionString = Database.ConnectionString.Trim();
        ExecutionPolicy.CooldownAfterBuySeconds = Math.Max(0, ExecutionPolicy.CooldownAfterBuySeconds);
        ExecutionPolicy.CooldownAfterSellSeconds = Math.Max(0, ExecutionPolicy.CooldownAfterSellSeconds);
        ExecutionPolicy.MinHoldSeconds = Math.Max(0, ExecutionPolicy.MinHoldSeconds);
        ExecutionPolicy.MaxNewPositionsPerCycle = Math.Max(0, ExecutionPolicy.MaxNewPositionsPerCycle);
        ExecutionPolicy.EntryBlackoutUtcFromHour = Math.Clamp(ExecutionPolicy.EntryBlackoutUtcFromHour, 0, 23);
        ExecutionPolicy.EntryBlackoutMinutes = Math.Max(0, ExecutionPolicy.EntryBlackoutMinutes);
        ExecutionPolicy.MaxNewPositionsPerHour = Math.Max(0, ExecutionPolicy.MaxNewPositionsPerHour);
        ExecutionPolicy.CooldownAfterStopLossSeconds = Math.Max(0, ExecutionPolicy.CooldownAfterStopLossSeconds);
        PositionExit.MinProfitToExitOnSignalFlipPercent = Math.Max(0m, PositionExit.MinProfitToExitOnSignalFlipPercent);
        PositionExit.StopLossPercent = PositionExit.StopLossPercent is { } legacyStop ? Math.Max(0m, legacyStop) : null;
        PositionExit.TakeProfitPercent = PositionExit.TakeProfitPercent is { } legacyTakeProfit ? Math.Max(0m, legacyTakeProfit) : null;
        PositionExit.Mode = PositionExitOptions.NormalizeMode(PositionExit.Mode);
        PositionExit.AtrPeriod = Math.Max(1, PositionExit.AtrPeriod);
        PositionExit.StopLossAtrMultiplier = Math.Max(0m, PositionExit.StopLossAtrMultiplier);
        PositionExit.TakeProfitAtrMultiplier = Math.Max(0m, PositionExit.TakeProfitAtrMultiplier);
        Strategy.MinTakeProfitToFrictionRatio = Math.Max(0m, Strategy.MinTakeProfitToFrictionRatio);
        PositionExit.FixedStopLossPercent = PositionExit.StopLossPercent is > 0m
            ? PositionExit.StopLossPercent.Value
            : Math.Max(0m, PositionExit.FixedStopLossPercent);
        PositionExit.FixedTakeProfitPercent = PositionExit.TakeProfitPercent is > 0m
            ? PositionExit.TakeProfitPercent.Value
            : Math.Max(0m, PositionExit.FixedTakeProfitPercent);
        if (PositionExit.IsAtrMode
            && PositionExit.TakeProfitAtrMultiplier > 0m
            && PositionExit.StopLossAtrMultiplier > 0m
            && PositionExit.TakeProfitAtrMultiplier < PositionExit.StopLossAtrMultiplier)
        {
            throw new InvalidOperationException(
                $"Invalid PositionExit ATR risk/reward: TakeProfitAtrMultiplier {PositionExit.TakeProfitAtrMultiplier:0.###} is below StopLossAtrMultiplier {PositionExit.StopLossAtrMultiplier:0.###} (risk/reward < 1).");
        }
        PositionExit.MaxHoldMinutes = Math.Max(0, PositionExit.MaxHoldMinutes);
        PositionExit.TrailingActivationPercent = Math.Max(0m, PositionExit.TrailingActivationPercent);
        PositionExit.TrailingDistancePercent = Math.Max(0m, PositionExit.TrailingDistancePercent);
        // Loss floor for confirmed bearish flips is a loss (<= 0); a positive value is
        // nonsensical, so clamp it up to 0 (which also disables the mechanism).
        PositionExit.MaxSignalFlipLossExitPercent = Math.Min(0m, PositionExit.MaxSignalFlipLossExitPercent);
        PositionExit.ScoreDecayMinEntryScore = Math.Clamp(PositionExit.ScoreDecayMinEntryScore, 0m, 1m);
        PositionExit.ScoreDecayDefensiveScore = Math.Clamp(PositionExit.ScoreDecayDefensiveScore, 0m, 1m);
        PositionExit.ScoreDecayDefensiveCycles = Math.Max(0, PositionExit.ScoreDecayDefensiveCycles);
        PositionExit.ScoreDecayImmediateScore = Math.Clamp(PositionExit.ScoreDecayImmediateScore, 0m, 1m);
        PositionExit.PostEntryAdverseWindowMinutes = Math.Max(0, PositionExit.PostEntryAdverseWindowMinutes);
        PositionExit.PostEntryAdverseLossPercent = Math.Max(0m, PositionExit.PostEntryAdverseLossPercent);
        CorrelationRisk.MaxOpenPositionsPerGroup = Math.Max(0, CorrelationRisk.MaxOpenPositionsPerGroup);
        CorrelationRisk.MaxExposureEurPerGroup = Math.Max(0m, CorrelationRisk.MaxExposureEurPerGroup);
        CorrelationRisk.MaxHighBetaPositions = Math.Max(0, CorrelationRisk.MaxHighBetaPositions);
        CorrelationRisk.MaxHighBetaExposureEur = Math.Max(0m, CorrelationRisk.MaxHighBetaExposureEur);
        Portfolio.Positions = Portfolio.Positions
            .Where(position => !string.IsNullOrWhiteSpace(position.Pair))
            .Select(position =>
            {
                position.Pair = NormalizePair(position.Pair);
                position.Side = string.IsNullOrWhiteSpace(position.Side) ? "LONG" : position.Side.Trim().ToUpperInvariant();
                position.Quantity = Math.Max(0m, position.Quantity);
                position.EntryPrice = Math.Max(0m, position.EntryPrice);
                position.EntryNotionalEur = position.EntryNotionalEur <= 0m
                    ? position.Quantity * position.EntryPrice
                    : position.EntryNotionalEur;
                return position;
            })
            .Where(position => position.Quantity > 0m)
            .ToList();
        Ai.MaxRecommendations = Math.Max(1, Ai.MaxRecommendations);
        Ai.WatchlistRefreshSeconds = Math.Max(0, Ai.WatchlistRefreshSeconds);
        CandidateUniverse = CandidateUniverse
            .Where(item => item.Enabled)
            .Where(item => !string.IsNullOrWhiteSpace(item.Pair))
            .Select(item =>
            {
                item.Venue = string.IsNullOrWhiteSpace(item.Venue) ? "Kraken" : item.Venue.Trim();
                item.Pair = NormalizePair(item.Pair);
                item.KrakenPair = string.IsNullOrWhiteSpace(item.KrakenPair)
                    ? item.Pair.Replace("/", string.Empty, StringComparison.Ordinal)
                    : item.KrakenPair.Trim();
                return item;
            })
            .ToList();

        if (CandidateUniverse.Count == 0)
        {
            CandidateUniverse = DefaultCandidateUniverse();
        }
    }

    private void NormalizePositionSizing()
    {
        PositionSizing.CashReserveEur = Math.Max(0m, PositionSizing.CashReserveEur);
        PositionSizing.SmallOrderEur = PositionSizing.SmallOrderEur <= 0m ? Trading.TargetOrderEur : PositionSizing.SmallOrderEur;
        PositionSizing.BaseOrderEur = PositionSizing.BaseOrderEur <= 0m ? Trading.TargetOrderEur : PositionSizing.BaseOrderEur;
        PositionSizing.StrongOrderEur = PositionSizing.StrongOrderEur <= 0m ? PositionSizing.BaseOrderEur : PositionSizing.StrongOrderEur;
        PositionSizing.VeryStrongOrderEur = PositionSizing.VeryStrongOrderEur <= 0m ? PositionSizing.StrongOrderEur : PositionSizing.VeryStrongOrderEur;
        PositionSizing.MaxOrderEur = PositionSizing.MaxOrderEur <= 0m ? PositionSizing.VeryStrongOrderEur : PositionSizing.MaxOrderEur;
        PositionSizing.BaseScoreThreshold = Math.Clamp(PositionSizing.BaseScoreThreshold, 0m, 1m);
        PositionSizing.StrongScoreThreshold = Math.Clamp(PositionSizing.StrongScoreThreshold, 0m, 1m);
        PositionSizing.VeryStrongScoreThreshold = Math.Clamp(PositionSizing.VeryStrongScoreThreshold, 0m, 1m);
        PositionSizing.StrongEmaGapScoreThreshold = Math.Clamp(PositionSizing.StrongEmaGapScoreThreshold, 0m, 1m);
        PositionSizing.StrongEmaGapPercent = Math.Max(0m, PositionSizing.StrongEmaGapPercent);
    }

    private static string NormalizePair(string pair)
    {
        var trimmed = pair.Trim().ToUpperInvariant();
        if (trimmed.Contains('/', StringComparison.Ordinal))
        {
            return trimmed;
        }

        if (trimmed.EndsWith("EUR", StringComparison.Ordinal) && trimmed.Length > 3)
        {
            return $"{trimmed[..^3]}/EUR";
        }

        return trimmed;
    }

    private static List<InstrumentOptions> DefaultCandidateUniverse() => new()
    {
        new InstrumentOptions { Pair = "SOL/EUR", KrakenPair = "SOLEUR", Venue = "Kraken", Enabled = true },
        new InstrumentOptions { Pair = "BTC/EUR", KrakenPair = "BTCEUR", Venue = "Kraken", Enabled = true },
        new InstrumentOptions { Pair = "ETH/EUR", KrakenPair = "ETHEUR", Venue = "Kraken", Enabled = true }
    };

    private static void SetIfPresent(string name, Action<string> apply)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            apply(value.Trim());
        }
    }

    private static int ParseInt(string value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;

    private static decimal ParseDecimal(string value, decimal fallback) =>
        decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static bool ParseBool(string value, bool fallback) =>
        bool.TryParse(value, out var parsed) ? parsed : fallback;

}

internal sealed class RiskOptions
{
    public decimal MaxOrderEur { get; set; } = 3m;
    public decimal MaxDailyLossEur { get; set; } = 10m;
    public int MaxOpenPositions { get; set; } = 1;
    public decimal MaxTotalExposureEur { get; set; } = 0m;
    public bool KillSwitch { get; set; } = false;
}

internal sealed class PositionSizingOptions
{
    public bool Enabled { get; set; } = false;
    public decimal CashReserveEur { get; set; } = 0m;
    public decimal SmallOrderEur { get; set; } = 5m;
    public decimal BaseOrderEur { get; set; } = 10m;
    public decimal StrongOrderEur { get; set; } = 15m;
    public decimal VeryStrongOrderEur { get; set; } = 20m;
    public decimal MaxOrderEur { get; set; } = 20m;
    public decimal BaseScoreThreshold { get; set; } = 0.75m;
    public decimal StrongScoreThreshold { get; set; } = 0.88m;
    public decimal VeryStrongScoreThreshold { get; set; } = 0.94m;
    public decimal StrongEmaGapScoreThreshold { get; set; } = 0.85m;
    public decimal StrongEmaGapPercent { get; set; } = 0.50m;
}

internal sealed class PortfolioOptions
{
    public decimal StartingCashEur { get; set; } = 50m;
    public List<PositionOptions> Positions { get; set; } = new();
}

internal sealed class PositionOptions
{
    public string Pair { get; set; } = string.Empty;
    public string Side { get; set; } = "LONG";
    public decimal Quantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal EntryNotionalEur { get; set; }
}

internal sealed class ExecutionPolicyOptions
{
    // Minimum seconds to wait after a buy before another buy for the same pair is allowed.
    public int CooldownAfterBuySeconds { get; set; } = 900;

    // Minimum seconds to wait after a sell before buying the same pair again.
    public int CooldownAfterSellSeconds { get; set; } = 1800;

    // Minimum seconds a freshly opened position must be held before an ordinary
    // strategy signal-flip exit is allowed. Hard exits (stop-loss, take-profit,
    // trailing stop, kill switch, emergency risk, broker safety) always bypass this.
    public int MinHoldSeconds { get; set; } = 900;

    // Maximum new positions that may be opened in one decision cycle. Set to 0 to
    // disable the per-cycle throttle.
    public int MaxNewPositionsPerCycle { get; set; } = 0;

    // When true, disables the minimum-hold guard so a signal flip can close a
    // position immediately. Default false to avoid buy/sell churn on noisy flips.
    public bool AllowImmediateExitOnSignalFlip { get; set; } = false;

    // UTC-midnight (or configured hour) entry blackout. Kraken Ticker's 'o' resets at
    // 00:00 UTC, so early-UTC change data is meaningless and liquidity is thin. During
    // [FromHour:00, FromHour:00 + Minutes) NEW entries are blocked; exits and held
    // management are unaffected. EntryBlackoutMinutes = 0 disables the window.
    public int EntryBlackoutUtcFromHour { get; set; } = 0;
    public int EntryBlackoutMinutes { get; set; } = 60;

    // Rolling 60-minute cap on NEW entries, counted over the BUY timestamps recorded
    // in ActionHistory. 0 disables the throttle.
    public int MaxNewPositionsPerHour { get; set; } = 2;

    // Extra per-pair cooldown after a stop-loss fill (additive to CooldownAfterSell).
    // A pair cannot be re-bought within this many seconds of its last SELL_STOP_LOSS.
    // 0 disables it; plain (non-stop-loss) sells keep using CooldownAfterSellSeconds.
    public int CooldownAfterStopLossSeconds { get; set; } = 14400;
}

internal sealed class PositionExitOptions
{
    public const string ModeAtr = "Atr";
    public const string ModeFixedPercent = "FixedPercent";

    public string Mode { get; set; } = ModeFixedPercent;
    public int AtrPeriod { get; set; } = 14;
    public decimal StopLossAtrMultiplier { get; set; } = 2.0m;
    public decimal TakeProfitAtrMultiplier { get; set; } = 3.0m;
    public decimal FixedStopLossPercent { get; set; } = 2.5m;
    public decimal FixedTakeProfitPercent { get; set; } = 2.0m;

    // Normal signal-flip exits are only allowed when the conservative unrealized
    // PnL percent is at least this value. Does not apply to hard exits.
    public decimal MinProfitToExitOnSignalFlipPercent { get; set; } = 1.2m;

    // Legacy aliases. New configs should use FixedStopLossPercent /
    // FixedTakeProfitPercent; these stay readable for old appsettings and tests.
    public decimal? StopLossPercent { get; set; }
    public decimal? TakeProfitPercent { get; set; }

    public bool IsAtrMode => Mode.Equals(ModeAtr, StringComparison.OrdinalIgnoreCase);
    public decimal EffectiveFixedStopLossPercent => StopLossPercent ?? FixedStopLossPercent;
    public decimal EffectiveFixedTakeProfitPercent => TakeProfitPercent ?? FixedTakeProfitPercent;

    public static string NormalizeMode(string? mode) =>
        string.Equals(mode, ModeAtr, StringComparison.OrdinalIgnoreCase)
            ? ModeAtr
            : ModeFixedPercent;

    // Conditional stale-position exit: once the position age reaches this many
    // minutes, sell only if the position is losing or the entry thesis has weakened.
    // Set to 0 to disable the age guard.
    public int MaxHoldMinutes { get; set; } = 240;

    // Controlled-loss floor for CONFIRMED bearish signal-flip exits (only used when
    // exit hysteresis is enabled, Strategy.ExitEmaGapPercent > 0). For those flips the
    // signal-flip sell is allowed when conservative PnL percent >= this value,
    // replacing the MinProfitToExitOnSignalFlipPercent guard. It is a loss floor
    // (negative), e.g. -1.2: exit a confirmed bearish position while its loss is still
    // shallow, but leave deeper losses to stop-loss / max-hold. 0 disables it (legacy
    // min-profit behavior). Value is clamped to <= 0.
    public decimal MaxSignalFlipLossExitPercent { get; set; } = -1.2m;

    // Trailing stop (forced exit, TIER 2). Once the conservative peak PnL reaches
    // TrailingActivationPercent, sell if PnL falls TrailingDistancePercent below that
    // peak. Either value at 0 disables the trailing stop. TakeProfitPercent stays the
    // hard cap above it.
    public decimal TrailingActivationPercent { get; set; } = 1.5m;
    public decimal TrailingDistancePercent { get; set; } = 1.0m;

    // ---- Score-decay defensive exits (TIER 2.5) ----
    // Only positions opened at or above this entry score are decay-protected; the
    // rules exist to stop a failed HIGH-conviction entry from riding to stop-loss.
    // 0 disables all score-decay rules.
    public decimal ScoreDecayMinEntryScore { get; set; } = 0.90m;

    // Defensive rule: exit (when not profitable) after the current score has stayed
    // at or below this level for ScoreDecayDefensiveCycles consecutive cycles.
    public decimal ScoreDecayDefensiveScore { get; set; } = 0.50m;
    public int ScoreDecayDefensiveCycles { get; set; } = 2;

    // Immediate rule: exit (when not profitable) as soon as the current score falls
    // to or below this level. 0 disables the immediate rule.
    public decimal ScoreDecayImmediateScore { get; set; } = 0.40m;

    // ---- Post-entry adverse movement guard (TIER 2.5) ----
    // Within this many minutes of entry, a position that is down at least
    // PostEntryAdverseLossPercent can be cut early only when recent price action is
    // negative, the final score is below the defensive floor, and EMA or momentum
    // has structurally deteriorated. This stays separate from hard stop-loss.
    // Either value at 0 disables the guard.
    public int PostEntryAdverseWindowMinutes { get; set; } = 30;
    public decimal PostEntryAdverseLossPercent { get; set; } = 2.0m;
}
