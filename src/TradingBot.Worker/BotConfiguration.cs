using System.Text.Json;

namespace TradingBot.Worker;

internal sealed class BotConfiguration
{
    public WorkerOptions Worker { get; set; } = new();
    public HttpOptions Http { get; set; } = new();
    public KrakenOptions Kraken { get; set; } = new();
    public AiOptions Ai { get; set; } = new();
    public TradingOptions Trading { get; set; } = new();
    public RiskOptions Risk { get; set; } = new();
    public StrategyOptions Strategy { get; set; } = new();
    public PortfolioOptions Portfolio { get; set; } = new();
    public DryRunOptions DryRun { get; set; } = new();
    public ExecutionPolicyOptions ExecutionPolicy { get; set; } = new();
    public PositionExitOptions PositionExit { get; set; } = new();
    public List<InstrumentOptions> CandidateUniverse { get; set; } = DefaultCandidateUniverse();

    public static BotConfiguration Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            path = Path.Combine(Directory.GetCurrentDirectory(), "src", "TradingBot.Worker", "appsettings.json");
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
        SetIfPresent("TRADINGBOT_MARKET_DATA_MODE", value => config.Kraken.MarketDataMode = value);
        SetIfPresent("TRADINGBOT_KRAKEN_BASE_URL", value => config.Kraken.BaseUrl = value);
        SetIfPresent("TRADINGBOT_RUN_ONCE", value => config.Worker.RunOnce = ParseBool(value, config.Worker.RunOnce));
        SetIfPresent("TRADINGBOT_LOOP_INTERVAL_SECONDS", value => config.Worker.LoopIntervalSeconds = ParseInt(value, config.Worker.LoopIntervalSeconds));
        SetIfPresent("TRADINGBOT_TIMEFRAME_MINUTES", value => config.Trading.TimeframeMinutes = ParseInt(value, config.Trading.TimeframeMinutes));
        SetIfPresent("TRADINGBOT_MAX_ACTIVE_INSTRUMENTS", value => config.Trading.MaxActiveInstruments = ParseInt(value, config.Trading.MaxActiveInstruments));
        SetIfPresent("TRADINGBOT_LIVE_TRADING_ENABLED", value => config.Trading.LiveTradingEnabled = ParseBool(value, config.Trading.LiveTradingEnabled));
        SetIfPresent("TRADINGBOT_MAX_ORDER_EUR", value => config.Risk.MaxOrderEur = ParseDecimal(value, config.Risk.MaxOrderEur));
        SetIfPresent("TRADINGBOT_STARTING_CASH_EUR", value => config.Portfolio.StartingCashEur = ParseDecimal(value, config.Portfolio.StartingCashEur));
        SetIfPresent("TRADINGBOT_DRY_RUN_ENABLED", value => config.DryRun.Enabled = ParseBool(value, config.DryRun.Enabled));
        SetIfPresent("TRADINGBOT_DRY_RUN_APPLY_VIRTUAL_FILLS", value => config.DryRun.ApplyVirtualFills = ParseBool(value, config.DryRun.ApplyVirtualFills));
        SetIfPresent("TRADINGBOT_DRY_RUN_OUTPUT_DIRECTORY", value => config.DryRun.OutputDirectory = value);
        SetIfPresent("TRADINGBOT_DRY_RUN_TAKER_FEE_BPS", value => config.DryRun.TakerFeeBps = ParseDecimal(value, config.DryRun.TakerFeeBps));
        SetIfPresent("TRADINGBOT_DRY_RUN_SLIPPAGE_BPS", value => config.DryRun.SlippageBps = ParseDecimal(value, config.DryRun.SlippageBps));
        SetIfPresent("TRADINGBOT_AI_PROVIDER", value => config.Ai.Provider = value);
        SetIfPresent("TRADINGBOT_AI_BASE_URL", value => config.Ai.BaseUrl = value);
        SetIfPresent("TRADINGBOT_AI_API_KEY", value => config.Ai.ApiKey = value);
        SetIfPresent("TRADINGBOT_AI_MODEL", value => config.Ai.Model = value);
        SetIfPresent("TRADINGBOT_AI_MAX_RECOMMENDATIONS", value => config.Ai.MaxRecommendations = ParseInt(value, config.Ai.MaxRecommendations));
        SetIfPresent("TRADINGBOT_EXECUTION_COOLDOWN_AFTER_BUY_SECONDS", value => config.ExecutionPolicy.CooldownAfterBuySeconds = ParseInt(value, config.ExecutionPolicy.CooldownAfterBuySeconds));
        SetIfPresent("TRADINGBOT_EXECUTION_COOLDOWN_AFTER_SELL_SECONDS", value => config.ExecutionPolicy.CooldownAfterSellSeconds = ParseInt(value, config.ExecutionPolicy.CooldownAfterSellSeconds));
        SetIfPresent("TRADINGBOT_EXECUTION_MIN_HOLD_SECONDS", value => config.ExecutionPolicy.MinHoldSeconds = ParseInt(value, config.ExecutionPolicy.MinHoldSeconds));
        SetIfPresent("TRADINGBOT_EXECUTION_ALLOW_IMMEDIATE_EXIT_ON_SIGNAL_FLIP", value => config.ExecutionPolicy.AllowImmediateExitOnSignalFlip = ParseBool(value, config.ExecutionPolicy.AllowImmediateExitOnSignalFlip));
        SetIfPresent("TRADINGBOT_POSITION_EXIT_MIN_PROFIT_ON_SIGNAL_FLIP_PERCENT", value => config.PositionExit.MinProfitToExitOnSignalFlipPercent = ParseDecimal(value, config.PositionExit.MinProfitToExitOnSignalFlipPercent));
        SetIfPresent("TRADINGBOT_POSITION_EXIT_STOP_LOSS_PERCENT", value => config.PositionExit.StopLossPercent = ParseDecimal(value, config.PositionExit.StopLossPercent));
        SetIfPresent("TRADINGBOT_POSITION_EXIT_TAKE_PROFIT_PERCENT", value => config.PositionExit.TakeProfitPercent = ParseDecimal(value, config.PositionExit.TakeProfitPercent));
        SetIfPresent("TRADINGBOT_POSITION_EXIT_MAX_HOLD_MINUTES", value => config.PositionExit.MaxHoldMinutes = ParseInt(value, config.PositionExit.MaxHoldMinutes));
    }

    private void Normalize()
    {
        Worker.LoopIntervalSeconds = Math.Max(10, Worker.LoopIntervalSeconds);
        Http.TimeoutSeconds = Math.Clamp(Http.TimeoutSeconds, 5, 120);
        Trading.TimeframeMinutes = Trading.TimeframeMinutes <= 0 ? 5 : Trading.TimeframeMinutes;
        Trading.MaxActiveInstruments = Math.Max(1, Trading.MaxActiveInstruments);
        Trading.TargetOrderEur = Trading.TargetOrderEur <= 0 ? 3m : Trading.TargetOrderEur;
        Risk.MaxOrderEur = Risk.MaxOrderEur <= 0 ? 3m : Risk.MaxOrderEur;
        Risk.MaxDailyLossEur = Risk.MaxDailyLossEur <= 0 ? 10m : Risk.MaxDailyLossEur;
        Risk.MaxOpenPositions = Math.Max(1, Risk.MaxOpenPositions);
        Strategy.MinimumLongScore = Math.Clamp(Strategy.MinimumLongScore, 0m, 1m);
        Portfolio.StartingCashEur = Portfolio.StartingCashEur < 0 ? 0m : Portfolio.StartingCashEur;
        DryRun.OutputDirectory = string.IsNullOrWhiteSpace(DryRun.OutputDirectory) ? "data/dry-run" : DryRun.OutputDirectory.Trim();
        DryRun.StateFile = string.IsNullOrWhiteSpace(DryRun.StateFile) ? "portfolio-state.json" : DryRun.StateFile.Trim();
        DryRun.EventsFile = string.IsNullOrWhiteSpace(DryRun.EventsFile) ? "events.jsonl" : DryRun.EventsFile.Trim();
        DryRun.TakerFeeBps = Math.Max(0m, DryRun.TakerFeeBps);
        DryRun.SlippageBps = Math.Max(0m, DryRun.SlippageBps);
        ExecutionPolicy.CooldownAfterBuySeconds = Math.Max(0, ExecutionPolicy.CooldownAfterBuySeconds);
        ExecutionPolicy.CooldownAfterSellSeconds = Math.Max(0, ExecutionPolicy.CooldownAfterSellSeconds);
        ExecutionPolicy.MinHoldSeconds = Math.Max(0, ExecutionPolicy.MinHoldSeconds);
        PositionExit.MinProfitToExitOnSignalFlipPercent = Math.Max(0m, PositionExit.MinProfitToExitOnSignalFlipPercent);
        PositionExit.StopLossPercent = Math.Max(0m, PositionExit.StopLossPercent);
        PositionExit.TakeProfitPercent = Math.Max(0m, PositionExit.TakeProfitPercent);
        PositionExit.MaxHoldMinutes = Math.Max(0, PositionExit.MaxHoldMinutes);
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

internal sealed class WorkerOptions
{
    public bool RunOnce { get; set; } = true;
    public int LoopIntervalSeconds { get; set; } = 300;
}

internal sealed class HttpOptions
{
    public int TimeoutSeconds { get; set; } = 20;
}

internal sealed class KrakenOptions
{
    public string MarketDataMode { get; set; } = "sample";
    public string BaseUrl { get; set; } = "https://api.kraken.com";
}

internal sealed class AiOptions
{
    public string Provider { get; set; } = "none";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int MaxRecommendations { get; set; } = 2;
    public bool UseJsonResponseFormat { get; set; } = true;
}

internal sealed class TradingOptions
{
    public bool LiveTradingEnabled { get; set; } = false;
    public int TimeframeMinutes { get; set; } = 5;
    public int MaxActiveInstruments { get; set; } = 2;
    public decimal TargetOrderEur { get; set; } = 3m;
}

internal sealed class RiskOptions
{
    public decimal MaxOrderEur { get; set; } = 3m;
    public decimal MaxDailyLossEur { get; set; } = 10m;
    public int MaxOpenPositions { get; set; } = 1;
    public bool KillSwitch { get; set; } = false;
}

internal sealed class StrategyOptions
{
    public int FastEmaPeriod { get; set; } = 9;
    public int SlowEmaPeriod { get; set; } = 21;
    public int RsiPeriod { get; set; } = 14;
    public decimal MinimumLongScore { get; set; } = 0.55m;
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

internal sealed class DryRunOptions
{
    public bool Enabled { get; set; } = true;
    public bool ApplyVirtualFills { get; set; } = true;
    public string OutputDirectory { get; set; } = "data/dry-run";
    public string StateFile { get; set; } = "portfolio-state.json";
    public string EventsFile { get; set; } = "events.jsonl";
    public decimal TakerFeeBps { get; set; } = 26m;
    public decimal SlippageBps { get; set; } = 5m;
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

    // When true, disables the minimum-hold guard so a signal flip can close a
    // position immediately. Default false to avoid buy/sell churn on noisy flips.
    public bool AllowImmediateExitOnSignalFlip { get; set; } = false;
}

internal sealed class PositionExitOptions
{
    // Normal signal-flip exits are only allowed when the conservative unrealized
    // PnL percent is at least this value. Does not apply to hard exits.
    public decimal MinProfitToExitOnSignalFlipPercent { get; set; } = 1.2m;

    // Hard exit: sell when conservative unrealized PnL percent <= -StopLossPercent.
    public decimal StopLossPercent { get; set; } = 1.5m;

    // Hard exit: sell when conservative unrealized PnL percent >= TakeProfitPercent,
    // even if the strategy still wants LONG_MICRO.
    public decimal TakeProfitPercent { get; set; } = 2.0m;

    // Hard exit: sell once the position age reaches this many minutes. Set to 0 to
    // disable the max-hold guard.
    public int MaxHoldMinutes { get; set; } = 240;
}

internal sealed class InstrumentOptions
{
    public string Pair { get; set; } = string.Empty;
    public string KrakenPair { get; set; } = string.Empty;
    public string Venue { get; set; } = "Kraken";
    public bool Enabled { get; set; } = true;
}
