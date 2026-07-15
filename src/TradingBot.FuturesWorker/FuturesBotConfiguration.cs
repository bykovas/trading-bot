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
    public FuturesFilterOptions Filters { get; set; } = new();
    public FuturesExitOptions Exits { get; set; } = new();
    public FuturesRegimeOptions Regime { get; set; } = new();
    public FuturesShortOptions Shorts { get; set; } = new();
    public FuturesRiskOptions Risk { get; set; } = new();
    public FuturesExecutionPolicyOptions ExecutionPolicy { get; set; } = new();
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
        SetIfPresent("TRADINGBOT_STARTING_CASH_EUR", value => config.Portfolio.StartingCashEur = ParseDecimal(value, config.Portfolio.StartingCashEur));
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
        SetIfPresent("TRADINGBOT_FUTURES_MAX_POSITIONS", value => config.Futures.MaxPositions = ParseInt(value, config.Futures.MaxPositions));
        SetIfPresent("TRADINGBOT_FUTURES_ALLOW_SHORTS", value => config.Futures.AllowShorts = ParseBool(value, config.Futures.AllowShorts));
        SetIfPresent("TRADINGBOT_FUTURES_TARGET_MARGIN_EUR", value => config.Futures.TargetMarginEur = ParseDecimal(value, config.Futures.TargetMarginEur));
        // Deprecated alias: sets the legacy notional field, migrated in Normalize.
        SetIfPresent("TRADINGBOT_FUTURES_TARGET_NOTIONAL_EUR", value => config.Futures.TargetNotionalEur = ParseDecimal(value, config.Futures.TargetNotionalEur ?? 0m));
        SetIfPresent("TRADINGBOT_FUTURES_USD_PER_EUR", value => config.Futures.UsdPerEur = ParseDecimal(value, config.Futures.UsdPerEur));
        SetIfPresent("TRADINGBOT_FUTURES_FAST_EXIT_CHECK_SECONDS", value => config.Futures.FastExitCheckSeconds = ParseInt(value, config.Futures.FastExitCheckSeconds));
        SetIfPresent("TRADINGBOT_FUTURES_ENTRY_MAX_PRICE_DEVIATION_PERCENT", value => config.Entry.MaxEntryPriceDeviationPct = ParseDecimal(value, config.Entry.MaxEntryPriceDeviationPct));
        SetIfPresent("TRADINGBOT_FUTURES_FRESHNESS_FRESH_CONTINUATION_MIN_24H_RANGE_POSITION_PERCENT", value => config.Freshness.FreshContinuationMin24hRangePositionPct = ParseDecimal(value, config.Freshness.FreshContinuationMin24hRangePositionPct));
        SetIfPresent("TRADINGBOT_FUTURES_FRESHNESS_NEAR_HIGH_MIN_24H_RANGE_POSITION_PERCENT", value => config.Freshness.NearHighMin24hRangePositionPct = ParseDecimal(value, config.Freshness.NearHighMin24hRangePositionPct));
        SetIfPresent("TRADINGBOT_FUTURES_FRESHNESS_NEAR_HIGH_MAX_DISTANCE_FROM_RECENT_HIGH_PERCENT", value => config.Freshness.NearHighMaxDistanceFromRecentHighPct = ParseDecimal(value, config.Freshness.NearHighMaxDistanceFromRecentHighPct));
        SetIfPresent("TRADINGBOT_FUTURES_FRESHNESS_RECENT_HIGH_LOOKBACK_CANDLES", value => config.Freshness.RecentHighLookbackCandles = ParseInt(value, config.Freshness.RecentHighLookbackCandles));
        SetIfPresent("TRADINGBOT_FUTURES_FRESHNESS_FRESH_TAPE_SNAPSHOT_COUNT", value => config.Freshness.FreshTapeSnapshotCount = ParseInt(value, config.Freshness.FreshTapeSnapshotCount));
        SetIfPresent("TRADINGBOT_FUTURES_FRESHNESS_FRESH_TAPE_MIN_SLOPE_PERCENT", value => config.Freshness.FreshTapeMinSlopePct = ParseDecimal(value, config.Freshness.FreshTapeMinSlopePct));
        SetIfPresent("TRADINGBOT_FUTURES_FRESHNESS_FRESH_TAPE_MIN_POSITIVE_STEPS", value => config.Freshness.FreshTapeMinPositiveSteps = ParseInt(value, config.Freshness.FreshTapeMinPositiveSteps));
        SetIfPresent("TRADINGBOT_FUTURES_FRESHNESS_BREAKOUT_MIN_ABOVE_RECENT_HIGH_PERCENT", value => config.Freshness.BreakoutMinAboveRecentHighPct = ParseDecimal(value, config.Freshness.BreakoutMinAboveRecentHighPct));
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
        SetIfPresent("TRADINGBOT_FUTURES_LIVE_TRADING_ENABLED", value => config.Futures.LiveTradingEnabled = ParseBool(value, config.Futures.LiveTradingEnabled));
        SetIfPresent("TRADINGBOT_KRAKEN_FUTURES_API_KEY", value => config.Kraken.ApiKey = value);
        SetIfPresent("TRADINGBOT_KRAKEN_FUTURES_API_SECRET", value => config.Kraken.ApiSecret = value);
        SetIfPresent("TRADINGBOT_FUTURES_MIN_LIQUIDATION_DISTANCE_PERCENT", value => config.Margin.MinLiquidationDistancePercent = ParseDecimal(value, config.Margin.MinLiquidationDistancePercent));
        SetIfPresent("TRADINGBOT_FUTURES_MAX_MARGIN_UTILIZATION_PERCENT", value => config.Margin.MaxAccountMarginUtilizationPercent = ParseDecimal(value, config.Margin.MaxAccountMarginUtilizationPercent));
        SetIfPresent("TRADINGBOT_FUTURES_DEAD_MAN_SWITCH_SECONDS", value => config.Futures.DeadManSwitchSeconds = ParseInt(value, config.Futures.DeadManSwitchSeconds));
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
        Portfolio.StartingCashEur = Portfolio.StartingCashEur <= 0m ? 100m : Portfolio.StartingCashEur;

        // Blueprint safety defaults: dry-run only, no flip, and a small portfolio
        // cap. Normalize clamps rather than trusts config so a typo cannot widen the
        // risk envelope. The hard leverage ceiling is 10x — a value above that is a
        // typo, not an intent; the per-symbol margin preference set on Kraken and the
        // liquidation-distance gate still apply on top of this cap.
        Futures.MaxLeverage = Math.Clamp(Futures.MaxLeverage <= 0m ? 10m : Futures.MaxLeverage, 1m, 10m);
        Futures.DefaultLeverage = Math.Clamp(Futures.DefaultLeverage <= 0m ? 1m : Futures.DefaultLeverage, 1m, Futures.MaxLeverage);
        Futures.MaxPositions = Math.Clamp(Futures.MaxPositions <= 0 ? 3 : Futures.MaxPositions, 1, 3);
        Futures.AllowFlip = false;

        // Sizing migration: the old TargetNotionalEur meant NOTIONAL; the new
        // TargetMarginEur means MARGIN. If only the legacy value is set, derive the
        // margin that PRESERVES the old notional exposure (notional / leverage) and
        // warn — never silently 10x the position by reinterpreting the number.
        if (Futures.TargetMarginEur <= 0m && Futures.TargetNotionalEur is { } legacyNotional && legacyNotional > 0m)
        {
            Futures.TargetMarginEur = legacyNotional / Math.Max(1m, Futures.DefaultLeverage);
            Console.WriteLine(
                $"config-migration: Futures.TargetNotionalEur={legacyNotional:0.####} is deprecated; interpreted as legacy NOTIONAL and migrated to TargetMarginEur={Futures.TargetMarginEur:0.####} (notional/leverage) to preserve exposure. Set TargetMarginEur explicitly.");
        }
        Futures.TargetMarginEur = Futures.TargetMarginEur <= 0m ? 10m : Futures.TargetMarginEur;
        Futures.TargetNotionalEur = null;
        Futures.UsdPerEur = Futures.UsdPerEur <= 0m ? 1m : Futures.UsdPerEur;
        Futures.FastExitCheckSeconds = Math.Clamp(Futures.FastExitCheckSeconds <= 0 ? 10 : Futures.FastExitCheckSeconds, 5, Worker.LoopIntervalSeconds);
        Futures.DeadManSwitchSeconds = Math.Max(10, Futures.DeadManSwitchSeconds);
        // DMS must outlive the gap between live-cycle refreshes (loop sleep + cycle work).
        var minDeadManSwitchSeconds = Worker.LoopIntervalSeconds * 2;
        if (Futures.DeadManSwitchSeconds < minDeadManSwitchSeconds)
        {
            Futures.DeadManSwitchSeconds = minDeadManSwitchSeconds;
        }

        Margin.MaintenanceMarginRatePercent = Math.Clamp(Margin.MaintenanceMarginRatePercent, 0m, 50m);
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
        Freshness.NearHighMin24hRangePositionPct = Math.Clamp(Freshness.NearHighMin24hRangePositionPct <= 0m ? 88m : Freshness.NearHighMin24hRangePositionPct, 50m, 100m);
        Freshness.NearHighMaxDistanceFromRecentHighPct = Math.Clamp(Freshness.NearHighMaxDistanceFromRecentHighPct <= 0m ? 0.5m : Freshness.NearHighMaxDistanceFromRecentHighPct, 0m, 10m);
        Freshness.RecentHighLookbackCandles = Math.Clamp(Freshness.RecentHighLookbackCandles <= 0 ? 12 : Freshness.RecentHighLookbackCandles, 2, 96);
        Freshness.FreshTapeSnapshotCount = Math.Clamp(Freshness.FreshTapeSnapshotCount <= 0 ? 3 : Freshness.FreshTapeSnapshotCount, 2, 10);
        Freshness.FreshTapeMinSlopePct = Math.Clamp(Freshness.FreshTapeMinSlopePct <= 0m ? 0.05m : Freshness.FreshTapeMinSlopePct, 0m, 5m);
        Freshness.FreshTapeMinPositiveSteps = Math.Clamp(Freshness.FreshTapeMinPositiveSteps <= 0 ? 2 : Freshness.FreshTapeMinPositiveSteps, 1, Freshness.FreshTapeSnapshotCount - 1);
        Freshness.BreakoutMinAboveRecentHighPct = Math.Clamp(Freshness.BreakoutMinAboveRecentHighPct <= 0m ? 0.05m : Freshness.BreakoutMinAboveRecentHighPct, 0m, 5m);
        Filters.MinQuoteVolume24h = Filters.MinQuoteVolume24h <= 0m ? 50_000m : Filters.MinQuoteVolume24h;
        Filters.MinExitDepthMultiple = Filters.MinExitDepthMultiple <= 0m ? 5m : Filters.MinExitDepthMultiple;
        Filters.MaxExitImpactPct = Filters.MaxExitImpactPct <= 0m ? 0.5m : Filters.MaxExitImpactPct;
        Exits.StopAtrMult = Exits.StopAtrMult <= 0m ? 2m : Exits.StopAtrMult;
        Exits.MinStopAtrFloor = Exits.MinStopAtrFloor <= 0m ? 1.5m : Exits.MinStopAtrFloor;
        Exits.TakeProfitAtrMult = Exits.TakeProfitAtrMult <= 0m ? 3m : Exits.TakeProfitAtrMult;
        Exits.MinTpVsCostMult = Exits.MinTpVsCostMult <= 0m ? 3m : Exits.MinTpVsCostMult;
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
        // Open-risk cap must follow the NOTIONAL semantics, not the old margin-sized
        // number. A stop-out loses ~notional * stopPct; the default lets all
        // MaxPositions sit open at once. A too-small legacy value (sized for the old
        // ~10 EUR notional) would otherwise block every 100 EUR entry, so it is
        // recomputed when below one position's stop-out loss.
        var stopLossPercentForRisk = TpSl.StopLossPercent <= 0m ? 2m : TpSl.StopLossPercent;
        var perPositionOpenRisk = Futures.DerivedNotionalEur(Futures.DefaultLeverage) * stopLossPercentForRisk / 100m;
        if (Risk.MaxConcurrentOpenRisk < perPositionOpenRisk)
        {
            Risk.MaxConcurrentOpenRisk = decimal.Round(perPositionOpenRisk * Futures.MaxPositions, 4);
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
        CorrelationRisk.MaxExposureEurPerGroup = CorrelationRisk.MaxExposureEurPerGroup <= 0m ? Futures.DerivedNotionalEur(Futures.DefaultLeverage) : CorrelationRisk.MaxExposureEurPerGroup;
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
        TpSl.StopLossPercent = TpSl.StopLossPercent <= 0m ? 2m : TpSl.StopLossPercent;
        TpSl.TriggerSource = string.IsNullOrWhiteSpace(TpSl.TriggerSource) ? "mark" : TpSl.TriggerSource;
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
}

