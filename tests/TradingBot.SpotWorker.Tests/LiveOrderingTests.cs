using System.Net;
using System.Text;
using System.Text.Json;
using TradingBot.SpotWorker;
using Xunit;

namespace TradingBot.SpotWorker.Tests;

// Live execution ordering: the exchange order goes out FIRST and the virtual
// portfolio is committed only after the exchange accepted it. A failed or rejected
// live order must leave the portfolio untouched (no phantom position after a failed
// BUY, position kept after a failed SELL).
public class LiveOrderingTests
{
    [Fact]
    public async Task Failed_live_buy_leaves_the_virtual_portfolio_unchanged()
    {
        var outputDirectory = TempDir();
        var config = LiveConfig(outputDirectory);
        var worker = Worker(config, RejectingBrokerHandler());

        await worker.RunAsync(CancellationToken.None);

        var cycle = LastCycle(outputDirectory);
        var decision = cycle.GetProperty("decisions").EnumerateArray()
            .Single(d => d.GetProperty("pair").GetString() == "AAA/EUR");
        var action = decision.GetProperty("dryRunAction");

        Assert.Equal("LIVE_ORDER_FAILED", action.GetProperty("action").GetString());
        Assert.Contains("Insufficient funds", action.GetProperty("reason").GetString());

        // No phantom position, no phantom cash movement.
        var after = cycle.GetProperty("portfolioAfter");
        Assert.Empty(after.GetProperty("positions").EnumerateArray());
        Assert.Equal(
            cycle.GetProperty("portfolioBefore").GetProperty("cashEur").GetDecimal(),
            after.GetProperty("cashEur").GetDecimal());
    }

    [Fact]
    public async Task Accepted_live_buy_commits_the_virtual_fill()
    {
        var outputDirectory = TempDir();
        var config = LiveConfig(outputDirectory);
        var worker = Worker(config, AcceptingBrokerHandler());

        await worker.RunAsync(CancellationToken.None);

        var cycle = LastCycle(outputDirectory);
        var decision = cycle.GetProperty("decisions").EnumerateArray()
            .Single(d => d.GetProperty("pair").GetString() == "AAA/EUR");

        Assert.Equal("WOULD_BUY", decision.GetProperty("dryRunAction").GetProperty("action").GetString());
        Assert.Contains("LIVE_SUBMITTED", decision.GetProperty("broker").GetString());

        var positions = LastCycle(outputDirectory).GetProperty("portfolioAfter").GetProperty("positions").EnumerateArray().ToList();
        var position = Assert.Single(positions);
        Assert.Equal("AAA/EUR", position.GetProperty("pair").GetString());
    }

    [Fact]
    public async Task Accepted_live_buy_commits_the_exchange_confirmed_fill()
    {
        var outputDirectory = TempDir();
        var config = LiveConfig(outputDirectory);
        // AddOrder accepts; QueryOrders reports a REAL fill that differs from the
        // modeled ask+slippage fill (real price 1.41 vs modeled 1.40, real fee 0.03).
        var worker = Worker(config, AcceptingBrokerWithFillHandler());

        await worker.RunAsync(CancellationToken.None);

        var cycle = LastCycle(outputDirectory);
        var decision = cycle.GetProperty("decisions").EnumerateArray()
            .Single(d => d.GetProperty("pair").GetString() == "AAA/EUR");
        var action = decision.GetProperty("dryRunAction");

        Assert.Equal("WOULD_BUY", action.GetProperty("action").GetString());
        Assert.Equal("REAL", action.GetProperty("fillSource").GetString());
        Assert.Equal(1.41m, action.GetProperty("fillPrice").GetDecimal());
        Assert.Equal(0.03m, action.GetProperty("feeEur").GetDecimal());
        // Modeled numbers stay on the record for drift analysis.
        Assert.True(action.GetProperty("modeledFillPrice").GetDecimal() > 0m);

        var position = Assert.Single(
            cycle.GetProperty("portfolioAfter").GetProperty("positions").EnumerateArray().ToList());
        Assert.Equal(1.41m, position.GetProperty("entryPrice").GetDecimal());
        Assert.Equal(7.09m, position.GetProperty("quantity").GetDecimal());
        // Cash reduced by the REAL cost + fee (9.9969 + 0.03), not the modeled 10.
        var cashAfter = cycle.GetProperty("portfolioAfter").GetProperty("cashEur").GetDecimal();
        var cashBefore = cycle.GetProperty("portfolioBefore").GetProperty("cashEur").GetDecimal();
        Assert.Equal(10.0269m, cashBefore - cashAfter);
    }

