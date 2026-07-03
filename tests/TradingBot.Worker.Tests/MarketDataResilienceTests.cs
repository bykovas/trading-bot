using System.Net;
using System.Text;
using TradingBot.Worker;
using Xunit;

namespace TradingBot.Worker.Tests;

public class MarketDataResilienceTests
{
    // AssetPairs (assetVersion=1) uses the canonical property name ("BTC/EUR") which
    // is ALSO the Ticker response key, while wsname keeps the Kraken alias ("XBT/EUR").
    private const string AssetPairsJson = """
        {
          "error": [],
          "result": {
            "BTC/EUR": { "altname": "XBTEUR", "wsname": "XBT/EUR", "status": "online", "ordermin": "0.00005", "costmin": "0.5", "lot_decimals": 8, "pair_decimals": 1 },
            "DOGE/EUR": { "altname": "XDGEUR", "wsname": "XDG/EUR", "status": "online", "ordermin": "30", "costmin": "0.5", "lot_decimals": 8, "pair_decimals": 5 }
          }
        }
        """;

    private const string TickerJson = """
        {
          "error": [],
          "result": {
            "BTC/EUR": { "a": ["60000.1","1","1.0"], "b": ["59999.9","1","1.0"], "c": ["60000.0","0.01"], "v": ["10","20"], "o": "59000.0" },
            "DOGE/EUR": { "a": ["0.101","1","1.0"], "b": ["0.099","1","1.0"], "c": ["0.1","100"], "v": ["1000","2000"], "o": "0.09" }
          }
        }
        """;

    private static readonly InstrumentOptions[] Instruments =
    {
        new() { Pair = "XBT/EUR", KrakenPair = "XBTEUR", Venue = "Kraken", Enabled = true },
        new() { Pair = "XDG/EUR", KrakenPair = "XDGEUR", Venue = "Kraken", Enabled = true }
    };

    [Fact]
    public async Task Ticker_key_matching_resolves_xbt_and_xdg_quotes()
    {
        var handler = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("AssetPairs", StringComparison.Ordinal))
            {
                return Json(AssetPairsJson);
            }

            return url.Contains("Ticker", StringComparison.Ordinal)
                ? Json(TickerJson)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new HttpClient(handler);
        var source = new KrakenMarketDataSource(client, new KrakenOptions { BaseUrl = "https://api.kraken.test" });

        var states = await source.GetLightMarketStatesAsync(Instruments, CancellationToken.None);

        var xbt = states.Single(state => state.Instrument.Pair == "XBT/EUR");
        var xdg = states.Single(state => state.Instrument.Pair == "XDG/EUR");
        Assert.Null(xbt.DataWarning);
        Assert.Equal(60000.0m, xbt.LastPrice);
        Assert.Equal(59999.9m, xbt.BestBid);
        Assert.Equal(60000.1m, xbt.BestAsk);
        Assert.Null(xdg.DataWarning);
        Assert.Equal(0.1m, xdg.LastPrice);
        Assert.Equal(0.099m, xdg.BestBid);
    }

    [Fact]
    public async Task Batch_ticker_failure_degrades_to_warning_states_without_throwing()
    {
        var handler = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("AssetPairs", StringComparison.Ordinal))
            {
                return Json(AssetPairsJson);
            }

            // One transient 5xx on the whole Ticker batch must not crash the cycle.
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("<html>502 Bad Gateway</html>")
            };
        });

        using var client = new HttpClient(handler);
        var source = new KrakenMarketDataSource(client, new KrakenOptions { BaseUrl = "https://api.kraken.test" });

        var states = await source.GetLightMarketStatesAsync(Instruments, CancellationToken.None);

        Assert.Equal(2, states.Count);
        Assert.All(states, state => Assert.Null(state.Quote));
        Assert.All(states, state => Assert.Contains("ticker batch failed", state.DataWarning));
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
