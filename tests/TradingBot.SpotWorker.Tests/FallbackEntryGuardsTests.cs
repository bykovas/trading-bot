using TradingBot.SpotWorker;
using Xunit;

namespace TradingBot.SpotWorker.Tests;

// Pure re-validation of the BUY intent + execution/risk constraints immediately
// before the IOC taker fallback. Every rejection branch is exercised here so the
// worker-level integration tests can stay focused on the order plumbing.
public class FallbackEntryGuardsTests
{
    // A baseline that passes every guard; each test mutates exactly one input.
    private static FallbackEntryGuards.Inputs Allowing() => new(
        OriginalMakerBid: 100m,
        FreshBid: 100m,
        FreshAsk: 100.05m,
        QuoteFresh: true,
        SpreadPercent: 0.05m,
        MaxEntrySpreadPercent: 0.5m,
        MaxBuySlippagePercent: 0.10m,
        TargetNotionalEur: 10m,
        Volume: 0.1m,
        PairDecimals: 2,
        OrderMinimum: 0.01m,
        CostMinimum: 0.5m,
        PositionAlreadyOpen: false,
        EntryInFlight: false,
        OpenPositions: 0,
        MaxOpenPositions: 5,
        CashEur: 100m,
        CashReserveEur: 0m,
        CurrentExposureEur: 0m,
        MaxTotalExposureEur: 0m,
        RecentEntriesLastHour: 0,
        MaxNewPositionsPerHour: 0);

    [Fact]
    public void Allows_and_rounds_ioc_price_to_tick_when_all_guards_pass()
    {
        var result = FallbackEntryGuards.Evaluate(Allowing());

        Assert.Equal(FallbackEntryGuards.Verdict.Allow, result.Verdict);
        Assert.Equal(100.05m, result.IocPrice);
    }

    [Fact]
    public void Rounds_ioc_price_down_to_pair_tick_size()
    {
        var result = FallbackEntryGuards.Evaluate(Allowing() with { FreshAsk = 100.0579m });

        Assert.Equal(FallbackEntryGuards.Verdict.Allow, result.Verdict);
        Assert.Equal(100.05m, result.IocPrice);
    }

    [Fact]
    public void Rejects_when_ask_exceeds_slippage_cap()
    {
        var result = FallbackEntryGuards.Evaluate(Allowing() with { FreshAsk = 100.2m });

        Assert.Equal(FallbackEntryGuards.Verdict.RejectSlippage, result.Verdict);
    }

    [Fact]
    public void Interprets_slippage_cap_as_tenth_of_a_percent_not_ten_percent()
    {
        // With bid 100 and MaxBuySlippagePercent 0.10, the cap is 100.10 (= +0.10%),
        // NOT 110 (= +10%). An ask of 101 must be rejected; only <= 100.10 is allowed.
        Assert.Equal(FallbackEntryGuards.Verdict.RejectSlippage,
            FallbackEntryGuards.Evaluate(Allowing() with { FreshAsk = 101m, SpreadPercent = 0.05m }).Verdict);

        var allowed = FallbackEntryGuards.Evaluate(Allowing() with { FreshAsk = 100.10m });
        Assert.Equal(FallbackEntryGuards.Verdict.Allow, allowed.Verdict);
        Assert.Equal(100.10m, allowed.MaxAllowedPrice);
    }

    [Fact]
    public void Rejects_when_spread_exceeds_entry_ceiling()
    {
        var result = FallbackEntryGuards.Evaluate(Allowing() with { SpreadPercent = 0.6m });

        Assert.Equal(FallbackEntryGuards.Verdict.RejectSpread, result.Verdict);
    }

    [Fact]
    public void Rejects_when_quote_is_stale()
    {
        Assert.Equal(FallbackEntryGuards.Verdict.RejectStaleQuote,
            FallbackEntryGuards.Evaluate(Allowing() with { QuoteFresh = false }).Verdict);
    }

    [Fact]
    public void Rejects_when_quote_is_crossed()
    {
        Assert.Equal(FallbackEntryGuards.Verdict.RejectStaleQuote,
            FallbackEntryGuards.Evaluate(Allowing() with { FreshBid = 100.10m, FreshAsk = 100.05m }).Verdict);
    }

    [Fact]
    public void Rejects_when_position_already_open()
    {
        Assert.Equal(FallbackEntryGuards.Verdict.RejectRisk,
            FallbackEntryGuards.Evaluate(Allowing() with { PositionAlreadyOpen = true }).Verdict);
    }

    [Fact]
    public void Rejects_when_another_entry_is_in_flight()
    {
        Assert.Equal(FallbackEntryGuards.Verdict.RejectRisk,
            FallbackEntryGuards.Evaluate(Allowing() with { EntryInFlight = true }).Verdict);
    }

    [Fact]
    public void Rejects_when_max_open_positions_reached_after_maker_phase()
    {
        Assert.Equal(FallbackEntryGuards.Verdict.RejectRisk,
            FallbackEntryGuards.Evaluate(Allowing() with { OpenPositions = 5, MaxOpenPositions = 5 }).Verdict);
    }

    [Fact]
    public void Rejects_when_cash_reserve_would_be_breached()
    {
        Assert.Equal(FallbackEntryGuards.Verdict.RejectRisk,
            FallbackEntryGuards.Evaluate(Allowing() with { CashEur = 12m, CashReserveEur = 5m, TargetNotionalEur = 10m }).Verdict);
    }

    [Fact]
    public void Rejects_when_max_exposure_would_be_exceeded()
    {
        Assert.Equal(FallbackEntryGuards.Verdict.RejectRisk,
            FallbackEntryGuards.Evaluate(Allowing() with { CurrentExposureEur = 45m, TargetNotionalEur = 10m, MaxTotalExposureEur = 50m }).Verdict);
    }

    [Fact]
    public void Rejects_when_hourly_entry_limit_reached()
    {
        Assert.Equal(FallbackEntryGuards.Verdict.RejectRisk,
            FallbackEntryGuards.Evaluate(Allowing() with { RecentEntriesLastHour = 2, MaxNewPositionsPerHour = 2 }).Verdict);
    }

    [Fact]
    public void Rejects_when_below_pair_volume_minimum()
    {
        Assert.Equal(FallbackEntryGuards.Verdict.RejectRisk,
            FallbackEntryGuards.Evaluate(Allowing() with { Volume = 0.001m, OrderMinimum = 0.01m }).Verdict);
    }

    [Fact]
    public void Rejects_when_below_pair_cost_minimum()
    {
        Assert.Equal(FallbackEntryGuards.Verdict.RejectRisk,
            FallbackEntryGuards.Evaluate(Allowing() with { Volume = 0.1m, CostMinimum = 100m }).Verdict);
    }
}
