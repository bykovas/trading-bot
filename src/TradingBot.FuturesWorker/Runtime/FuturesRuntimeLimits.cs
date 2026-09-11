namespace TradingBot.FuturesWorker;

internal static class BotConfigOverrideKeys
{
    public const string PositionMarginUsd = "position_margin_usd";
    public const string Leverage = "leverage";
    public const string MaxOpenPositions = "max_open_positions";
    public const string MaxOpenPositionsPerGroup = "max_open_positions_per_group";

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(
        [PositionMarginUsd, Leverage, MaxOpenPositions, MaxOpenPositionsPerGroup],
        StringComparer.OrdinalIgnoreCase);
}

internal sealed record FuturesRuntimeLimits(
    decimal PositionMarginUsd,
    decimal Leverage,
    decimal MaxLeverage,
    int MaxOpenPositions,
    int MaxOpenPositionsPerGroup,
    decimal PositionNotionalUsd,
    decimal MaxNotionalUsd,
    decimal MaxMarginPerPositionUsd,
    decimal MaxTotalNotionalUsd,
    decimal MaxExposureUsdPerGroup,
    decimal MaxConcurrentOpenRiskUsd,
    decimal MaxAccountMarginUtilizationPercent,
    bool FixedSizingEnabled,
    bool HasDatabaseOverrides,
    bool NewEntriesPaused)
{
    public static FuturesRuntimeLimits FromConfiguration(FuturesBotConfiguration config)
    {
        var maxLeverage = Math.Clamp(
            config.Futures.MaxLeverage <= 0m
                ? FuturesBotConfiguration.MaxLeverageCeiling
                : config.Futures.MaxLeverage,
            1m,
            FuturesBotConfiguration.MaxLeverageCeiling);
        var leverage = Math.Clamp(
            config.Futures.DefaultLeverage <= 0m ? 1m : config.Futures.DefaultLeverage,
            1m,
            maxLeverage);
        var margin = config.Futures.TargetMarginUsd <= 0m ? 10m : config.Futures.TargetMarginUsd;
        var positions = Math.Clamp(
            config.Futures.MaxPositions <= 0 ? 3 : config.Futures.MaxPositions,
            1,
            FuturesBotConfiguration.MaxPositionsCeiling);
        var groupPositions = Math.Max(1, config.CorrelationRisk.MaxOpenPositionsPerGroup);

        return new FuturesRuntimeLimits(
            PositionMarginUsd: margin,
            Leverage: leverage,
            MaxLeverage: maxLeverage,
            MaxOpenPositions: positions,
            MaxOpenPositionsPerGroup: groupPositions,
            PositionNotionalUsd: margin * leverage,
            MaxNotionalUsd: config.Futures.MaxNotionalUsd,
            MaxMarginPerPositionUsd: config.Futures.MaxMarginPerPositionUsd,
            MaxTotalNotionalUsd: config.Futures.MaxTotalNotionalUsd,
            MaxExposureUsdPerGroup: config.CorrelationRisk.MaxExposureUsdPerGroup,
            MaxConcurrentOpenRiskUsd: config.Risk.MaxConcurrentOpenRiskUsd,
            MaxAccountMarginUtilizationPercent: config.Margin.MaxAccountMarginUtilizationPercent,
            FixedSizingEnabled: false,
            HasDatabaseOverrides: false,
            NewEntriesPaused: false);
    }

    public static FuturesRuntimeLimitResolution Resolve(
        FuturesBotConfiguration config,
        IReadOnlyDictionary<string, decimal> overrides)
    {
        var fallback = FromConfiguration(config);
        var warnings = overrides.Keys
            .Where(key => !BotConfigOverrideKeys.Known.Contains(key))
            .Select(key => $"unknown override '{key}' ignored")
            .ToList();

        // An explicit database zero is an operator pause command, not an invalid size.
        // Keep the prior effective sizing intact so a resumed worker has no transient
        // zero-sized calculations; entry paths observe NewEntriesPaused before sizing.
        var newEntriesPaused = overrides.TryGetValue(BotConfigOverrideKeys.PositionMarginUsd, out var requestedMargin)
            && requestedMargin == 0m;
        bool marginOverridden;
        decimal margin;
        if (newEntriesPaused)
        {
            margin = fallback.PositionMarginUsd;
            marginOverridden = false;
        }
        else
        {
            margin = ReadDecimal(
                overrides,
                BotConfigOverrideKeys.PositionMarginUsd,
                fallback.PositionMarginUsd,
                value => value is > 0m and <= 1_000_000m,
                warnings,
                out marginOverridden);
        }
        var leverage = ReadDecimal(
            overrides,
            BotConfigOverrideKeys.Leverage,
            fallback.Leverage,
            value => value is >= 1m and <= FuturesBotConfiguration.MaxLeverageCeiling,
            warnings,
            out var leverageOverridden);
        var maxPositions = ReadInteger(
            overrides,
            BotConfigOverrideKeys.MaxOpenPositions,
            fallback.MaxOpenPositions,
            1,
            FuturesBotConfiguration.MaxPositionsCeiling,
            warnings,
            out var maxPositionsOverridden);
        var groupPositions = ReadInteger(
            overrides,
            BotConfigOverrideKeys.MaxOpenPositionsPerGroup,
            fallback.MaxOpenPositionsPerGroup,
            1,
            FuturesBotConfiguration.MaxPositionsCeiling,
            warnings,
            out var groupPositionsOverridden);

        if (groupPositions > maxPositions)
        {
            warnings.Add(
                $"override '{BotConfigOverrideKeys.MaxOpenPositionsPerGroup}'={groupPositions} exceeds max positions {maxPositions}; effective value is {maxPositions}");
            groupPositions = maxPositions;
        }

        var hasOverrides = newEntriesPaused || marginOverridden || leverageOverridden || maxPositionsOverridden || groupPositionsOverridden;
        if (!hasOverrides)
        {
            return new FuturesRuntimeLimitResolution(fallback, warnings);
        }

        var fixedSizing = marginOverridden || leverageOverridden;
        var notional = decimal.Round(margin * leverage, 8);
        var maxLeverage = fixedSizing ? leverage : fallback.MaxLeverage;

        return new FuturesRuntimeLimitResolution(
            new FuturesRuntimeLimits(
                PositionMarginUsd: margin,
                Leverage: leverage,
                MaxLeverage: maxLeverage,
                MaxOpenPositions: maxPositions,
                MaxOpenPositionsPerGroup: groupPositions,
                PositionNotionalUsd: notional,
                MaxNotionalUsd: notional,
                MaxMarginPerPositionUsd: margin,
                MaxTotalNotionalUsd: decimal.Round(notional * maxPositions, 8),
                MaxExposureUsdPerGroup: decimal.Round(notional * groupPositions, 8),
                // Total notional, slot count and the stop-distance ceiling form the runtime
                // risk envelope. A stale appsettings heat cap must not bind before it.
                MaxConcurrentOpenRiskUsd: 0m,
                // Free collateral remains a hard check. The static utilization percentage
                // must not make a DB-declared number of fully funded slots unreachable.
                MaxAccountMarginUtilizationPercent: 100m,
                FixedSizingEnabled: fixedSizing,
                HasDatabaseOverrides: true,
                NewEntriesPaused: newEntriesPaused),
            warnings);
    }

    public string Describe() =>
        $"source={(HasDatabaseOverrides ? "database" : "appsettings")} marginUsd={PositionMarginUsd:0.####} leverage={Leverage:0.####}x notionalUsd={PositionNotionalUsd:0.####} positions={MaxOpenPositions} groupPositions={MaxOpenPositionsPerGroup} fixedSizing={FixedSizingEnabled} newEntriesPaused={NewEntriesPaused}";

    private static decimal ReadDecimal(
        IReadOnlyDictionary<string, decimal> overrides,
        string key,
        decimal fallback,
        Func<decimal, bool> valid,
        ICollection<string> warnings,
        out bool applied)
    {
        applied = false;
        if (!overrides.TryGetValue(key, out var value))
        {
            return fallback;
        }

        if (!valid(value))
        {
            warnings.Add($"override '{key}'={value} is invalid; appsettings value {fallback} retained");
            return fallback;
        }

        applied = true;
        return value;
    }

    private static int ReadInteger(
        IReadOnlyDictionary<string, decimal> overrides,
        string key,
        int fallback,
        int minimum,
        int maximum,
        ICollection<string> warnings,
        out bool applied)
    {
        applied = false;
        if (!overrides.TryGetValue(key, out var value))
        {
            return fallback;
        }

        if (value != decimal.Truncate(value) || value < minimum || value > maximum)
        {
            warnings.Add($"override '{key}'={value} is invalid; appsettings value {fallback} retained");
            return fallback;
        }

        applied = true;
        return decimal.ToInt32(value);
    }
}