internal sealed class FuturesPortfolioOptions
{
    // Starting margin collateral for the virtual ledger, not spot cash.
    public decimal StartingCashEur { get; set; } = 100m;
}

internal sealed class FuturesOptions
{
    public decimal MaxLeverage { get; set; } = 10m;
    public decimal DefaultLeverage { get; set; } = 10m;
    public int MaxPositions { get; set; } = 3;
    public bool AllowShorts { get; set; } = true;

    // Flips (long -> short in one step) are forbidden by the blueprint; Normalize
    // forces this to false regardless of config.
    public bool AllowFlip { get; set; }

    // Business sizing parameter: the initial margin (collateral) committed per
    // position, in EUR. The position NOTIONAL is derived once as
    // TargetMarginEur * leverage, so TargetMarginEur=10 at 10x opens ~100 EUR
    // notional and posts ~10 EUR margin. This is the ONLY sizing input; nothing
    // else multiplies by leverage a second time.
    public decimal TargetMarginEur { get; set; } = 10m;

    // Legacy alias (deprecated): previously this value was the position NOTIONAL.
    // Kept only so old appsettings/env do not silently change the risk envelope on
    // upgrade — Normalize migrates it to TargetMarginEur = legacyNotional / leverage
    // (preserving the old exposure) and logs a one-time warning. Null when unset.
    public decimal? TargetNotionalEur { get; set; }

