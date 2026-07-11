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
        SetIfPresent("TRADINGBOT_STARTING_CASH_EUR", value => config.Portfolio.StartingCashEur = ParseDecimal(value, config.Portfolio.StartingCashEur));
        SetIfPresent("TRADINGBOT_LOG_DIRECTORY", value => config.Logging.Directory = value);
        SetIfPresent("TRADINGBOT_DATABASE_ENABLED", value => config.Database.Enabled = ParseBool(value, config.Database.Enabled));
        SetIfPresent("TRADINGBOT_DATABASE_CONNECTION_STRING", value => config.Database.ConnectionString = value);
        SetIfPresent("TRADINGBOT_FUTURES_MAX_LEVERAGE", value => config.Futures.MaxLeverage = ParseDecimal(value, config.Futures.MaxLeverage));
        SetIfPresent("TRADINGBOT_FUTURES_DEFAULT_LEVERAGE", value => config.Futures.DefaultLeverage = ParseDecimal(value, config.Futures.DefaultLeverage));
        SetIfPresent("TRADINGBOT_FUTURES_MAX_POSITIONS", value => config.Futures.MaxPositions = ParseInt(value, config.Futures.MaxPositions));
        SetIfPresent("TRADINGBOT_FUTURES_ALLOW_SHORTS", value => config.Futures.AllowShorts = ParseBool(value, config.Futures.AllowShorts));
        SetIfPresent("TRADINGBOT_FUTURES_TARGET_NOTIONAL_EUR", value => config.Futures.TargetNotionalEur = ParseDecimal(value, config.Futures.TargetNotionalEur));
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
        Portfolio.StartingCashEur = Portfolio.StartingCashEur <= 0m ? 100m : Portfolio.StartingCashEur;

        // Blueprint safety defaults: dry-run only, small leverage, no flip, and a
        // small portfolio cap. Normalize clamps rather than trusts config so a typo
        // cannot widen the risk envelope.
        Futures.MaxLeverage = Math.Clamp(Futures.MaxLeverage <= 0m ? 2m : Futures.MaxLeverage, 1m, 2m);
        Futures.DefaultLeverage = Math.Clamp(Futures.DefaultLeverage <= 0m ? 1m : Futures.DefaultLeverage, 1m, Futures.MaxLeverage);
        Futures.MaxPositions = Math.Clamp(Futures.MaxPositions <= 0 ? 3 : Futures.MaxPositions, 1, 3);
        Futures.AllowFlip = false;
        Futures.TargetNotionalEur = Futures.TargetNotionalEur <= 0m ? 10m : Futures.TargetNotionalEur;
        Futures.DeadManSwitchSeconds = Math.Max(10, Futures.DeadManSwitchSeconds);

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
        Filters.MinQuoteVolume24h = Filters.MinQuoteVolume24h <= 0m ? 50_000m : Filters.MinQuoteVolume24h;
        Filters.MinExitDepthMultiple = Filters.MinExitDepthMultiple <= 0m ? 5m : Filters.MinExitDepthMultiple;
        Filters.MaxExitImpactPct = Filters.MaxExitImpactPct <= 0m ? 0.5m : Filters.MaxExitImpactPct;
        Exits.StopAtrMult = Exits.StopAtrMult <= 0m ? 2m : Exits.StopAtrMult;
        Exits.MinStopAtrFloor = Exits.MinStopAtrFloor <= 0m ? 1.5m : Exits.MinStopAtrFloor;
        Exits.TakeProfitAtrMult = Exits.TakeProfitAtrMult <= 0m ? 3m : Exits.TakeProfitAtrMult;
        Exits.MinTpVsCostMult = Exits.MinTpVsCostMult <= 0m ? 3m : Exits.MinTpVsCostMult;
        Exits.SlippageBufferPct = Exits.SlippageBufferPct < 0m ? 0.10m : Exits.SlippageBufferPct;
        Exits.MaxHoldMinutes = Exits.MaxHoldMinutes <= 0 ? 360 : Exits.MaxHoldMinutes;
        Exits.TrailingActivationBufferPct = Math.Max(0m, Exits.TrailingActivationBufferPct);
        Regime.BtcTrendMa = Regime.BtcTrendMa <= 0 ? 50 : Regime.BtcTrendMa;
        Regime.BtcSlopeLookback = Regime.BtcSlopeLookback <= 0 ? 3 : Regime.BtcSlopeLookback;
        Regime.BtcCrashLookback = Regime.BtcCrashLookback <= 0 ? 4 : Regime.BtcCrashLookback;
        Regime.BtcCrashPct = Regime.BtcCrashPct <= 0m ? 2m : Regime.BtcCrashPct;
        Shorts.MaxChaseDrawdownPct = Shorts.MaxChaseDrawdownPct <= 0m ? 3m : Shorts.MaxChaseDrawdownPct;
        Shorts.MinShortScore = Shorts.MinShortScore <= 0m ? 0.90m : Shorts.MinShortScore;
        Risk.MaxConcurrentOpenRisk = Risk.MaxConcurrentOpenRisk <= 0m ? 1.5m : Risk.MaxConcurrentOpenRisk;
        Risk.EstimatedEmergencyExitCostPct = Math.Max(0m, Risk.EstimatedEmergencyExitCostPct);
        ExecutionPolicy.CooldownAfterCloseSeconds = Math.Max(0, ExecutionPolicy.CooldownAfterCloseSeconds);
        ExecutionPolicy.CooldownAfterStopLossSeconds = Math.Max(0, ExecutionPolicy.CooldownAfterStopLossSeconds);
        ExecutionPolicy.MinHoldSeconds = Math.Max(0, ExecutionPolicy.MinHoldSeconds);
        ExecutionPolicy.EntryBlackoutUtcFromHour = Math.Clamp(ExecutionPolicy.EntryBlackoutUtcFromHour, 0, 23);
        ExecutionPolicy.EntryBlackoutMinutes = Math.Max(0, ExecutionPolicy.EntryBlackoutMinutes);
        CorrelationRisk.MaxOpenPositionsPerGroup = CorrelationRisk.MaxOpenPositionsPerGroup <= 0 ? 1 : CorrelationRisk.MaxOpenPositionsPerGroup;
        CorrelationRisk.MaxExposureEurPerGroup = CorrelationRisk.MaxExposureEurPerGroup <= 0m ? Futures.TargetNotionalEur : CorrelationRisk.MaxExposureEurPerGroup;

        TpSl.Enabled = true;
        TpSl.TakeProfitPercent = Exits.TakeProfitAtrMult;
        TpSl.StopLossPercent = Exits.StopAtrMult;
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
}

internal sealed class FuturesPortfolioOptions
{
    // Starting margin collateral for the virtual ledger, not spot cash.
    public decimal StartingCashEur { get; set; } = 100m;
}

internal sealed class FuturesOptions
{
    public decimal MaxLeverage { get; set; } = 2m;
    public decimal DefaultLeverage { get; set; } = 2m;
    public int MaxPositions { get; set; } = 3;
    public bool AllowShorts { get; set; } = true;

    // Flips (long -> short in one step) are forbidden by the blueprint; Normalize
    // forces this to false regardless of config.
    public bool AllowFlip { get; set; }
    public decimal TargetNotionalEur { get; set; } = 10m;
    public bool LiveTradingEnabled { get; set; }
    public int DeadManSwitchSeconds { get; set; } = 90;
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
    public decimal TrailingActivationBufferPct { get; set; } = 0m;
}

internal sealed class FuturesRegimeOptions
{
    public string BtcPair { get; set; } = "XBT/USD";
    public int BtcTrendMa { get; set; } = 50;
    public int BtcSlopeLookback { get; set; } = 3;
    public int BtcCrashLookback { get; set; } = 4;
    public decimal BtcCrashPct { get; set; } = 2.0m;
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