internal sealed record FuturesRuntimeLimitResolution(
    FuturesRuntimeLimits Limits,
    IReadOnlyList<string> Warnings);

internal sealed class FuturesRuntimeLimitProvider(
    FuturesBotConfiguration config,
    IBotConfigOverrideStore store)
{
    private string? _lastWarningSignature;
    private string? _lastFailureMessage;

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var values = await store.LoadAsync(config.BotInstance.Id, cancellationToken);
            var resolution = FuturesRuntimeLimits.Resolve(config, values);
            var previous = config.RuntimeLimits;
            config.SetRuntimeLimits(resolution.Limits);

            if (_lastFailureMessage is not null)
            {
                Console.WriteLine($"bot-config-overrides: instance={config.BotInstance.Id} refresh recovered");
                _lastFailureMessage = null;
            }

            if (previous != resolution.Limits)
            {
                Console.WriteLine($"bot-config-overrides: instance={config.BotInstance.Id} {resolution.Limits.Describe()}");
            }

            var warningSignature = string.Join("|", resolution.Warnings);
            if (!string.Equals(_lastWarningSignature, warningSignature, StringComparison.Ordinal))
            {
                foreach (var warning in resolution.Warnings)
                {
                    Console.WriteLine($"bot-config-overrides: instance={config.BotInstance.Id} warning={warning}");
                }

                _lastWarningSignature = warningSignature;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            if (!string.Equals(_lastFailureMessage, error.Message, StringComparison.Ordinal))
            {
                Console.WriteLine(
                    $"bot-config-overrides: instance={config.BotInstance.Id} refresh failed; retaining last effective values: {error.Message}");
                _lastFailureMessage = error.Message;
            }
        }
    }
}