    [Fact]
    public async Task Live_buy_does_not_create_position_when_maker_fill_is_unconfirmed()
    {
        var outputDirectory = TempDir();
        var config = LiveConfig(outputDirectory);
        var worker = Worker(config, AcceptingBrokerWithFailingQueryHandler());

        await worker.RunAsync(CancellationToken.None);

        var cycle = LastCycle(outputDirectory);
        var decision = cycle.GetProperty("decisions").EnumerateArray()
            .Single(d => d.GetProperty("pair").GetString() == "AAA/EUR");
        var action = decision.GetProperty("dryRunAction");

        Assert.Equal("LIVE_ORDER_FAILED", action.GetProperty("action").GetString());
        Assert.Contains("not executed", action.GetProperty("reason").GetString());
        Assert.Empty(cycle.GetProperty("portfolioAfter").GetProperty("positions").EnumerateArray().ToList());
    }

    // -------------------------------------------------- maker-then-IOC fallback

    [Fact]
    public async Task Maker_full_fill_does_not_trigger_the_ioc_fallback()
    {
        var outputDirectory = TempDir();
        var config = LiveConfig(outputDirectory);
        var iocSubmissions = new Counter();
        var worker = Worker(config, new BodyAwareStub((path, body) =>
        {
            CountIoc(path, body, iocSubmissions);
            if (IsMakerAdd(path, body)) return AddResponse("MAKER-1");
            if (path.Contains("QueryOrders")) return ClosedFill("MAKER-1", vol: "7.13", price: "1.40", cost: "9.982", fee: "0.026");
            return DefaultResponse(path);
        }));

        await worker.RunAsync(CancellationToken.None);

        var action = ActionFor(outputDirectory, "AAA/EUR");
        Assert.Equal("WOULD_BUY", action.GetProperty("action").GetString());
        Assert.Equal("MAKER", action.GetProperty("entryExecution").GetProperty("fillSource").GetString());
        Assert.Equal(0, iocSubmissions.Value);
    }

    [Fact]
    public async Task Maker_partial_fill_commits_partial_and_does_not_trigger_fallback()
    {
        var outputDirectory = TempDir();
        var config = LiveConfig(outputDirectory);
        var iocSubmissions = new Counter();
        var worker = Worker(config, new BodyAwareStub((path, body) =>
        {
            CountIoc(path, body, iocSubmissions);
            if (IsMakerAdd(path, body)) return AddResponse("MAKER-1");
            if (path.Contains("CancelOrder")) return CancelOk();
            // Reports an open order that has partially executed: the remainder is
            // canceled and the partial fill is committed, no IOC for the remainder.
            if (path.Contains("QueryOrders")) return Order("MAKER-1", "open", vol: "3.0", price: "1.395", cost: "4.185", fee: "0.011");
            return DefaultResponse(path);
        }));

        await worker.RunAsync(CancellationToken.None);

        var action = ActionFor(outputDirectory, "AAA/EUR");
        Assert.Equal("WOULD_BUY", action.GetProperty("action").GetString());
        Assert.Equal("MAKER_PARTIAL", action.GetProperty("entryExecution").GetProperty("fillSource").GetString());
        Assert.Equal(0, iocSubmissions.Value);

        var position = Assert.Single(PositionsAfter(outputDirectory));
        Assert.Equal(3.0m, position.GetProperty("quantity").GetDecimal());
    }

