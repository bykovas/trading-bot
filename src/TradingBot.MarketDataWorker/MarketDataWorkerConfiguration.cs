using System.Text.Json;

namespace TradingBot.MarketDataWorker;

internal sealed class MarketDataWorkerConfiguration
{
    public WorkerOptions Worker { get; set; } = new();
    public HttpOptions Http { get; set; } = new();
    public DatabaseOptions Database { get; set; } = new();
    public KrakenOptions SpotKraken { get; set; } = new();
    public KrakenOptions FuturesKraken { get; set; } = new() { BaseUrl = "https://futures.kraken.com" };
    public UniverseDiscoveryOptions UniverseDiscovery { get; set; } = new();
    public MarketDataIngestionOptions Ingestion { get; set; } = new();
    public List<InstrumentOptions> CandidateUniverse { get; set; } = new();

    public static MarketDataWorkerConfiguration Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            path = Path.Combine("src", "TradingBot.MarketDataWorker", "appsettings.json");
        }

        var config = File.Exists(path)
            ? JsonSerializer.Deserialize<MarketDataWorkerConfiguration>(
                  File.ReadAllText(path),
                  new JsonSerializerOptions
                  {
                      PropertyNameCaseInsensitive = true,
                      ReadCommentHandling = JsonCommentHandling.Skip,
                      AllowTrailingCommas = true
                  }) ?? new MarketDataWorkerConfiguration()
            : new MarketDataWorkerConfiguration();

        ApplyEnvironment(config);
        config.Normalize();
        return config;
    }

    private static void ApplyEnvironment(MarketDataWorkerConfiguration config)
    {
        SetIfPresent("TRADINGBOT_RUN_ONCE", value => config.Worker.RunOnce = ParseBool(value, config.Worker.RunOnce));
        SetIfPresent("TRADINGBOT_DATABASE_ENABLED", value => config.Database.Enabled = ParseBool(value, config.Database.Enabled));
        SetIfPresent("TRADINGBOT_DATABASE_CONNECTION_STRING", value => config.Database.ConnectionString = value);
        SetIfPresent("TRADINGBOT_KRAKEN_BASE_URL", value => config.SpotKraken.BaseUrl = value);
        SetIfPresent("TRADINGBOT_KRAKEN_FUTURES_BASE_URL", value => config.FuturesKraken.BaseUrl = value);
        SetIfPresent("TRADINGBOT_MARKET_DATA_SPOT_ENABLED", value => config.Ingestion.SpotEnabled = ParseBool(value, config.Ingestion.SpotEnabled));
        SetIfPresent("TRADINGBOT_MARKET_DATA_FUTURES_ENABLED", value => config.Ingestion.FuturesEnabled = ParseBool(value, config.Ingestion.FuturesEnabled));
        SetIfPresent("TRADINGBOT_MARKET_DATA_LIGHT_INTERVAL_SECONDS", value => config.Ingestion.LightIntervalSeconds = ParseInt(value, config.Ingestion.LightIntervalSeconds));
        SetIfPresent("TRADINGBOT_MARKET_DATA_CANDLE_INTERVAL_SECONDS", value => config.Ingestion.CandleIntervalSeconds = ParseInt(value, config.Ingestion.CandleIntervalSeconds));
        SetIfPresent("TRADINGBOT_TIMEFRAME_MINUTES", value => config.Ingestion.TimeframeMinutes = ParseInt(value, config.Ingestion.TimeframeMinutes));
        SetIfPresent("TRADINGBOT_MARKET_DATA_MAX_CANDLE_PAIRS", value => config.Ingestion.MaxCandlePairs = ParseInt(value, config.Ingestion.MaxCandlePairs));
        SetIfPresent("TRADINGBOT_MARKET_DATA_TOP_VOLUME_PAIRS", value => config.Ingestion.TopVolumePairs = ParseInt(value, config.Ingestion.TopVolumePairs));
        SetIfPresent("TRADINGBOT_MARKET_DATA_TOP_MOVER_PAIRS", value => config.Ingestion.TopMoverPairs = ParseInt(value, config.Ingestion.TopMoverPairs));
        SetIfPresent("TRADINGBOT_MARKET_DATA_STRONG_MOVER_PERCENT", value => config.Ingestion.StrongMoverPercent = ParseDecimal(value, config.Ingestion.StrongMoverPercent));
        SetIfPresent("TRADINGBOT_MARKET_DATA_FORCE_INCLUDE", value => config.Ingestion.ForceInclude = ParseCsv(value));
        SetIfPresent("TRADINGBOT_MARKET_DATA_BLACKLIST", value => config.Ingestion.Blacklist = ParseCsv(value));
        SetIfPresent("TRADINGBOT_MARKET_DATA_STRATEGY_CANDLE_INTERVAL_SECONDS", value => config.Ingestion.StrategyCandleIntervalSeconds = ParseInt(value, config.Ingestion.StrategyCandleIntervalSeconds));
        SetIfPresent("TRADINGBOT_MARKET_DATA_STRATEGY_CANDLE_BARS", value => config.Ingestion.StrategyCandleBars = ParseInt(value, config.Ingestion.StrategyCandleBars));
        SetIfPresent("TRADINGBOT_MARKET_DATA_STRATEGY_TIMEFRAMES_MINUTES", value => config.Ingestion.StrategyTimeframesMinutes = ParseCsv(value).Select(item => ParseInt(item, 0)).Where(item => item > 0).ToList());
        SetIfPresent("TRADINGBOT_MARKET_DATA_STRATEGY_PAIRS", value => config.Ingestion.StrategyPairs = ParseCsv(value));
        SetIfPresent("TRADINGBOT_MARKET_DATA_FUTURES_LIQUIDATIONS_ENABLED", value => config.Ingestion.FuturesLiquidationsEnabled = ParseBool(value, config.Ingestion.FuturesLiquidationsEnabled));
        SetIfPresent("TRADINGBOT_KRAKEN_FUTURES_WEBSOCKET_URL", value => config.Ingestion.FuturesWebSocketUrl = value);
    }

    private void Normalize()
    {
        Worker.RunOnce = Worker.RunOnce;
        Http.TimeoutSeconds = Math.Clamp(Http.TimeoutSeconds, 5, 120);
        Ingestion.LightIntervalSeconds = Math.Max(10, Ingestion.LightIntervalSeconds);
        Ingestion.CandleIntervalSeconds = Math.Max(Ingestion.LightIntervalSeconds, Ingestion.CandleIntervalSeconds);
        Ingestion.StrategyCandleIntervalSeconds = Math.Max(30, Ingestion.StrategyCandleIntervalSeconds);
        Ingestion.StrategyCandleBars = Math.Clamp(Ingestion.StrategyCandleBars, 203, 500);
        Ingestion.StrategyTimeframesMinutes = Ingestion.StrategyTimeframesMinutes
            .Where(value => value is 1 or 5 or 15 or 60)
            .Distinct()
            .OrderBy(value => value)
            .ToList();
        Ingestion.StrategyPairs = Ingestion.StrategyPairs
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Ingestion.TimeframeMinutes = Ingestion.TimeframeMinutes <= 0 ? 15 : Ingestion.TimeframeMinutes;
        Ingestion.MaxCandlePairs = Math.Clamp(Ingestion.MaxCandlePairs <= 0 ? 100 : Ingestion.MaxCandlePairs, 1, 120);
        Ingestion.TopVolumePairs = Math.Clamp(Ingestion.TopVolumePairs <= 0 ? 30 : Ingestion.TopVolumePairs, 1, Ingestion.MaxCandlePairs);
        Ingestion.TopMoverPairs = Math.Clamp(Ingestion.TopMoverPairs <= 0 ? 40 : Ingestion.TopMoverPairs, 0, Ingestion.MaxCandlePairs);
        SpotKraken.MarketDataMode = "kraken";
        FuturesKraken.MarketDataMode = "kraken-futures";
    }

    private static void SetIfPresent(string name, Action<string> apply)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            apply(value);
        }
    }

    private static bool ParseBool(string value, bool fallback) =>
        bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static int ParseInt(string value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;

    private static decimal ParseDecimal(string value, decimal fallback) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static List<string> ParseCsv(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
