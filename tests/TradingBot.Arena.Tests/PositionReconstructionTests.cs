using TradingBot.Arena;
using Xunit;

namespace TradingBot.Arena.Tests;

public sealed class PositionReconstructionTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-10T23:00:00Z");
    private static readonly DateTimeOffset Since = DateTimeOffset.Parse("2026-10-11T00:00:00Z");
    private static VenueFill Fill(string id, int hour, decimal old, decimal quantity, decimal price, string side,
        string position = "position-a", decimal fee = .1m, decimal funding = 0, decimal? leverage = 5) =>
        new(id, "order-" + id, position, "PF_XBTUSD", Start.AddHours(hour), side, quantity, price, old,
            old + quantity * (side == "buy" ? 1 : -1), fee, funding, hour, leverage);
    private static ReconstructedHistory Build(VenueFill[] fills, VenueFunding[]? funding = null, JournalAnnotation[]? journal = null) =>
        PositionReconstruction.Build(fills, funding ?? [], journal ?? [], Since, Start.AddDays(5));
    [Fact]
    public void Earlier_open_partial_closes_and_adds_form_one_position_with_all_costs()
    {
        var fills = new[] { Fill("one", 0, 0, 2, 100, "buy"), Fill("two", 2, 2, 1, 120, "sell"),
            Fill("three", 3, 1, 1, 110, "buy"), Fill("four", 4, 2, 2, 130, "sell") };
        var result = Build(fills, [new("funding-1", "position-a", "PF_XBTUSD", Start.AddHours(1), -.3m)],
            [new("order-one", "firing-one", "rule-one"), new("order-four", ExitReason: "tp")]);
        var position = Assert.Single(result.Closed);
        Assert.Empty(result.Open); Assert.Equal(Start, position.OpenedAt); Assert.Equal(3, position.Size);
        Assert.Equal(310m / 3, position.Entry); Assert.Equal(380m / 3, position.Exit);
        Assert.Equal(.4m, position.Fees); Assert.Equal(-.3m, position.Funding);
        Assert.Equal("kraken-PF_XBTUSD-one", position.TradeId); Assert.Equal("firing-one", position.FiringId); Assert.Equal("tp", position.ExitReason);
        Assert.Equal(4, position.Payload()["fills"]!.AsArray().Count);
    }
    [Fact]
    public void Hedge_scopes_are_separate_and_active_position_is_never_a_closed_trade()
    {
        var result = Build([Fill("long", 0, 0, 2, 100, "buy"), Fill("short", 1, 0, 3, 110, "sell", "hedge-b"), Fill("close", 2, 2, 2, 115, "sell")]);
        Assert.Equal("long", Assert.Single(result.Closed).Side);
        var open = Assert.Single(result.Open); Assert.Equal("short", open.Side); Assert.Equal(3, open.Size);
        Assert.Null(open.Payload()["trade_id"]); Assert.Null(open.Payload()["closed_at"]);
    }
    [Fact]
    public void Reversal_splits_fees_and_does_not_duplicate_realized_funding()
    {
        var result = Build([Fill("open", 0, 0, 2, 100, "buy", fee: 2), Fill("reverse", 2, 2, 3, 110, "sell", fee: 3, funding: -4),
            Fill("flat", 3, -1, 1, 90, "buy", fee: 1)]);
        Assert.Equal(2, result.Closed.Count); Assert.Equal(6, result.Closed.Sum(p => p.Fees)); Assert.Equal(-4, result.Closed.Sum(p => p.Funding));
        Assert.Equal(-4, result.Closed[0].Funding); Assert.Equal(0, result.Closed[1].Funding);
        Assert.Equal("kraken-PF_XBTUSD-reverse", result.Closed[1].TradeId);
    }
    [Fact]
    public void Old_closed_cycles_are_not_imported_and_missing_open_is_never_guessed()
    {
        Assert.Empty(Build([Fill("open", 0, 0, 1, 100, "buy"), Fill("flat", 0, 1, 1, 110, "sell")]).Closed);
        Assert.Throws<InvalidDataException>(() => Build([Fill("orphan", 2, 1, 1, 110, "sell")]));
    }
    [Fact]
    public void Partial_reduction_retains_the_correct_remaining_average_cost()
    {
        var open = Assert.Single(Build([Fill("a", 0, 0, 2, 100, "buy"), Fill("b", 1, 2, 1, 120, "sell"), Fill("c", 2, 1, 1, 110, "buy")]).Open);
        Assert.Equal(2, open.Size); Assert.Equal(105, open.Entry);
    }
    [Fact]
    public void Unknown_or_changed_historical_leverage_is_not_defaulted()
    {
        var result = Build([Fill("a", 0, 0, 1, 100, "buy", leverage: null), Fill("b", 2, 1, 1, 110, "sell")]);
        Assert.Null(Assert.Single(result.Closed).Payload()["leverage"]);
    }
    [Fact]
    public void Duplicate_fill_and_funding_ids_or_ambiguous_funding_are_errors()
    {
        var f = Fill("a", 0, 0, 1, 100, "buy");
        Assert.Throws<InvalidDataException>(() => Build([f, f]));
        Assert.Throws<InvalidDataException>(() => Build([f], [new("fund", "wrong-position", "PF_XBTUSD", Start.AddHours(1), 1)]));
        var funding = new VenueFunding("fund", "position-a", "PF_XBTUSD", Start.AddHours(1), 1);
        Assert.Throws<InvalidDataException>(() => Build([f], [funding, funding]));
    }
    [Fact]
    public void Bot_selection_requires_every_open_and_close_order_not_merely_an_entry_or_backfilled_close()
    {
        var history = Build([Fill("a", 0, 0, 1, 100, "buy"), Fill("b", 2, 1, 1, 110, "sell")]);
        var mixed = PositionReconstruction.BotOwnedOnly(history, new HashSet<string> { "order-a" });
        Assert.Empty(mixed.Included.Closed); Assert.Equal("mixed_or_unproven_order_ownership", Assert.Single(mixed.Excluded).Reason);
        var manual = PositionReconstruction.BotOwnedOnly(history, new HashSet<string>());
        Assert.Empty(manual.Included.Closed); Assert.Single(manual.Excluded);
        var bot = PositionReconstruction.BotOwnedOnly(history, new HashSet<string> { "order-a", "order-b" });
        Assert.Single(bot.Included.Closed); Assert.Empty(bot.Excluded);
    }
}
