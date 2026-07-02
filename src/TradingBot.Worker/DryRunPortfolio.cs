using System.Text.Json;

namespace TradingBot.Worker;

internal sealed class DryRunPortfolio(DryRunOptions options, PortfolioOptions initialPortfolio)
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private string StatePath => Path.Combine(options.OutputDirectory, options.StateFile);
    private string EventsPath => Path.Combine(options.OutputDirectory, options.EventsFile);

    public PortfolioState Load()
    {
        Directory.CreateDirectory(options.OutputDirectory);

        if (File.Exists(StatePath))
        {
            try
            {
                var json = File.ReadAllText(StatePath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var state = JsonSerializer.Deserialize<PortfolioState>(json, _jsonOptions);
                    if (IsUsable(state))
                    {
                        state!.Positions ??= new List<PortfolioPosition>();
                        Console.WriteLine(
                            $"portfolio-load: reusing existing state from {StatePath} (cash {state.CashEur:0.##} EUR, positions {state.Positions.Count})");
                        return state;
                    }
                }

                Console.WriteLine(
                    $"portfolio-load: existing state at {StatePath} is empty or invalid; creating a fresh portfolio with {initialPortfolio.StartingCashEur:0.##} EUR");
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                Console.WriteLine(
                    $"portfolio-load: failed to read {StatePath} ({ex.Message}); creating a fresh portfolio with {initialPortfolio.StartingCashEur:0.##} EUR");
            }
        }
        else
        {
            Console.WriteLine(
                $"portfolio-load: no state file at {StatePath}; creating a fresh portfolio with {initialPortfolio.StartingCashEur:0.##} EUR");
        }

        var fresh = CreateInitialState();
        Save(fresh);
        return fresh;
    }

    private PortfolioState CreateInitialState() => new()
    {
        UpdatedAt = DateTimeOffset.UtcNow,
        CashEur = initialPortfolio.StartingCashEur,
        Positions = initialPortfolio.Positions
            .Select(position => new PortfolioPosition
            {
                Pair = position.Pair,
                Side = position.Side,
                Quantity = position.Quantity,
                EntryPrice = position.EntryPrice,
                EntryNotionalEur = position.EntryNotionalEur
            })
            .ToList()
    };

    private static bool IsUsable(PortfolioState? state) =>
        state is not null && (state.CashEur > 0m || state.Positions is { Count: > 0 });

    public PortfolioState CloneAndMark(PortfolioState state, IReadOnlyList<InstrumentMarketState> marketStates)
    {
        var clone = state.Clone();
        MarkToMarket(clone, marketStates);
        return clone;
    }

    public DryRunAction Apply(
        PortfolioState state,
        InstrumentMarketState marketState,
        DecisionProposal proposal,
        RiskEvaluation risk,
        RiskOptions riskOptions)
    {
        var position = state.Positions.FirstOrDefault(item => item.Pair.Equals(proposal.Pair, StringComparison.OrdinalIgnoreCase));
        var beforeCash = state.CashEur;
        var beforeValue = CalculateTotalValue(state);

        if (!risk.Approved)
        {
            return BuildAction("REJECTED", "risk rejected proposal", proposal, position, beforeCash, beforeValue, state);
        }

        if (proposal.DesiredPosition == "LONG_MICRO")
        {
            if (position is not null)
            {
                return BuildAction("WOULD_HOLD", "current position already matches desired long exposure", proposal, position, beforeCash, beforeValue, state);
            }

            if (state.Positions.Count >= riskOptions.MaxOpenPositions)
            {
                return BuildAction("WOULD_BUY_BLOCKED", $"max open positions {riskOptions.MaxOpenPositions} already reached", proposal, position, beforeCash, beforeValue, state);
            }

            if (proposal.TargetNotionalEur > state.CashEur)
            {
                return BuildAction("WOULD_BUY_BLOCKED", $"cash EUR {state.CashEur:0.##} is below target EUR {proposal.TargetNotionalEur:0.##}", proposal, position, beforeCash, beforeValue, state);
            }

            if (marketState.LastPrice <= 0m)
            {
                return BuildAction("WOULD_BUY_BLOCKED", "last price is zero", proposal, position, beforeCash, beforeValue, state);
            }

            var buyPrice = CalculateBuyPrice(marketState);
            var feeRate = FeeRate;
            var grossNotional = proposal.TargetNotionalEur / (1m + feeRate);
            var buyFee = proposal.TargetNotionalEur - grossNotional;
            var quantity = decimal.Round(grossNotional / buyPrice, 10);
            if (quantity <= 0m)
            {
                return BuildAction("WOULD_BUY_BLOCKED", "calculated quantity is zero", proposal, position, beforeCash, beforeValue, state);
            }

            var newPosition = new PortfolioPosition
            {
                Pair = proposal.Pair,
                Side = "LONG",
                Quantity = quantity,
                EntryPrice = buyPrice,
                EntryNotionalEur = proposal.TargetNotionalEur,
                LastPrice = marketState.LastPrice,
                MarketValueEur = CalculateLiquidationValue(quantity, marketState)
            };

            if (options.ApplyVirtualFills)
            {
                state.CashEur -= proposal.TargetNotionalEur;
                state.Positions.Add(newPosition);
                state.UpdatedAt = DateTimeOffset.UtcNow;
                MarkToMarket(state, new[] { marketState });
            }

            return BuildAction(
                "WOULD_BUY",
                $"open virtual long: gross EUR {grossNotional:0.####}, fee EUR {buyFee:0.####}, fill ask+slippage {buyPrice:0.####}",
                proposal,
                newPosition,
                beforeCash,
                beforeValue,
                state,
                buyPrice,
                buyFee,
                grossNotional,
                proposal.TargetNotionalEur);
        }

        if (position is null)
        {
            return BuildAction("NO_ORDER", "no current position and desired is none", proposal, position, beforeCash, beforeValue, state);
        }

        var sellPrice = CalculateSellPrice(marketState);
        var grossExitValue = position.Quantity * sellPrice;
        var sellFee = grossExitValue * FeeRate;
        var exitValue = grossExitValue - sellFee;
        var realizedPnl = exitValue - position.EntryNotionalEur;
        var realizedPnlPercent = position.EntryNotionalEur == 0m ? 0m : realizedPnl / position.EntryNotionalEur * 100m;

        if (options.ApplyVirtualFills)
        {
            state.CashEur += exitValue;
            state.Positions.Remove(position);
            state.UpdatedAt = DateTimeOffset.UtcNow;
            MarkToMarket(state, new[] { marketState });
        }

        return BuildAction(
            "WOULD_SELL",
            $"close virtual long: gross EUR {grossExitValue:0.####}, fee EUR {sellFee:0.####}, fill bid-slippage {sellPrice:0.####}, realized PnL EUR {realizedPnl:0.####} ({realizedPnlPercent:0.##}%)",
            proposal,
            position,
            beforeCash,
            beforeValue,
            state,
            sellPrice,
            sellFee,
            grossExitValue,
            exitValue);
    }

    public void Save(PortfolioState state)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, _jsonOptions));
    }

    public void AppendCycle(DryRunCycleRecord record)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        var line = JsonSerializer.Serialize(record, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.AppendAllText(EventsPath, line + Environment.NewLine);
    }

    public string GetStatePath() => StatePath;
    public string GetEventsPath() => EventsPath;

    private void MarkToMarket(PortfolioState state, IReadOnlyList<InstrumentMarketState> marketStates)
    {
        foreach (var position in state.Positions)
        {
            var marketState = marketStates.FirstOrDefault(item => item.Instrument.Pair.Equals(position.Pair, StringComparison.OrdinalIgnoreCase));
            if (marketState is null || marketState.LastPrice <= 0m)
            {
                continue;
            }

            position.LastPrice = marketState.LastPrice;
            position.MarketValueEur = CalculateLiquidationValue(position.Quantity, marketState);
            position.UnrealizedPnlEur = position.MarketValueEur - position.EntryNotionalEur;
            position.UnrealizedPnlPercent = position.EntryNotionalEur == 0m
                ? 0m
                : position.UnrealizedPnlEur / position.EntryNotionalEur * 100m;
        }
    }

    private static decimal CalculateTotalValue(PortfolioState state) =>
        state.CashEur + state.Positions.Sum(position => position.MarketValueEur);

    private decimal FeeRate => options.TakerFeeBps / 10_000m;

    private decimal SlippageRate => options.SlippageBps / 10_000m;

    private decimal CalculateBuyPrice(InstrumentMarketState marketState) =>
        marketState.BestAsk * (1m + SlippageRate);

    private decimal CalculateSellPrice(InstrumentMarketState marketState) =>
        marketState.BestBid * (1m - SlippageRate);

    private decimal CalculateLiquidationValue(decimal quantity, InstrumentMarketState marketState)
    {
        var grossValue = quantity * CalculateSellPrice(marketState);
        return grossValue - grossValue * FeeRate;
    }

    private static DryRunAction BuildAction(
        string action,
        string reason,
        DecisionProposal proposal,
        PortfolioPosition? position,
        decimal beforeCash,
        decimal beforeValue,
        PortfolioState afterState,
        decimal fillPrice = 0m,
        decimal feeEur = 0m,
        decimal grossNotionalEur = 0m,
        decimal netNotionalEur = 0m)
    {
        var afterValue = CalculateTotalValue(afterState);
        return new DryRunAction
        {
            Pair = proposal.Pair,
            Action = action,
            Reason = reason,
            DesiredPosition = proposal.DesiredPosition,
            TargetNotionalEur = proposal.TargetNotionalEur,
            Quantity = action == "WOULD_BUY"
                ? position?.Quantity ?? 0m
                : position?.Quantity ?? 0m,
            EntryPrice = position?.EntryPrice ?? 0m,
            LastPrice = position?.LastPrice ?? 0m,
            FillPrice = fillPrice,
            FeeEur = feeEur,
            GrossNotionalEur = grossNotionalEur,
            NetNotionalEur = netNotionalEur,
            CashBeforeEur = beforeCash,
            CashAfterEur = afterState.CashEur,
            PortfolioValueBeforeEur = beforeValue,
            PortfolioValueAfterEur = afterValue
        };
    }
}