    // FX factor to convert an EUR notional into the instrument's USD quote currency
    // when computing contract quantity for USD-quoted perps. Static and configurable
    // (appsettings ships 1.08); wire to a live EUR/USD feed later. Code default is 1.0
    // (EUR treated as quote currency) so it never silently changes sizing. Only
    // affects quantity, never the EUR margin/notional books.
    public decimal UsdPerEur { get; set; } = 1m;

    public int FastExitCheckSeconds { get; set; } = 10;
    public bool LiveTradingEnabled { get; set; }
    public int DeadManSwitchSeconds { get; set; } = 90;

    // Derived position notional in EUR for a new entry at the given leverage.
    public decimal DerivedNotionalEur(decimal leverage) => TargetMarginEur * Math.Max(1m, leverage);
}

internal sealed class MarginOptions
{
    public decimal MaintenanceMarginRatePercent { get; set; } = 0.5m;
    public decimal MinLiquidationDistancePercent { get; set; } = 15m;
    public decimal MaxAccountMarginUtilizationPercent { get; set; } = 50m;
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
    public decimal NearHighMin24hRangePositionPct { get; set; } = 88m;
    public decimal NearHighMaxDistanceFromRecentHighPct { get; set; } = 0.5m;
    public int RecentHighLookbackCandles { get; set; } = 12;
    public int FreshTapeSnapshotCount { get; set; } = 3;
    public decimal FreshTapeMinSlopePct { get; set; } = 0.05m;
    public int FreshTapeMinPositiveSteps { get; set; } = 2;
    public decimal BreakoutMinAboveRecentHighPct { get; set; } = 0.05m;
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
    public decimal StopAtrMult { get; set; } = 2m;
    public decimal MinStopAtrFloor { get; set; } = 1.5m;
    public decimal TakeProfitAtrMult { get; set; } = 3m;
    public decimal MinTpVsCostMult { get; set; } = 3m;
    public decimal SlippageBufferPct { get; set; } = 0.10m;
    public int MaxHoldMinutes { get; set; } = 360;
    public decimal MaxHoldMinStopProgressPct { get; set; } = 60m;
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
}

internal sealed class FuturesShortOptions
{
    public decimal MaxChaseDrawdownPct { get; set; } = 3.0m;
    public decimal MinShortScore { get; set; } = 0.90m;
}

internal sealed class FuturesRiskOptions
{
    public decimal MaxConcurrentOpenRisk { get; set; } = 1.5m;
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
    public decimal TakeProfitPercent { get; set; } = 3m;
    public decimal StopLossPercent { get; set; } = 2m;

    // Which price stream triggers simulated TP/SL: "mark" | "index" | "last".
    // Only "mark"/"last" are meaningful for the virtual portfolio today.
    public string TriggerSource { get; set; } = "mark";

    // All simulated TP/SL exits are reduce-only by design; there is intentionally
    // no configuration switch for this (blueprint hard rule).
}
