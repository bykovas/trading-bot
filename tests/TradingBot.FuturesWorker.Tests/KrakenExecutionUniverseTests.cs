using System.Net;
using System.Text;
using TradingBot.Core.Common;
using TradingBot.FuturesWorker;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class KrakenExecutionUniverseTests
{
    private const string Registry = """
        {"result":"success","instruments":[
          {"symbol":"PF_XBTUSD","tradeable":true,"tickSize":1,"contractValueTradePrecision":4},
          {"symbol":"PF_ETHUSD","tradeable":true,"tickSize":0.1,"contractValueTradePrecision":3},
          {"symbol":"PF_DOGEUSD","tradeable":true,"tickSize":0.00001,"contractValueTradePrecision":0},
          {"symbol":"PF_ZECUSD","tradeable":true,"tickSize":0.01,"contractValueTradePrecision":2},
          {"symbol":"PF_BADUSD","tradeable":true,"suspended":true,"tickSize":1,"contractValueTradePrecision":0}
        ]}
        """;

    [Fact]
    public async Task DiscoversMissingDogeAndUsesAuthoritativePrecisionInsteadOfConfiguredGuess()
    {
        var calls = 0;
        var provider = Provider(_ => { calls++; return new(HttpStatusCode.OK) { Content = new StringContent(Registry) }; });
        var selection = await provider.GetUniverseAsync(default);
        var doge = FuturesDecisionWorker.ResolveDebykoInstrument("DOGE-PERP", selection.Instruments);
        Assert.Equal("PF_DOGEUSD", doge!.KrakenPair);
        var btc = FuturesDecisionWorker.ResolveDebykoInstrument("BTC-PERP", selection.Instruments);
        Assert.Equal(4, btc!.QuantityDecimals);
        Assert.Equal(0, btc.PriceDecimals);
        Assert.DoesNotContain(selection.Instruments, x => x.Pair == "BAD/USD");
        await provider.GetUniverseAsync(default);
        Assert.Equal(1, calls);
        provider.Invalidate();
        Assert.False(provider.TryGetRules("PF_XBTUSD", out _));
        await provider.GetUniverseAsync(default);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RegistryFailureDoesNotInventTradableMetadata()
    {
        var provider = Provider(_ => new(HttpStatusCode.ServiceUnavailable));
        var selection = await provider.GetUniverseAsync(default);
        Assert.Empty(selection.Instruments);
        Assert.False(provider.HasFreshMetadata);
        Assert.False(provider.TryGetRules("PF_XBTUSD", out _));
    }

    [Theory]
    [InlineData("1", "0.0001", "81202.19263054", "0.00061359", "81203", "81202", "0.0006")]
    [InlineData("0.1", "0.001", "2405.08608843", "0.02071422", "2405.1", "2405.0", "0.020")]
    [InlineData("0.01", "0.01", "1129.23323118", "0.04418873", "1129.24", "1129.23", "0.04")]
    [InlineData("0.5", "1", "100.23", "49.8", "100.5", "100.0", "49")]
    public void OrdersStayOnTheExactGridAndNeverIncreaseQuantity(string tick, string step, string rawPrice,
        string rawQuantity, string shortPrice, string longPrice, string quantity)
    {
        static decimal D(string x) => decimal.Parse(x, System.Globalization.CultureInfo.InvariantCulture);
        var rules = new KrakenContractRules("PF_TESTUSD", D(tick), D(step));
        Assert.Equal(D(shortPrice), rules.RoundPrice(D(rawPrice), true));
        Assert.Equal(D(longPrice), rules.RoundPrice(D(rawPrice), false));
        Assert.Equal(D(quantity), rules.RoundQuantity(D(rawQuantity)));
        Assert.Equal(0m, rules.RoundQuantity(D(step) / 2m));
    }

    [Theory]
    [InlineData("live futures entry rejected: invalidSize", true)]
    [InlineData("live futures entry rejected: invalidPrice", true)]
    [InlineData("live futures entry rejected: unavailable", false)]
    [InlineData("entry ATR snapshot unavailable", false)]
    public void PermanentExchangeErrorsAreNotRetriedUnchanged(string reason, bool permanent) =>
        Assert.Equal(permanent, FuturesDecisionWorker.IsPermanentEntryRejection(reason));

    private static KrakenExecutionUniverseProvider Provider(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new Handler(respond)), new KrakenOptions { BaseUrl = "https://metadata.test" },
            [new InstrumentOptions { Pair = "XBT/USD", QuantityDecimals = 8, PriceDecimals = 8 }],
            ["BTC-PERP", "ETH-PERP", "DOGE-PERP", "ZEC-PERP", "BAD-PERP"]);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/derivatives/api/v3/instruments", request.RequestUri!.AbsolutePath);
            return Task.FromResult(respond(request));
        }
    }
}