internal sealed class PortfolioState
{
    public DateTimeOffset UpdatedAt { get; set; }
    public decimal CashEur { get; set; }
    public List<PortfolioPosition> Positions { get; set; } = new();

    public decimal PositionsValueEur => Positions.Sum(position => position.MarketValueEur);
    public decimal TotalValueEur => CashEur + PositionsValueEur;

    public PortfolioState Clone() => new()
    {
        UpdatedAt = UpdatedAt,
        CashEur = CashEur,
        Positions = Positions.Select(position => position.Clone()).ToList()
    };
}

internal sealed class PortfolioPosition
{
    public string Pair { get; set; } = string.Empty;
    public string Side { get; set; } = "LONG";
    public decimal Quantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal EntryNotionalEur { get; set; }
    public decimal LastPrice { get; set; }
    public decimal MarketValueEur { get; set; }
    public decimal UnrealizedPnlEur { get; set; }
    public decimal UnrealizedPnlPercent { get; set; }

    public PortfolioPosition Clone() => new()
    {
        Pair = Pair,
        Side = Side,
        Quantity = Quantity,
        EntryPrice = EntryPrice,
        EntryNotionalEur = EntryNotionalEur,
        LastPrice = LastPrice,
        MarketValueEur = MarketValueEur,
        UnrealizedPnlEur = UnrealizedPnlEur,
        UnrealizedPnlPercent = UnrealizedPnlPercent
    };
}

