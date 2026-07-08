namespace TradingBot.Core.Risk;

public sealed class PositionExitOptions
{
    public const string ModeAtr = "Atr";
    public const string ModeFixedPercent = "FixedPercent";

    public string Mode { get; set; } = ModeFixedPercent;
    public int AtrPeriod { get; set; } = 14;
    public decimal StopLossAtrMultiplier { get; set; } = 2.0m;
    public decimal TakeProfitAtrMultiplier { get; set; } = 3.0m;
    public decimal MinStopAtrFloor { get; set; } = 1.5m;
    public decimal MinTpVsCostMult { get; set; } = 3.0m;
    public decimal FixedStopLossPercent { get; set; } = 2.5m;
    public decimal FixedTakeProfitPercent { get; set; } = 2.0m;
    public decimal MinProfitToExitOnSignalFlipPercent { get; set; } = 1.2m;
    public decimal? StopLossPercent { get; set; }
    public decimal? TakeProfitPercent { get; set; }

    public bool IsAtrMode => Mode.Equals(ModeAtr, StringComparison.OrdinalIgnoreCase);
    public decimal EffectiveFixedStopLossPercent => StopLossPercent ?? FixedStopLossPercent;
    public decimal EffectiveFixedTakeProfitPercent => TakeProfitPercent ?? FixedTakeProfitPercent;

    public static string NormalizeMode(string? mode) =>
        string.Equals(mode, ModeAtr, StringComparison.OrdinalIgnoreCase)
            ? ModeAtr
            : ModeFixedPercent;

    public int MaxHoldMinutes { get; set; } = 240;
    public decimal MaxSignalFlipLossExitPercent { get; set; } = -1.2m;
    public decimal TrailingActivationPercent { get; set; } = 1.5m;
    public decimal TrailingDistancePercent { get; set; } = 1.0m;
    public decimal ScoreDecayMinEntryScore { get; set; } = 0.90m;
    public decimal ScoreDecayDefensiveScore { get; set; } = 0.50m;
    public int ScoreDecayDefensiveCycles { get; set; } = 2;
    public decimal ScoreDecayImmediateScore { get; set; } = 0.40m;
    public int PostEntryAdverseWindowMinutes { get; set; } = 30;
    public decimal PostEntryAdverseLossPercent { get; set; } = 2.0m;
}