    [Fact]
    public async Task Maker_miss_falls_back_to_a_filled_ioc_taker()
    {
        var outputDirectory = TempDir();
        var config = LiveConfig(outputDirectory);
        config.Entry.MaxBuySlippagePercent = 1.0m; // let ask 1.40 pass the cap off bid 1.395
        var iocSubmissions = new Counter();
        var worker = Worker(config, new BodyAwareStub((path, body) =>
        {
            CountIoc(path, body, iocSubmissions);
            if (IsMakerAdd(path, body)) return AddResponse("MAKER-1");
            if (IsIocAdd(path, body)) return AddResponse("IOC-1");
            if (path.Contains("CancelOrder")) return CancelOk();
            if (path.Contains("QueryOrders") && body.Contains("MAKER-1")) return Order("MAKER-1", "canceled", vol: "0", price: "0", cost: "0", fee: "0");
            if (path.Contains("QueryOrders") && body.Contains("IOC-1")) return ClosedFill("IOC-1", vol: "7.13", price: "1.40", cost: "9.982", fee: "0.03");
            return DefaultResponse(path);
        }));

        await worker.RunAsync(CancellationToken.None);

        var action = ActionFor(outputDirectory, "AAA/EUR");
        var entry = action.GetProperty("entryExecution");
        Assert.Equal("WOULD_BUY", action.GetProperty("action").GetString());
        Assert.Equal("REAL", action.GetProperty("fillSource").GetString());
        Assert.Equal("IOC_FALLBACK", entry.GetProperty("fillSource").GetString());
        Assert.True(entry.GetProperty("fallbackAttempted").GetBoolean());
        Assert.Equal(1, iocSubmissions.Value);

        var position = Assert.Single(PositionsAfter(outputDirectory));
        Assert.Equal(1.40m, position.GetProperty("entryPrice").GetDecimal());
    }

    [Fact]
    public async Task Ioc_fallback_rejected_by_exchange_leaves_portfolio_unchanged()
    {
        var outputDirectory = TempDir();
        var config = LiveConfig(outputDirectory);
        config.Entry.MaxBuySlippagePercent = 1.0m;
        var worker = Worker(config, new BodyAwareStub((path, body) =>
        {
            if (IsIocAdd(path, body)) return """{"error":["EOrder:Insufficient funds"]}""";
            if (IsMakerAdd(path, body)) return AddResponse("MAKER-1");
            if (path.Contains("CancelOrder")) return CancelOk();
            if (path.Contains("QueryOrders")) return Order("MAKER-1", "canceled", vol: "0", price: "0", cost: "0", fee: "0");
            return DefaultResponse(path);
        }));

        await worker.RunAsync(CancellationToken.None);

        var action = ActionFor(outputDirectory, "AAA/EUR");
        Assert.Equal("LIVE_ORDER_FAILED", action.GetProperty("action").GetString());
        Assert.Equal("NONE", action.GetProperty("entryExecution").GetProperty("fillSource").GetString());
        Assert.Empty(PositionsAfter(outputDirectory));
    }

    [Fact]
    public async Task Ioc_fallback_accepted_with_zero_fill_leaves_portfolio_unchanged()
    {
        var outputDirectory = TempDir();
        var config = LiveConfig(outputDirectory);
        config.Entry.MaxBuySlippagePercent = 1.0m;
        var worker = Worker(config, new BodyAwareStub((path, body) =>
        {
            if (IsMakerAdd(path, body)) return AddResponse("MAKER-1");
            if (IsIocAdd(path, body)) return AddResponse("IOC-1");
            if (path.Contains("CancelOrder")) return CancelOk();
            if (path.Contains("QueryOrders") && body.Contains("IOC-1")) return Order("IOC-1", "canceled", vol: "0", price: "0", cost: "0", fee: "0");
            if (path.Contains("QueryOrders")) return Order("MAKER-1", "canceled", vol: "0", price: "0", cost: "0", fee: "0");
            return DefaultResponse(path);
        }));

        await worker.RunAsync(CancellationToken.None);

        var action = ActionFor(outputDirectory, "AAA/EUR");
        Assert.Equal("LIVE_ORDER_FAILED", action.GetProperty("action").GetString());
        Assert.Empty(PositionsAfter(outputDirectory));
    }