internal sealed class DryRunAction
{
    public string Pair { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string DesiredPosition { get; set; } = string.Empty;
    public decimal TargetNotionalEur { get; set; }
    public decimal Quantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal LastPrice { get; set; }
    public decimal FillPrice { get; set; }
    public decimal FeeEur { get; set; }
    public decimal GrossNotionalEur { get; set; }
    public decimal NetNotionalEur { get; set; }
    public decimal CashBeforeEur { get; set; }
    public decimal CashAfterEur { get; set; }
    public decimal PortfolioValueBeforeEur { get; set; }
    public decimal PortfolioValueAfterEur { get; set; }
}

internal sealed class DryRunCycleRecord
{
    public required string CycleId { get; init; }
    public required DateTimeOffset Utc { get; init; }
    public required string MarketDataMode { get; init; }
    public required string AiProvider { get; init; }
    public required IReadOnlyList<string> ActivePairs { get; init; }
    public required IReadOnlyList<DryRunDecisionRecord> Decisions { get; init; }
    public required PortfolioState PortfolioBefore { get; init; }
    public required PortfolioState PortfolioAfter { get; init; }
}

internal sealed class DryRunDecisionRecord
{
    public required string Pair { get; init; }
    public required decimal Price { get; init; }
    public required decimal? FastEma { get; init; }
    public required decimal? SlowEma { get; init; }
    public required decimal? Rsi { get; init; }
    public required string DesiredPosition { get; init; }
    public required decimal Score { get; init; }
    public required bool RiskApproved { get; init; }
    public required IReadOnlyList<string> RiskReasons { get; init; }
    public required IReadOnlyList<SignalContribution> Contributions { get; init; }
    public required DryRunAction DryRunAction { get; init; }
}
