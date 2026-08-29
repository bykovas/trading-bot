namespace EasyBot.Trading;

public sealed class BotOptions
{
    public const string SectionName = "Bot";

    public string Pair { get; set; } = "PF_XBTUSD";
    public string Timeframe { get; set; } = "4h";
    public decimal RiskPercent { get; set; } = 1m;
    public decimal LeverageMax { get; set; } = 2m;
    public int AtrPeriods { get; set; } = 14;
    public decimal AtrMultiplier { get; set; } = 2m;
    public int EmaFast { get; set; } = 20;
    public int EmaSlow { get; set; } = 50;
    public int CandleHistoryDepth { get; set; } = 100;
    public int CandleCloseSafetyBufferSeconds { get; set; } = 10;

    /// <summary>
    /// When true, connects to the Kraken Futures demo environment. Real trading requires
    /// explicitly setting this to false via configuration (never a code change).
    /// </summary>
    public bool DemoMode { get; set; } = true;
}

public sealed class KrakenOptions
{
    public const string SectionName = "Kraken";

    public string FuturesApiKey { get; set; } = "";
    public string FuturesApiSecret { get; set; } = "";
}
