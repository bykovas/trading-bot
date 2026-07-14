namespace TradingBot.Web.Models;

public sealed record ShellViewModel(string? BotInstanceId, BotStatusViewModel Status, IReadOnlyList<string> BotInstances);

public sealed record BotStatusViewModel(
    DateTimeOffset Utc,
    string? BotInstanceId,
    string RuntimeState,
    string MarketDataMode,
    DateTimeOffset? LatestCycleUtc,
    double? LatestCycleAgeSeconds,
    string Label,
    string Tone);

public sealed record DashboardViewModel(ShellViewModel Shell, PortfolioSummaryViewModel? Summary, IReadOnlyList<PortfolioPositionViewModel> Positions, string? Notice);

public sealed record PortfolioSummaryViewModel(
    DateTime UpdatedAt,
    decimal CashEur,
    decimal PositionsValueEur,
    decimal TotalValueEur,
    int OpenPositions,
    decimal DailyRealizedPnlEur,
    decimal ExternalPnlEur);

public sealed record PortfolioPositionViewModel(
    string Pair,
    string Side,
    decimal Quantity,
    decimal EntryPrice,
    decimal LastPrice,
    decimal UnrealizedPnlEur,
    decimal UnrealizedPnlPercent,
    decimal? Leverage,
    DateTimeOffset? OpenedAt);

public sealed record PageViewModel<T>(ShellViewModel Shell, IReadOnlyList<T> Items, int Limit, int Offset, int? NextOffset, string? Notice = null);

public sealed record CycleListItemViewModel(
    string CycleId,
    string BotInstanceId,
    DateTimeOffset Utc,
    string Action,
    string Pair,
    decimal? Score,
    decimal? TotalValueEur,
    string? WorkerVersion,
    string? ChangeSet);

public sealed record CycleDetailViewModel(ShellViewModel Shell, string CycleId, string BotInstanceId, DateTimeOffset Utc, string RawJson, IReadOnlyList<DecisionViewModel> Decisions);

public sealed record DecisionViewModel(string Pair, string Desired, string Action, decimal? Score, string Reason, string? RiskReason);

public sealed record MarketSnapshotViewModel(string Pair, string BotInstanceId, DateTimeOffset Utc, decimal? Price, decimal? Bid, decimal? Ask, decimal? ChangePercent, decimal? Volume24h);

public sealed record SimulationInput(int LastHours, double Spread, double Score, double StopLoss, double TakeProfit, double Notional, double Fee, bool BtcFilter, string Exclude);

public sealed record SimulationViewModel(ShellViewModel Shell, SimulationInput Input, SimulationResultViewModel? Result, string? Notice);

public sealed record SimulationResultViewModel(int Candidates, int Trades, decimal GrossPnl, decimal Fees, decimal NetPnl, decimal WinRate);
