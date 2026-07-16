using System.Globalization;
using System.Net;
using System.Text;

using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class KrakenFuturesMarketDataSourceTests
{
    private static readonly InstrumentOptions[] Instruments =
    {
        new() { Pair = "XBT/EUR", KrakenPair = "PF_XBTEUR", Venue = "KrakenFutures", Enabled = true }
    };

    [Fact]
    public async Task Universe_provider_preserves_contract_value_trade_precision()
    {
        var handler = new StubHandler(request => Json("""
            {
              "result": "success",
              "instruments": [
                { "symbol": "PF_APEUSD", "pair": "APE:USD", "tradeable": true, "postOnly": false, "contractValueTradePrecision": 0 },
                { "symbol": "PF_AAVEUSD", "pair": "AAVE:USD", "tradeable": true, "postOnly": false, "contractValueTradePrecision": 2 }
              ]
            }
            """));
        using var client = new HttpClient(handler);
        var provider = new KrakenFuturesUniverseProvider(
            client,
            new KrakenOptions { BaseUrl = "https://futures.kraken.test" },
            new UniverseDiscoveryOptions { Enabled = true },
            Array.Empty<InstrumentOptions>());

        var universe = await provider.GetUniverseAsync(CancellationToken.None);

        Assert.Equal(0, universe.Instruments.Single(instrument => instrument.Pair == "APE/USD").QuantityDecimals);
        Assert.Equal(2, universe.Instruments.Single(instrument => instrument.Pair == "AAVE/USD").QuantityDecimals);
    }

    [Fact]
    public async Task Light_state_parses_instruments_and_tickers()
    {
        var handler = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/instruments", StringComparison.Ordinal))
            {
                return Json("""
                    {
                      "result": "success",
                      "instruments": [
                        { "symbol": "PF_XBTEUR", "pair": "XBT:EUR", "tickSize": 0.5, "contractSize": 1, "tradeable": true, "postOnly": false, "contractValueTradePrecision": 4 }
                      ]
                    }
                    """);
            }

            if (url.Contains("/tickers", StringComparison.Ordinal))
            {
                return Json("""
                    {
                      "result": "success",
                      "tickers": [
                        { "symbol": "PF_XBTEUR", "markPrice": 60000.0, "bid": 59990.0, "bidSize": 0.5, "ask": 60010.0, "askSize": 0.25, "volumeQuote": 1000000, "change24h": 1.25, "fundingRate": -0.0001, "indexPrice": 60005.0, "last": 60001.0, "suspended": false, "postOnly": false }
                      ],
                      "serverTime": "2026-07-06T12:00:00.000Z"
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new HttpClient(handler);
        var source = new KrakenFuturesMarketDataSource(client, new KrakenOptions { BaseUrl = "https://futures.kraken.test" });

        var states = await source.GetLightMarketStatesAsync(Instruments, CancellationToken.None);

        var state = Assert.Single(states);
        Assert.Null(state.DataWarning);
        Assert.Equal(60000m, state.LastPrice);
        Assert.Equal(59990m, state.BestBid);
        Assert.Equal(60010m, state.BestAsk);
        Assert.Equal(-0.0001m, state.Quote!.FundingRatePercent);
        Assert.Equal(60005m, state.Quote.IndexPrice);
        Assert.Equal(0.5m, state.Quote.BidSize);
        Assert.Equal(0.25m, state.Quote.AskSize);
        Assert.Equal("online", state.PairRules!.Status);
    }

    [Fact]
    public async Task Full_state_parses_closed_mark_candles()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-10);
        var candles = string.Join(",", Enumerable.Range(0, 40).Select(index =>
        {
            var open = (100 + index).ToString(CultureInfo.InvariantCulture);
            var high = (101 + index).ToString(CultureInfo.InvariantCulture);
            var low = (99 + index).ToString(CultureInfo.InvariantCulture);
            var close = (100.5m + index).ToString(CultureInfo.InvariantCulture);
            var volume = (1000 + index).ToString(CultureInfo.InvariantCulture);
            return $$"""
              { "time": {{start.AddMinutes(index * 15).ToUnixTimeMilliseconds()}}, "open": "{{open}}", "high": "{{high}}", "low": "{{low}}", "close": "{{close}}", "volume": "{{volume}}" }
              """;
        }));
        var handler = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/instruments", StringComparison.Ordinal))
            {
                return Json("""{ "result": "success", "instruments": [ { "symbol": "PF_XBTEUR", "tickSize": 0.5, "contractSize": 1, "tradeable": true, "postOnly": false } ] }""");
            }

            if (url.Contains("/api/charts/v1/mark/PF_XBTEUR/15m", StringComparison.Ordinal))
            {
                return Json($$"""{ "candles": [ {{candles}} ], "more_candles": false }""");
            }

            if (url.Contains("/orderbook", StringComparison.Ordinal))
            {
                return Json("""{ "result": "success", "orderBook": { "bids": [[99, 10], [98, 5]], "asks": [[101, 10], [102, 5]] } }""");
            }

            return Json("""{ "result": "success", "tickers": [] }""");
        });

        using var client = new HttpClient(handler);
        var source = new KrakenFuturesMarketDataSource(client, new KrakenOptions { BaseUrl = "https://futures.kraken.test" });

        var states = await source.GetFullMarketStatesAsync(
            Instruments,
            timeframeMinutes: 15,
            new[]
            {
                new InstrumentMarketState
                {
                    Instrument = Instruments[0],
                    Candles = Array.Empty<Candle>(),
                    Quote = new Quote(99m, 101m, 100m, 1000m, MarkPrice: 100m)
                }
            },
            CancellationToken.None);

        var state = Assert.Single(states);
        Assert.True(state.Candles.Count >= 30);
        Assert.Null(state.DataWarning);
        Assert.Equal(100.5m, state.Candles[0].Close);
        Assert.NotNull(state.OrderBook);
        Assert.Equal(101m, state.OrderBook!.Asks[0].Price);
    }

    [Fact]
    public async Task Missing_mark_or_bid_ask_fails_closed_with_warning()
    {
        var handler = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/instruments", StringComparison.Ordinal))
            {
                return Json("""{ "result": "success", "instruments": [ { "symbol": "PF_XBTEUR", "tradeable": true, "postOnly": false } ] }""");
            }

            return Json("""{ "result": "success", "tickers": [ { "symbol": "PF_XBTEUR", "last": 100, "bid": 0, "ask": 0 } ] }""");
        });

        using var client = new HttpClient(handler);
        var source = new KrakenFuturesMarketDataSource(client, new KrakenOptions { BaseUrl = "https://futures.kraken.test" });

        var state = Assert.Single(await source.GetLightMarketStatesAsync(Instruments, CancellationToken.None));

        Assert.Contains("mark price unavailable", state.DataWarning);
        Assert.Contains("bid/ask unavailable", state.DataWarning);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