    [Fact]
    public async Task Late_maker_fill_after_cancel_suppresses_the_ioc_fallback()
    {
        var outputDirectory = TempDir();
        var config = LiveConfig(outputDirectory);
        config.Entry.MaxBuySlippagePercent = 1.0m;
        var iocSubmissions = new Counter();
        var canceled = new Counter();
        var postCancelQueries = new Counter();
        var worker = Worker(config, new BodyAwareStub((path, body) =>
        {
            CountIoc(path, body, iocSubmissions);
            if (IsMakerAdd(path, body)) return AddResponse("MAKER-1");
            if (IsIocAdd(path, body)) return AddResponse("IOC-1");
            if (path.Contains("CancelOrder"))
            {
                canceled.Increment();
                return CancelOk();
            }
            if (path.Contains("QueryOrders") && body.Contains("MAKER-1"))
            {
                // Open throughout the maker phase; the post-window reconcile (first
                // query after cancel) still shows zero; the late fill only lands by the
                // time the FALLBACK re-reads the final state (second query after cancel).
                if (canceled.Value == 0)
                {
                    return Order("MAKER-1", "open", vol: "0", price: "0", cost: "0", fee: "0");
                }

                return postCancelQueries.Increment() == 1
                    ? Order("MAKER-1", "open", vol: "0", price: "0", cost: "0", fee: "0")
                    : ClosedFill("MAKER-1", vol: "7.10", price: "1.395", cost: "9.9045", fee: "0.02");
            }
            return DefaultResponse(path);
        }));

        await worker.RunAsync(CancellationToken.None);

        var decision = DecisionFor(outputDirectory, "AAA/EUR");
        var action = decision.GetProperty("dryRunAction");
        Assert.Equal("WOULD_BUY", action.GetProperty("action").GetString());
        Assert.Equal("MAKER_PARTIAL", action.GetProperty("entryExecution").GetProperty("fillSource").GetString());
        Assert.Contains("LATE_MAKER_FILL", decision.GetProperty("broker").GetString() ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(0, iocSubmissions.Value);
        Assert.Single(PositionsAfter(outputDirectory));
    }

    [Fact]
    public async Task Unknown_maker_state_after_cancel_suppresses_the_ioc_fallback()
    {
        var outputDirectory = TempDir();
        var config = LiveConfig(outputDirectory);
        config.Entry.MaxBuySlippagePercent = 1.0m;
        var iocSubmissions = new Counter();
        var worker = Worker(config, new BodyAwareStub((path, body) =>
        {
            CountIoc(path, body, iocSubmissions);
            if (IsMakerAdd(path, body)) return AddResponse("MAKER-1");
            if (IsIocAdd(path, body)) return AddResponse("IOC-1");
            if (path.Contains("CancelOrder")) return CancelOk();
            // The maker order never reaches a final state: the IOC must be suppressed
            // rather than risk a double position.
            if (path.Contains("QueryOrders")) return Order("MAKER-1", "open", vol: "0", price: "0", cost: "0", fee: "0");
            return DefaultResponse(path);
        }));

        await worker.RunAsync(CancellationToken.None);

        var action = ActionFor(outputDirectory, "AAA/EUR");
        Assert.Equal("LIVE_ORDER_FAILED", action.GetProperty("action").GetString());
        Assert.Contains("UNKNOWN_MAKER_STATE", action.GetProperty("reason").GetString() ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(0, iocSubmissions.Value);
        Assert.Empty(PositionsAfter(outputDirectory));
    }

    [Fact]
    public async Task Ioc_fallback_rejected_when_ask_exceeds_slippage_cap()
    {
        var outputDirectory = TempDir();
        var config = LiveConfig(outputDirectory);
        config.Entry.MaxBuySlippagePercent = 0.10m; // ask 1.40 vs bid 1.395 is ~0.36% > cap
        var iocSubmissions = new Counter();
        var worker = Worker(config, new BodyAwareStub((path, body) =>
        {
            CountIoc(path, body, iocSubmissions);
            if (IsMakerAdd(path, body)) return AddResponse("MAKER-1");
            if (IsIocAdd(path, body)) return AddResponse("IOC-1");
            if (path.Contains("CancelOrder")) return CancelOk();
            if (path.Contains("QueryOrders")) return Order("MAKER-1", "canceled", vol: "0", price: "0", cost: "0", fee: "0");
            return DefaultResponse(path);
        }));

        await worker.RunAsync(CancellationToken.None);

        var action = ActionFor(outputDirectory, "AAA/EUR");
        Assert.Equal("LIVE_ORDER_FAILED", action.GetProperty("action").GetString());
        Assert.Contains("REJECTED_SLIPPAGE", action.GetProperty("reason").GetString() ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(0, iocSubmissions.Value);
        Assert.Empty(PositionsAfter(outputDirectory));
    }

    [Fact]
    public async Task Sell_execution_path_is_unchanged_by_the_buy_fallback()
    {
        // A held position that flips bearish must still exit via a plain market SELL:
        // the maker-then-IOC BUY path must never intercept a SELL.
        var outputDirectory = TempDir();
        var config = LiveConfig(outputDirectory);
        config.Portfolio = new PortfolioOptions
        {
            StartingCashEur = 100m,
            Positions =
            {
                new PositionOptions { Pair = "AAA/EUR", Side = "LONG", Quantity = 5m, EntryPrice = 5m, EntryNotionalEur = 25m }
            }
        };
        // The injected position is deeply underwater vs the sample last price, so the
        // Tier-1 fixed stop-loss forces a hard market SELL exit this cycle.
        config.ExecutionPolicy.MinHoldSeconds = 0;
        config.PositionExit = new PositionExitOptions { StopLossPercent = 1m, TakeProfitPercent = 0m, MaxHoldMinutes = 0, MinProfitToExitOnSignalFlipPercent = 0m };

        var makerAdds = new Counter();
        var worker = Worker(config, new BodyAwareStub((path, body) =>
        {
            if (IsMakerAdd(path, body)) makerAdds.Increment();
            if (path.Contains("AddOrder")) return """{"error":[],"result":{"txid":["SELL-1"],"descr":{"order":"sell 5 AAAEUR @ market"}}}""";
            if (path.Contains("QueryOrders")) return ClosedFill("SELL-1", vol: "5", price: "10.0", cost: "50.0", fee: "0.13");
            return DefaultResponse(path);
        }));

        await worker.RunAsync(CancellationToken.None);

        var action = ActionFor(outputDirectory, "AAA/EUR");
        Assert.Equal("WOULD_SELL", action.GetProperty("action").GetString());
        // The SELL never goes through the post-only maker path.
        Assert.Equal(0, makerAdds.Value);
    }

    // ---------------------------------------------- fallback test plumbing

    private sealed class Counter
    {
        public int Value;
        public int Increment() => ++Value;
    }

    private static void CountIoc(string path, string body, Counter counter)
    {
        if (IsIocAdd(path, body))
        {
            counter.Increment();
        }
    }

    private static bool IsMakerAdd(string path, string body) =>
        path.Contains("AddOrder", StringComparison.Ordinal) && body.Contains("oflags=post", StringComparison.Ordinal);

    private static bool IsIocAdd(string path, string body) =>
        path.Contains("AddOrder", StringComparison.Ordinal) && body.Contains("timeinforce=IOC", StringComparison.Ordinal);

    private static string AddResponse(string txid) =>
        "{\"error\":[],\"result\":{\"txid\":[\"" + txid + "\"],\"descr\":{\"order\":\"order " + txid + "\"}}}";

    private static string CancelOk() => """{"error":[],"result":{"count":1}}""";

    private static string DefaultResponse(string path) =>
        path.Contains("Balance", StringComparison.Ordinal)
            ? """{"error":[],"result":{"ZEUR":"100.0"}}"""
            : """{"error":[],"result":{}}""";

    private static string Order(string txid, string status, string vol, string price, string cost, string fee) =>
        "{\"error\":[],\"result\":{\"" + txid + "\":{\"status\":\"" + status +
        "\",\"vol_exec\":\"" + vol + "\",\"price\":\"" + price +
        "\",\"cost\":\"" + cost + "\",\"fee\":\"" + fee + "\"}}}";

    private static string ClosedFill(string txid, string vol, string price, string cost, string fee) =>
        Order(txid, "closed", vol, price, cost, fee);

    private static JsonElement DecisionFor(string outputDirectory, string pair) =>
        LastCycle(outputDirectory).GetProperty("decisions").EnumerateArray()
            .Single(d => d.GetProperty("pair").GetString() == pair);

    private static JsonElement ActionFor(string outputDirectory, string pair) =>
        DecisionFor(outputDirectory, pair).GetProperty("dryRunAction");

    private static List<JsonElement> PositionsAfter(string outputDirectory) =>
        LastCycle(outputDirectory).GetProperty("portfolioAfter").GetProperty("positions").EnumerateArray().ToList();

    private sealed class BodyAwareStub(Func<string, string, string> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request.RequestUri!.AbsolutePath, body), Encoding.UTF8, "application/json")
            };
        }
    }

    // ------------------------------------------------------------------ plumbing

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "trading-bot-tests", Guid.NewGuid().ToString("N"));

    private static JsonElement LastCycle(string outputDirectory)
    {
        var line = File.ReadLines(Path.Combine(outputDirectory, "events.jsonl")).Last();
        return JsonDocument.Parse(line).RootElement.Clone();
    }

    private static DecisionWorker Worker(BotConfiguration config, HttpMessageHandler brokerHandler) => new(
        config,
        new FakeMarketDataSource("AAA/EUR"),
        new FixedAdvisor("AAA/EUR"),
        new IndicatorEngine(),
        new TechnicalDecisionEngine(),
        new RiskManager(),
        new DryRunPortfolio(config.DryRun, config.Portfolio, config.ExecutionPolicy, config.PositionExit, config.PositionSizing, strategy: config.Strategy),
        broker: new KrakenBroker(new HttpClient(brokerHandler), config.Kraken));

    // Live-enabled config whose synthetic series produces a firm buy (the fake data
    // source scores 0.35 with MinimumLongScore lowered to match). NOTE: this config
    // deliberately skips Normalize() so the price-action warm-up (forced on in real
    // live runs) does not block the single-cycle entry under test.
    private static BotConfiguration LiveConfig(string outputDirectory) => new()
    {
        Worker = new WorkerOptions { RunOnce = true },
        Kraken = new KrakenOptions
        {
            MarketDataMode = "sample",
            BaseUrl = "https://kraken.test.invalid",
            ApiKey = "test-key",
            ApiSecret = Convert.ToBase64String(Encoding.UTF8.GetBytes("test-secret-material"))
        },
        Ai = new AiOptions { Provider = "none", MaxRecommendations = 20, WatchlistRefreshSeconds = 0 },
        Trading = new TradingOptions { LiveTradingEnabled = true, MaxActiveInstruments = 20, TargetOrderEur = 10m },
        Risk = new RiskOptions { KillSwitch = false, MaxOrderEur = 10m, MaxOpenPositions = 5, MaxDailyLossEur = 5m, MaxTotalExposureEur = 50m },
        Strategy = new StrategyOptions
        {
            FastEmaPeriod = 3,
            SlowEmaPeriod = 5,
            RsiPeriod = 3,
            MinimumEmaGapPercent = 0m,
            MinimumLongScore = 0.35m,
            RequirePriceActionData = false
        },
        PositionSizing = new PositionSizingOptions { Enabled = false },
        DryRun = new DryRunOptions
        {
            Enabled = true,
            ApplyVirtualFills = true,
            OutputDirectory = outputDirectory,
            StateFile = "portfolio-state.json",
            EventsFile = "events.jsonl"
        },
        ExecutionPolicy = new ExecutionPolicyOptions
        {
            MaxNewPositionsPerCycle = 1,
            AllowImmediateExitOnSignalFlip = true,
            EntryBlackoutMinutes = 0,
            MaxNewPositionsPerHour = 0
        },
        Entry = new EntryOptions { MakerFillTimeoutSec = 1, MakerRepegs = 0 },
        PositionExit = new PositionExitOptions { MinProfitToExitOnSignalFlipPercent = 0m, StopLossPercent = 0m, TakeProfitPercent = 0m, MaxHoldMinutes = 0 },
        CandidateUniverse = new List<InstrumentOptions>
        {
            new() { Pair = "AAA/EUR", KrakenPair = "AAAEUR", Venue = "Kraken", Enabled = true }
        }
    };

    // Kraken private-API stub: Balance always succeeds; AddOrder rejects.
    private static HttpMessageHandler RejectingBrokerHandler() => new StubHandler(path =>
        path.Contains("AddOrder", StringComparison.Ordinal)
            ? """{"error":["EOrder:Insufficient funds"]}"""
            : """{"error":[],"result":{"ZEUR":"100.0"}}""");

    // Kraken private-API stub: AddOrder accepts with a txid.
    private static HttpMessageHandler AcceptingBrokerHandler() => new StubHandler(path =>
        path.Contains("AddOrder", StringComparison.Ordinal)
            ? """{"error":[],"result":{"txid":["TX-TEST-1"],"descr":{"order":"buy 7.14 AAAEUR @ market"}}}"""
            : path.Contains("QueryOrders", StringComparison.Ordinal)
                ? """{"error":[],"result":{"TX-TEST-1":{"status":"closed","vol_exec":"7.13","price":"1.40","cost":"9.982","fee":"0.026"}}}"""
                : """{"error":[],"result":{"ZEUR":"100.0"}}""");

    // AddOrder accepts; QueryOrders reports a closed order with a REAL fill that
    // deliberately differs from the modeled ask+slippage fill.
    private static HttpMessageHandler AcceptingBrokerWithFillHandler() => new StubHandler(path =>
        path.Contains("AddOrder", StringComparison.Ordinal)
            ? """{"error":[],"result":{"txid":["TX-TEST-2"],"descr":{"order":"buy 7.14 AAAEUR @ market"}}}"""
            : path.Contains("QueryOrders", StringComparison.Ordinal)
                ? """{"error":[],"result":{"TX-TEST-2":{"status":"closed","vol_exec":"7.09","price":"1.41","cost":"9.9969","fee":"0.03"}}}"""
                : """{"error":[],"result":{"ZEUR":"100.0"}}""");

    // AddOrder accepts; QueryOrders always errors, forcing the modeled fallback.
    private static HttpMessageHandler AcceptingBrokerWithFailingQueryHandler() => new StubHandler(path =>
        path.Contains("AddOrder", StringComparison.Ordinal)
            ? """{"error":[],"result":{"txid":["TX-TEST-3"],"descr":{"order":"buy 7.14 AAAEUR @ market"}}}"""
            : path.Contains("QueryOrders", StringComparison.Ordinal)
                ? """{"error":["EQuery:Unknown order"]}"""
                : """{"error":[],"result":{"ZEUR":"100.0"}}""");

    private sealed class StubHandler(Func<string, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request.RequestUri!.AbsolutePath), Encoding.UTF8, "application/json")
            });
    }

    private sealed class FixedAdvisor(params string[] pairs) : IWatchlistAdvisor
    {
        public Task<WatchlistAdvice> SelectAsync(
            IReadOnlyList<InstrumentMarketState> candidates,
            int maxRecommendations,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new WatchlistAdvice(
                "fixed",
                pairs.Take(maxRecommendations)
                    .Select((pair, index) => new WatchlistRecommendation(pair, index + 1, "fixed pick"))
                    .ToList(),
                Array.Empty<string>()));
        }
    }

    private sealed class FakeMarketDataSource(params string[] pairs) : IMarketDataSource
    {
        private readonly HashSet<string> _pairs = pairs.ToHashSet(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<InstrumentMarketState>> GetLightMarketStatesAsync(
            IReadOnlyList<InstrumentOptions> instruments,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<InstrumentMarketState>>(
                instruments.Select(instrument => State(instrument, includeCandles: false)).ToList());
        }

        public Task<IReadOnlyList<InstrumentMarketState>> GetFullMarketStatesAsync(
            IReadOnlyList<InstrumentOptions> instruments,
            int timeframeMinutes,
            IReadOnlyList<InstrumentMarketState> lightStates,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<InstrumentMarketState>>(
                instruments.Select(instrument => State(instrument, _pairs.Contains(instrument.Pair))).ToList());
        }

        private static InstrumentMarketState State(InstrumentOptions instrument, bool includeCandles)
        {
            var candles = includeCandles
                ? Enumerable.Range(0, 40)
                    .Select(index => new Candle(
                        DateTimeOffset.UtcNow.AddMinutes(-40 + index),
                        Open: 1m + index * 0.01m,
                        High: 1.02m + index * 0.01m,
                        Low: 0.99m + index * 0.01m,
                        Close: 1m + index * 0.01m,
                        Volume: 100m + index,
                        TradeCount: 10))
                    .ToArray()
                : Array.Empty<Candle>();

            return new InstrumentMarketState
            {
                Instrument = instrument,
                Candles = candles,
                Quote = new Quote(1.395m, 1.40m, 1.39m, 100m, 0m),
                PairRules = new PairRules(instrument.Pair, "online", 0.001m, 0.5m, 8, 2)
            };
        }
    }
}
