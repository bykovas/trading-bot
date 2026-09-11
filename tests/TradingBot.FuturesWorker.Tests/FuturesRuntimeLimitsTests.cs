using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class FuturesRuntimeLimitsTests
{
    [Fact]
    public void Empty_overrides_preserve_the_normalized_configuration_envelope()
    {
        var config = Configuration();

        var result = FuturesRuntimeLimits.Resolve(
            config,
            new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase));

        Assert.False(result.Limits.HasDatabaseOverrides);
        Assert.False(result.Limits.FixedSizingEnabled);
        Assert.Equal(10m, result.Limits.PositionMarginUsd);
        Assert.Equal(1m, result.Limits.Leverage);
        Assert.Equal(3, result.Limits.MaxOpenPositions);
        Assert.Equal(1, result.Limits.MaxOpenPositionsPerGroup);
        Assert.Equal(10m, result.Limits.MaxNotionalUsd);
        Assert.Equal(30m, result.Limits.MaxTotalNotionalUsd);
        Assert.Equal(2.7m, result.Limits.MaxConcurrentOpenRiskUsd);
        Assert.Equal(80m, result.Limits.MaxAccountMarginUtilizationPercent);
    }

    [Theory]
    [InlineData(10, 1, 10)]
    [InlineData(15, 10, 150)]
    [InlineData(30, 2, 60)]
    public void Sizing_overrides_produce_the_declared_margin_leverage_and_notional(
        decimal margin,
        decimal leverage,
        decimal expectedNotional)
    {
        var config = Configuration();
        var result = FuturesRuntimeLimits.Resolve(
            config,
            Overrides(
                margin,
                leverage,
                maxPositions: 6,
                maxGroupPositions: 2));
        config.SetRuntimeLimits(result.Limits);

        var plan = FuturesPositionSizer.Size(
            config,
            atrPct: 1m,
            new ExecutionCostEstimate("test", 0.05m, 0.05m, 0m, 0m, 0.1m),
            result.Limits.Leverage);

        Assert.True(result.Limits.HasDatabaseOverrides);
        Assert.True(result.Limits.FixedSizingEnabled);
        Assert.Equal(expectedNotional, result.Limits.PositionNotionalUsd);
        Assert.Equal(expectedNotional, plan.SizedNotionalEur);
        Assert.Equal(margin, plan.RequiredMarginEur);
        Assert.Equal(leverage, plan.EffectiveLeverage);
        Assert.Equal(expectedNotional * 6m, result.Limits.MaxTotalNotionalUsd);
        Assert.Equal(expectedNotional * 2m, result.Limits.MaxExposureUsdPerGroup);
        Assert.Equal(0m, result.Limits.MaxConcurrentOpenRiskUsd);
        Assert.Equal(100m, result.Limits.MaxAccountMarginUtilizationPercent);
    }

    [Fact]
    public void Partial_override_uses_configuration_for_missing_values()
    {
        var config = Configuration();
        var result = FuturesRuntimeLimits.Resolve(
            config,
            new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                [BotConfigOverrideKeys.MaxOpenPositions] = 5m
            });

        Assert.True(result.Limits.HasDatabaseOverrides);
        Assert.False(result.Limits.FixedSizingEnabled);
        Assert.Equal(10m, result.Limits.PositionMarginUsd);
        Assert.Equal(1m, result.Limits.Leverage);
        Assert.Equal(5, result.Limits.MaxOpenPositions);
        Assert.Equal(50m, result.Limits.MaxTotalNotionalUsd);
    }

    [Fact]
    public void Invalid_values_fall_back_and_group_count_cannot_exceed_total_slots()
    {
        var config = Configuration();
        var result = FuturesRuntimeLimits.Resolve(
            config,
            new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                [BotConfigOverrideKeys.PositionMarginUsd] = -1m,
                [BotConfigOverrideKeys.Leverage] = 11m,
                [BotConfigOverrideKeys.MaxOpenPositions] = 2m,
                [BotConfigOverrideKeys.MaxOpenPositionsPerGroup] = 4m,
                ["future_key"] = 123m
            });

        Assert.Equal(10m, result.Limits.PositionMarginUsd);
        Assert.Equal(1m, result.Limits.Leverage);
        Assert.Equal(2, result.Limits.MaxOpenPositions);
        Assert.Equal(2, result.Limits.MaxOpenPositionsPerGroup);
        Assert.Contains(result.Warnings, warning => warning.Contains("position_margin_usd", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("leverage", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("future_key", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("exceeds max positions", StringComparison.Ordinal));
    }

    [Fact]
    public void Zero_margin_override_pauses_new_entries_without_zeroing_the_effective_size()
    {
        var config = Configuration();
        var result = FuturesRuntimeLimits.Resolve(
            config,
            new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                [BotConfigOverrideKeys.PositionMarginUsd] = 0m
            });

        Assert.True(result.Limits.HasDatabaseOverrides);
        Assert.True(result.Limits.NewEntriesPaused);
        Assert.False(result.Limits.FixedSizingEnabled);
        Assert.Equal(10m, result.Limits.PositionMarginUsd);
        Assert.Equal(10m, result.Limits.PositionNotionalUsd);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains(BotConfigOverrideKeys.PositionMarginUsd, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Refresh_applies_changes_deletion_and_retains_last_snapshot_on_failure()
    {
        var config = Configuration();
        var store = new MutableOverrideStore
        {
            Values = Overrides(30m, 2m, maxPositions: 4, maxGroupPositions: 2)
        };
        var provider = new FuturesRuntimeLimitProvider(config, store);

        await provider.RefreshAsync(CancellationToken.None);
        Assert.Equal(60m, config.RuntimeLimits.PositionNotionalUsd);
        Assert.Equal(4, config.RuntimeLimits.MaxOpenPositions);

        store.ThrowOnLoad = true;
        await provider.RefreshAsync(CancellationToken.None);
        Assert.Equal(60m, config.RuntimeLimits.PositionNotionalUsd);
        Assert.Equal(4, config.RuntimeLimits.MaxOpenPositions);

        store.ThrowOnLoad = false;
        store.Values = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        await provider.RefreshAsync(CancellationToken.None);
        Assert.False(config.RuntimeLimits.HasDatabaseOverrides);
        Assert.Equal(10m, config.RuntimeLimits.PositionNotionalUsd);
        Assert.Equal(3, config.RuntimeLimits.MaxOpenPositions);
    }

    [Fact]
    public async Task Stores_are_queried_with_the_runtime_instance_id()
    {
        var config = Configuration();
        config.BotInstance.Id = "futures-lukas-live";
        var store = new MutableOverrideStore
        {
            Values = Overrides(15m, 10m, maxPositions: 3, maxGroupPositions: 1)
        };
        var provider = new FuturesRuntimeLimitProvider(config, store);

        await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal("futures-lukas-live", store.LastBotInstanceId);
        Assert.Equal(150m, config.RuntimeLimits.PositionNotionalUsd);
    }

    [Fact]
    public void Runtime_slot_override_replaces_stale_static_capacity_caps()
    {
        var config = Configuration();
        var resolution = FuturesRuntimeLimits.Resolve(
            config,
            new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                [BotConfigOverrideKeys.MaxOpenPositions] = 4m
            });
        config.SetRuntimeLimits(resolution.Limits);

        var state = new PortfolioState { CashEur = 100m };
        for (var index = 0; index < 3; index++)
        {
            state.Positions.Add(new PortfolioPosition
            {
                Pair = $"TEST{index}/USD",
                Side = "LONG",
                Quantity = 1m,
                EntryPrice = 10m,
                LastPrice = 10m,
                EntryNotionalEur = 10m,
                InitialMarginEur = 10m,
                MarketValueEur = 10m,
                StopLossPrice = 9.8m
            });
        }

        var evaluation = new MarginRiskManager(config).EvaluateEntry(new FuturesEntryRiskInputs(
            state,
            FuturesDesiredExposure.Long,
            MarkPrice: 10m,
            TargetNotionalEur: 10m,
            FilledNotionalEur: 10m,
            Leverage: 1m,
            UsedMarginEur: 30m,
            FundingRatePercent: 0m,
            AtrPct: 1m,
            StopDistancePct: 1m,
            TakeProfitDistancePct: 2m,
            Volume24hUsd: config.Filters.MinQuoteVolume24h,
            ExitDepthEur: 10m * config.Filters.MinExitDepthMultiple,
            ProjectedOpenRiskEur: 100m,
            BtcAllowsLongs: true,
            BtcRegimeState: "test",
            ShortAllowed: true,
            ShortBlockReason: null));

        Assert.True(evaluation.Approved);
        Assert.Equal(4, config.RuntimeLimits.MaxOpenPositions);
        Assert.Equal(0m, config.RuntimeLimits.MaxConcurrentOpenRiskUsd);
        Assert.Equal(40m, config.RuntimeLimits.MaxTotalNotionalUsd);
    }

    private static FuturesBotConfiguration Configuration() => new()
    {
        BotInstance = new BotInstanceOptions { Id = "futures-live", Name = "Test" },
        Futures = new FuturesOptions
        {
            TargetMarginUsd = 10m,
            DefaultLeverage = 1m,
            MaxLeverage = 1m,
            MaxPositions = 3,
            MaxNotionalUsd = 10m,
            MaxMarginPerPositionUsd = 10m,
            MaxTotalNotionalUsd = 30m
        },
        Risk = new FuturesRiskOptions
        {
            TargetRiskUsd = 0.9m,
            MaxConcurrentOpenRiskUsd = 2.7m
        },
        Margin = new MarginOptions { MaxAccountMarginUtilizationPercent = 80m },
        CorrelationRisk = new CorrelationRiskOptions
        {
            MaxOpenPositionsPerGroup = 1,
            MaxExposureUsdPerGroup = 400m
        },
        TpSl = new TpSlOptions { StopLossPercent = 1.75m, TakeProfitPercent = 3.5m },
        Exits = new FuturesExitOptions
        {
            StopDistanceCapPct = 3m,
            MinStopDistancePct = 0.3m,
            MinRewardRiskMultiple = 2m,
            MinTpVsCostMult = 3m
        }
    };

    private static IReadOnlyDictionary<string, decimal> Overrides(
        decimal margin,
        decimal leverage,
        int maxPositions,
        int maxGroupPositions) =>
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            [BotConfigOverrideKeys.PositionMarginUsd] = margin,
            [BotConfigOverrideKeys.Leverage] = leverage,
            [BotConfigOverrideKeys.MaxOpenPositions] = maxPositions,
            [BotConfigOverrideKeys.MaxOpenPositionsPerGroup] = maxGroupPositions
        };

    private sealed class MutableOverrideStore : IBotConfigOverrideStore
    {
        public IReadOnlyDictionary<string, decimal> Values { get; set; } =
            new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        public bool ThrowOnLoad { get; set; }
        public string? LastBotInstanceId { get; private set; }

        public Task<IReadOnlyDictionary<string, decimal>> LoadAsync(
            string botInstanceId,
            CancellationToken cancellationToken)
        {
            LastBotInstanceId = botInstanceId;
            if (ThrowOnLoad)
            {
                throw new InvalidOperationException("database unavailable");
            }

            return Task.FromResult(Values);
        }
    }
}
