using TradingBot.SpotWorker;
using Xunit;

namespace TradingBot.SpotWorker.Tests;

// Spot live reconciliation helpers: real cost basis and real fills recovered from
// Kraken trade history, so the dashboard/DB stops diverging from the exchange.
public sealed class SpotKrakenReconciliationTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-07-16T10:00:00Z");

    private static SpotTradeHistoryEntry Trade(string ordertxid, string pair, string type, decimal price, decimal vol, decimal cost, decimal fee, int minute) =>
        new(ordertxid, pair, type, price, vol, cost, fee, T.AddMinutes(minute));

    [Fact]
    public void Average_cost_basis_uses_buys_and_fees()
    {
        var trades = new[]
        {
            Trade("O1", "SOLEUR", "buy", 100m, 10m, 1000m, 2m, 0),
            Trade("O2", "SOLEUR", "buy", 120m, 10m, 1200m, 2m, 5)
        };

        // (1000 + 2 + 1200 + 2) / 20 = 110.2
        var basis = DecisionWorker.AverageCostBasisPrice("SOL/EUR", trades);
        Assert.Equal(110.2m, decimal.Round(basis, 4));
    }

    [Fact]
    public void Average_cost_basis_reduces_pro_rata_on_sells()
    {
        var trades = new[]
        {
            Trade("O1", "SOLEUR", "buy", 100m, 10m, 1000m, 2m, 0),
            Trade("O2", "SOLEUR", "buy", 120m, 10m, 1200m, 2m, 5),
            Trade("O3", "SOLEUR", "sell", 130m, 10m, 1300m, 2m, 10)
        };

        // Average-cost method: the sell removes half the basis, leaving the same 110.2.
        var basis = DecisionWorker.AverageCostBasisPrice("SOL/EUR", trades);
        Assert.Equal(110.2m, decimal.Round(basis, 4));
    }

    [Fact]
    public void Average_cost_basis_is_zero_without_matching_history()
    {
        var trades = new[] { Trade("O1", "ADAEUR", "buy", 1m, 10m, 10m, 0.02m, 0) };
        Assert.Equal(0m, DecisionWorker.AverageCostBasisPrice("SOL/EUR", trades));
    }

    [Fact]
    public void Fill_from_order_txids_aggregates_partials()
    {
        var trades = new[]
        {
            Trade("OSELL", "SOLEUR", "sell", 100m, 5m, 500m, 1m, 0),
            Trade("OSELL", "SOLEUR", "sell", 102m, 5m, 510m, 1m, 1),
            Trade("OTHER", "SOLEUR", "sell", 999m, 1m, 999m, 1m, 2)
        };

        var fill = DecisionWorker.FillFromOrderTxIds(new[] { "OSELL" }, trades);
        Assert.NotNull(fill);
        Assert.Equal(10m, fill!.VolumeExecuted);
        Assert.Equal(1010m, fill.CostEur);
        Assert.Equal(2m, fill.FeeEur);
        Assert.Equal(101m, decimal.Round(fill.AveragePrice, 4)); // 1010 / 10
    }

    [Fact]
    public void Fill_from_order_txids_is_null_when_no_trade_matches()
    {
        var trades = new[] { Trade("OSELL", "SOLEUR", "sell", 100m, 5m, 500m, 1m, 0) };
        Assert.Null(DecisionWorker.FillFromOrderTxIds(new[] { "OMISSING" }, trades));
    }

    [Theory]
    [InlineData("SOL/EUR", "SOLEUR", true)]
    [InlineData("XBT/EUR", "XXBTZEUR", true)]
    [InlineData("XDG/EUR", "XXDGZEUR", true)]
    [InlineData("SOL/EUR", "ADAEUR", false)]
    public void Spot_pair_matching_tolerates_kraken_prefixes(string botPair, string krakenTradePair, bool expected)
    {
        Assert.Equal(expected, DecisionWorker.SpotPairMatches(botPair, krakenTradePair));
    }
}
