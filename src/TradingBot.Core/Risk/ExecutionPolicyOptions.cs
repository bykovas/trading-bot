namespace TradingBot.Core.Risk;

public sealed class ExecutionPolicyOptions
{
    public int CooldownAfterBuySeconds { get; set; } = 900;
    public int CooldownAfterSellSeconds { get; set; } = 1800;
    public int MinHoldSeconds { get; set; } = 900;
    public int MaxNewPositionsPerCycle { get; set; } = 0;
    public bool AllowImmediateExitOnSignalFlip { get; set; } = false;
    public int EntryBlackoutUtcFromHour { get; set; } = 0;
    public int EntryBlackoutMinutes { get; set; } = 60;
    public int MaxNewPositionsPerHour { get; set; } = 2;
    public int CooldownAfterStopLossSeconds { get; set; } = 14400;
}
