using System.Text;
using Xunit;

namespace TradingBot.MarketDataWorker.Tests;

public sealed class KrakenFuturesLiquidationParserTests
{
    [Fact]
    public void Recognizes_subscription_ack_and_failure()
    {
        Assert.True(KrakenFuturesLiquidationParser.IsSubscribed(
            Encoding.UTF8.GetBytes("{\"event\":\"subscribed\",\"feed\":\"trade\"}")));
        Assert.True(KrakenFuturesLiquidationParser.IsSubscriptionError(
            Encoding.UTF8.GetBytes("{\"event\":\"subscribed_failed\",\"message\":\"bad product\"}"),
            out var error));
        Assert.Equal("bad product", error);
    }

    [Fact]
    public void Parses_only_liquidation_trades_and_preserves_forced_side()
    {
        var json = """
        {"feed":"trade_snapshot","trades":[
          {"uid":"a","product_id":"PF_SOLUSD","side":"sell","type":"liquidation","time":1791590400000,"qty":2.5,"price":100},
          {"uid":"b","product_id":"PF_SOLUSD","side":"buy","type":"partialLiquidation","time":1791590401,"qty":"3","price":"101"},
          {"uid":"c","product_id":"PF_SOLUSD","side":"buy","type":"fill","time":1791590402,"qty":4,"price":102}
        ]}
        """;

        var records = KrakenFuturesLiquidationParser.Parse(
            Encoding.UTF8.GetBytes(json),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PF_SOLUSD"] = "SOL/USD" },
            DateTimeOffset.Parse("2026-10-10T00:00:03Z"));

        Assert.Equal(2, records.Count);
        Assert.Equal("sell", records[0].Side);
        Assert.Equal(250m, records[0].ValueUsd);
        Assert.Equal("buy", records[1].Side);
        Assert.Equal(303m, records[1].ValueUsd);
    }
}
