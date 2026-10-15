using System.Net;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class PukoReadOnlyChecksTests
{
    private const string Registry = """
        {"result":"success","instruments":[
        {"symbol":"PF_DOGEUSD","tradeable":true,"tickSize":0.00001,"contractValueTradePrecision":0},
        {"symbol":"PF_BADUSD","tradeable":true,"postOnly":true,"tickSize":1,"contractValueTradePrecision":0}]}
        """;
    private const string Fees = """
        {"result":"success","feeSchedules":[{"uid":"test","tiers":[
        {"usdVolume":0,"makerFee":0.02,"takerFee":0.05},
        {"usdVolume":1000,"makerFee":-0.01,"takerFee":0.03}]}]}
        """;

    [Theory]
    [InlineData("Long", "limit=0.10035")]
    [InlineData("Short", "limit=0.09965")]
    public async Task Check_is_indicative_and_rounds_without_mutating_paper_sizing(string side, string limit)
    {
        var (checks, _, _, _) = Setup();
        await checks.RefreshAsync(default);
        var result = await checks.CheckAsync(new() { Pair = "DOGE/USD", KrakenPair = "PF_DOGEUSD" }, Enum.Parse<FuturesDesiredExposure>(side), .1m, 50.09m, default);
        Assert.Contains("local_preflight=OK", result);
        Assert.Contains("quantity=500", result);
        Assert.Contains(limit, result);
        Assert.Contains("notionalDelta=-0.09", result);
        Assert.Contains("not_exchange_acceptance", result);
    }

    [Fact]
    public async Task Audits_at_start_and_after_one_hour_and_throttles_per_pair_and_reason()
    {
        var (checks, _, clock, telegram) = Setup();
        await checks.RefreshAsync(default);
        Assert.Single(telegram.Messages);
        await checks.RefreshAsync(default);
        Assert.Single(telegram.Messages);
        await checks.AlertAsync("DOGE/USD", "BELOW_LOT", default);
        await checks.AlertAsync("DOGE/USD", "BELOW_LOT", default);
        await checks.AlertAsync("DOGE/USD", "SYMBOL_MISMATCH", default);
        Assert.Equal(3, telegram.Messages.Count);
        clock.UtcNow = clock.UtcNow.AddHours(1);
        await checks.RefreshAsync(default);
        Assert.Equal(4, telegram.Messages.Count);
    }

    [Theory]
    [InlineData("PF_WRONGUSD", ".1", "50", "SYMBOL_MISMATCH")]
    [InlineData("PF_DOGEUSD", "100", "1", "BELOW_LOT")]
    [InlineData("PF_DOGEUSD", "0", "1", "INVALID_PRICE_OR_NOTIONAL")]
    public async Task Invalid_parameters_are_reported(string symbol, string price, string notional, string code)
    {
        var (checks, _, _, _) = Setup();
        await checks.RefreshAsync(default);
        var result = await checks.CheckAsync(new() { Pair = "DOGE/USD", KrakenPair = symbol }, FuturesDesiredExposure.Long,
            decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(notional), default);
        Assert.Contains("local_preflight=" + code, result);
    }

    [Fact]
    public async Task Public_tier_is_explicit_and_expired_data_uses_labelled_fallback()
    {
        var (checks, config, clock, _) = Setup(failFeeRefresh: true);
        config.PukoReadOnly.FeeVolumeUsd = 1000m;
        await checks.RefreshAsync(default);
        Assert.Equal(.03m, checks.Fee().Percent); // Immediate FOK/IOC/market => taker.
        Assert.Equal(-.01m, checks.Fee(restedAndFilledLater: true).Percent);
        Assert.Contains("volumeUsd=1000", checks.Fee().Source);
        clock.UtcNow = clock.UtcNow.AddHours(1);
        await checks.RefreshAsync(default);
        Assert.Equal(.03m, checks.Fee().Percent);
        Assert.Contains("kraken_public_assumed", checks.Fee().Source);
        clock.UtcNow = clock.UtcNow.AddMinutes(59).AddSeconds(59);
        Assert.Equal(.03m, checks.Fee().Percent);
        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        Assert.Equal(config.Fees.TakerPct, checks.Fee().Percent);
        Assert.Contains("configured_fallback", checks.Fee().Source);
    }

    [Fact]
    public async Task Network_failure_is_visible_and_does_not_fabricate_rules_or_fees()
    {
        var config = Configuration();
        var telegram = new Notices();
        var checks = new PukoReadOnlyChecks(new HttpClient(new Handler(_ => throw new HttpRequestException())), config, telegram);
        await checks.RefreshAsync(default);
        Assert.Contains(telegram.Messages, x => x.Contains("METADATA_UNAVAILABLE"));
        Assert.Contains(telegram.Messages, x => x.Contains("FEE_SCHEDULE_UNAVAILABLE"));
        Assert.Contains("configured_fallback", checks.Fee().Source);
    }

    [Fact]
    public async Task Portfolio_charges_public_taker_rate_at_entry_and_exit_and_journals_source()
    {
        var (checks, config, _, _) = Setup();
        config.PukoReadOnly.FeeVolumeUsd = 1000m;
        config.Portfolio.StartingCashUsd = 100m;
        config.Futures.MaxLeverage = 10m;
        config.Futures.DefaultLeverage = 10m;
        config.DryRun.SlippageBps = 0m;
        await checks.RefreshAsync(default);
        var portfolio = new FuturesVirtualPortfolio(config, new FileDryRunPortfolioStore(new DryRunOptions
            { OutputDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) }), readOnlyChecks: checks);
        var state = new PortfolioState { CashEur = 100m };
        var entry = portfolio.Apply(state, "DOGE/USD", FuturesDesiredExposure.Long, .1m, 50m, 10m);
        Assert.True(entry.PositionOpened);
        Assert.Equal(.015m, entry.Action.FeeEur);
        Assert.Contains("fee_source=kraken_public_assumed", entry.Action.Reason);
        var exit = portfolio.Apply(state, "DOGE/USD", FuturesDesiredExposure.Flat, .11m, 0m, 10m, reduceOnly: true);
        Assert.True(exit.PositionClosed);
        Assert.Equal(.0165m, exit.Action.FeeEur);
        Assert.Contains("liquidity=taker", exit.Action.Reason);
    }

    [Fact]
    public void Wiring_is_limited_to_paper_puko()
    {
        var config = Configuration();
        Assert.True(PukoReadOnlyChecks.Applies(config));
        config.Futures.LiveTradingEnabled = true;
        Assert.False(PukoReadOnlyChecks.Applies(config));
        config.Futures.LiveTradingEnabled = false;
        config.BotInstance.Id = "futures-live";
        Assert.False(PukoReadOnlyChecks.Applies(config));
    }

    private static FuturesBotConfiguration Configuration() => new()
    {
        BotInstance = new() { Id = "futures-pukis-live" },
        CandidateUniverse = [],
        BykoV2 = new() { Universe = ["DOGE/USD", "BAD/USD"] },
        Entry = new() { MaxEntryPriceDeviationPct = .35m },
        PukoReadOnly = new() { FeeScheduleUid = "test" }
    };

    private static (PukoReadOnlyChecks, FuturesBotConfiguration, TestClock, Notices) Setup(bool failFeeRefresh = false)
    {
        var config = Configuration();
        var clock = new TestClock();
        var telegram = new Notices();
        var feeCalls = 0;
        var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Empty(request.Headers);
            var path = request.RequestUri!.AbsolutePath;
            Assert.True(path is "/derivatives/api/v3/instruments" or "/derivatives/api/v3/feeschedules");
            if (path.EndsWith("feeschedules") && ++feeCalls > 1 && failFeeRefresh)
                return new(HttpStatusCode.ServiceUnavailable);
            return new(HttpStatusCode.OK) { Content = new StringContent(path.EndsWith("instruments") ? Registry : Fees) };
        }));
        return (new(http, config, telegram, clock), config, clock, telegram);
    }

    private sealed class TestClock : IClock { public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero); }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
    private sealed class Notices : ITelegramNotifier
    {
        public List<string> Messages { get; } = [];
        public Task SendAsync(string text, CancellationToken cancellationToken) { Messages.Add(text); return Task.CompletedTask; }
        public Task<bool> TrySendAsync(string text, CancellationToken cancellationToken) { Messages.Add(text); return Task.FromResult(true); }
        public Task SendAlertAsync(string reason, CancellationToken cancellationToken) => throw new InvalidOperationException("Must not use global alert throttle");
    }
}
