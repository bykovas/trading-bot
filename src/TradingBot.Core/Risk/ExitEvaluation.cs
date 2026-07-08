using TradingBot.Core.Common;

namespace TradingBot.Core.Risk;

public sealed record ExitEvaluation(
    bool ShouldSell,
    ExitReason? ExitReason,
    string? HoldReasonCode,
    string Reason);

public sealed record ScoreDecaySnapshot(
    decimal? EntryScore,
    decimal CurrentScore,
    int ConsecutiveLowScoreCycles,
    bool ScoreConfirmsEntry,
    bool EmaBullish = true,
    bool MomentumPositive = true);

public sealed record PositionExitLevelsSnapshot(
    string Side,
    decimal? StopLossPrice,
    decimal? TakeProfitPrice,
    decimal ConservativeExitPrice);
