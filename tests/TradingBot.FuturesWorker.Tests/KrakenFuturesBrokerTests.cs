using System.Net;
using System.Security.Cryptography;
using System.Text;
using TradingBot.Core.Indicators;
using TradingBot.Core.MarketData;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class KrakenFuturesBrokerTests
{
    [Fact]
    public async Task Send_order_uses_market_order_and_reduce_only_flag()
    {
        var handler = new CapturingHandler("""
            {"result":"success","sendStatus":{"status":"placed","order_id":"order-1"}}
            """);
        using var client = new HttpClient(handler);
        var broker = new KrakenFuturesBroker(client, new KrakenOptions
        {
            BaseUrl = "https://futures.kraken.test",
            ApiKey = "public",
            ApiSecret = Convert.ToBase64String(Encoding.UTF8.GetBytes("secret"))
        });

        var result = await broker.SendOrderAsync("PF_XBTUSD", "sell", 0.01m, reduceOnly: true, leverage: 2m, CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal("order-1", result.OrderId);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://futures.kraken.test/derivatives/api/v3/sendorder", handler.Request.RequestUri!.ToString());
        Assert.Contains("APIKey", handler.Request.Headers.Select(header => header.Key));
        Assert.Contains("Authent", handler.Request.Headers.Select(header => header.Key));
        Assert.Contains("Nonce", handler.Request.Headers.Select(header => header.Key));
        var nonce = Assert.Single(handler.Request.Headers.GetValues("Nonce"));
        var authent = Assert.Single(handler.Request.Headers.GetValues("Authent"));
        Assert.Equal(ExpectedAuthent("/api/v3/sendorder", nonce, handler.Body, "secret"), authent);
        Assert.Contains("orderType=mkt", handler.Body);
        Assert.Contains("symbol=PF_XBTUSD", handler.Body);
        Assert.Contains("side=sell", handler.Body);
        Assert.Contains("size=0.01", handler.Body);
        Assert.Contains("reduceOnly=true", handler.Body);
    }

    [Fact]
    public async Task Set_leverage_preference_puts_symbol_and_max_leverage()
    {
        var handler = new CapturingHandler("""
            {"result":"success"}
            """);
        using var client = new HttpClient(handler);
        var broker = new KrakenFuturesBroker(client, new KrakenOptions
        {
            BaseUrl = "https://futures.kraken.test",
            ApiKey = "public",
            ApiSecret = Convert.ToBase64String(Encoding.UTF8.GetBytes("secret"))
        });

        var ok = await broker.SetLeveragePreferenceAsync("PF_XBTUSD", 2m, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(HttpMethod.Put, handler.Request!.Method);
        Assert.Equal("https://futures.kraken.test/derivatives/api/v3/leveragepreferences", handler.Request.RequestUri!.ToString());
        Assert.Contains("symbol=PF_XBTUSD", handler.Body);
        Assert.Contains("maxLeverage=2", handler.Body);
    }

    [Fact]
    public async Task Send_trigger_order_places_reduce_only_mark_stop()
    {
        var handler = new CapturingHandler("""
            {"result":"success","sendStatus":{"status":"placed","order_id":"sl-1"}}
            """);
        using var client = new HttpClient(handler);
        var broker = new KrakenFuturesBroker(client, new KrakenOptions
        {
            BaseUrl = "https://futures.kraken.test",
            ApiKey = "public",
            ApiSecret = Convert.ToBase64String(Encoding.UTF8.GetBytes("secret"))
        });

        var result = await broker.SendTriggerOrderAsync("PF_BCHUSD", "sell", 1.86m, "stp", 230.05m, "mark", reduceOnly: true, CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal("sl-1", result.OrderId);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://futures.kraken.test/derivatives/api/v3/sendorder", handler.Request.RequestUri!.ToString());
        Assert.Contains("orderType=stp", handler.Body);
        Assert.Contains("symbol=PF_BCHUSD", handler.Body);
        Assert.Contains("side=sell", handler.Body);
        Assert.Contains("size=1.86", handler.Body);
        Assert.Contains("stopPrice=230.05", handler.Body);
        Assert.Contains("triggerSignal=mark", handler.Body);
        Assert.Contains("reduceOnly=true", handler.Body);
    }

    [Fact]
    public async Task Get_open_orders_reads_reduce_only_tpsl_orders()
    {
        var handler = new CapturingHandler("""
            {
              "result": "success",
              "openOrders": [
                {
                  "order_id": "sl-1",
                  "symbol": "PF_BCHUSD",
                  "side": "sell",
                  "orderType": "stp",
                  "unfilledSize": 1.86,
                  "stopPrice": 230.05,
                  "reduceOnly": true
                },
                {
                  "order_id": "tp-1",
                  "symbol": "PF_BCHUSD",
                  "side": "sell",
                  "orderType": "take_profit",
                  "unfilledSize": 1.86,
                  "stopPrice": 244.14,
                  "reduceOnly": true
                }
              ]
            }
            """);
        using var client = new HttpClient(handler);
        var broker = new KrakenFuturesBroker(client, new KrakenOptions
        {
            BaseUrl = "https://futures.kraken.test",
            ApiKey = "public",
            ApiSecret = Convert.ToBase64String(Encoding.UTF8.GetBytes("secret"))
        });

        var orders = await broker.GetOpenOrdersAsync(CancellationToken.None);

        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal("https://futures.kraken.test/derivatives/api/v3/openorders", handler.Request.RequestUri!.ToString());
        Assert.Collection(orders,
            stop =>
            {
                Assert.Equal("sl-1", stop.OrderId);
                Assert.Equal("PF_BCHUSD", stop.Symbol);
                Assert.Equal("sell", stop.Side);
                Assert.Equal("stp", stop.OrderType);
                Assert.Equal(230.05m, stop.StopPrice);
                Assert.True(stop.ReduceOnly);
            },
            takeProfit =>
            {
                Assert.Equal("tp-1", takeProfit.OrderId);
                Assert.Equal("take_profit", takeProfit.OrderType);
                Assert.Equal(244.14m, takeProfit.StopPrice);
                Assert.True(takeProfit.ReduceOnly);
            });
    }

    [Fact]
    public async Task Send_trailing_stop_order_places_reduce_only_percent_trailing_stop()
    {
        var handler = new CapturingHandler("""
            {"result":"success","sendStatus":{"status":"placed","order_id":"trail-1"}}
            """);
        using var client = new HttpClient(handler);
        var broker = new KrakenFuturesBroker(client, new KrakenOptions
        {
            BaseUrl = "https://futures.kraken.test",
            ApiKey = "public",
            ApiSecret = Convert.ToBase64String(Encoding.UTF8.GetBytes("secret"))
        });

        var result = await broker.SendTrailingStopOrderAsync("PF_BCHUSD", "sell", 1.86m, 2m, "mark", reduceOnly: true, CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal("trail-1", result.OrderId);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://futures.kraken.test/derivatives/api/v3/sendorder", handler.Request.RequestUri!.ToString());
        Assert.Contains("orderType=trailing_stop", handler.Body);
        Assert.Contains("symbol=PF_BCHUSD", handler.Body);
        Assert.Contains("side=sell", handler.Body);
        Assert.Contains("size=1.86", handler.Body);
        Assert.Contains("trailingStopMaxDeviation=2", handler.Body);
        Assert.Contains("trailingStopDeviationUnit=PERCENT", handler.Body);
        Assert.Contains("triggerSignal=mark", handler.Body);
        Assert.Contains("reduceOnly=true", handler.Body);
    }

    [Fact]
    public async Task Cancel_order_posts_order_id_and_accepts_cancelled_status()
    {
        var handler = new CapturingHandler("""
            {"result":"success","cancelStatus":{"status":"cancelled","order_id":"sl-1"}}
            """);
        using var client = new HttpClient(handler);
        var broker = new KrakenFuturesBroker(client, new KrakenOptions
        {
            BaseUrl = "https://futures.kraken.test",
            ApiKey = "public",
            ApiSecret = Convert.ToBase64String(Encoding.UTF8.GetBytes("secret"))
        });

        var result = await broker.CancelOrderAsync("sl-1", CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal("sl-1", result.OrderId);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://futures.kraken.test/derivatives/api/v3/cancelorder", handler.Request.RequestUri!.ToString());
        Assert.Contains("order_id=sl-1", handler.Body);
    }

    [Fact]
    public async Task Get_accounts_treats_flex_multi_collateral_account_as_usd()
    {
        var handler = new CapturingHandler("""
            {
              "result": "success",
              "accounts": {
                "flex": {
                  "type": "multiCollateralMarginAccount",
                  "portfolioValue": 72.68,
                  "availableMargin": 72.68
                }
              }
            }
            """);
        using var client = new HttpClient(handler);
        var broker = new KrakenFuturesBroker(client, new KrakenOptions
        {
            BaseUrl = "https://futures.kraken.test",
            ApiKey = "public",
            ApiSecret = Convert.ToBase64String(Encoding.UTF8.GetBytes("secret"))
        });

        var account = Assert.Single(await broker.GetAccountsAsync(CancellationToken.None));

        Assert.Equal("USD", account.Currency);
        Assert.Equal(72.68m, account.MarginBalance);
        Assert.Equal(72.68m, account.AvailableMargin);
    }

    private static string ExpectedAuthent(string endpointPath, string nonce, string postData, string secret)
    {
        var payload = Encoding.UTF8.GetBytes(postData + nonce + endpointPath);
        var sha = SHA256.HashData(payload);
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(hmac.ComputeHash(sha));
    }

    [Fact]
    public async Task Live_worker_fails_closed_when_broker_is_not_configured()
    {
        var config = new FuturesBotConfiguration
        {
            Futures = new FuturesOptions { LiveTradingEnabled = true, DefaultLeverage = 2m, MaxLeverage = 2m },
            Worker = new WorkerOptions { RunOnce = true },
            DryRun = new DryRunOptions { OutputDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) }
        };
        var worker = new FuturesDecisionWorker(
            config,
            new SampleMarketDataSource(),
            new IndicatorEngine(),
            new LongShortStrategy(config),
            new MarginRiskManager(config),
            new FuturesVirtualPortfolio(config, new FileDryRunPortfolioStore(config.DryRun)),
            new TpSlOrchestrator(config));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RunAsync(CancellationToken.None));
        Assert.Contains("LIVE_TRADING_ENABLED=true", ex.Message);
    }

    private sealed class CapturingHandler(string responseBody) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
