using Xunit;

namespace TradingBot.FuturesWorker.Tests;

// The announcement says what the bot intends, not what it staked. These tests pin that
// boundary, because it is the whole point of the format: a channel post carrying size
// and leverage turns every entry into an invitation to copy the trade.
public sealed class FuturesEntryAnnouncementTests
{
    // Real numbers: futures-lukas-live opened this ETH short on 2026-08-24 at 04:10.
    private const decimal EthPrice = 2436.367213114754m;
    private const decimal EthStop = 2485.09455738m;
    private const decimal EthTarget = 2338.91252459m;

    [Fact]
    public void A_short_names_the_pair_the_direction_and_both_exits()
    {
        var text = EthShort("ShortReclaim");

        Assert.Contains("ETH/USD SHORT", text);
        Assert.Contains("žemyn", text);
        Assert.Contains("2 338,91", text);
        Assert.Contains("2 485,09", text);
    }

    // On a short the target is below and the stop above. Getting these the wrong way
    // round would read as a bot that does not know which way it is facing.
    [Fact]
    public void A_short_targets_downward_and_stops_upward()
    {
        var text = EthShort("ShortReclaim");
        var target = text[text.IndexOf("Tikslas", StringComparison.Ordinal)..];
        var stop = text[text.IndexOf("Stopas", StringComparison.Ordinal)..];

        Assert.Contains("−4 %", target[..target.IndexOf('\n')]);
        Assert.Contains("+2 %", stop[..stop.IndexOf('\n')]);
    }

    [Fact]
    public void A_long_targets_upward_and_stops_downward()
    {
        var text = FuturesEntryAnnouncement.Compose(
            "ARB/USD", "LONG", 0.10329m, "Breakout",
            takeProfitPrice: 0.1074216m, stopLossPrice: 0.1012242m,
            takeProfitPercent: 4m, stopLossPercent: 2m,
            btc24hChangePct: 0.85m, pair24hChangePct: 2.42m);

        Assert.Contains("🟢", text);
        Assert.Contains("į viršų", text);
        var target = text[text.IndexOf("Tikslas", StringComparison.Ordinal)..];
        Assert.Contains("+4 %", target[..target.IndexOf('\n')]);
    }

    // The rule the format exists for.
    [Theory]
    [InlineData("svertas")]
    [InlineData("marža")]
    [InlineData("pozicija")]
    [InlineData("kiekis")]
    [InlineData("mokestis")]
    [InlineData("148")]
    [InlineData("14,86")]
    public void The_post_carries_no_stake_no_leverage_and_no_money(string forbidden)
    {
        Assert.DoesNotContain(forbidden, EthShort("ShortReclaim"));
    }

    [Fact]
    public void Every_entry_channel_gets_its_own_sentence()
    {
        var channels = new[]
        {
            "Breakout", "Continuation", "Reclaim", "DipBounce",
            "ShortBreakdown", "ShortContinuation", "ShortReclaim", "Standard"
        };

        var sentences = channels.Select(channel => Sentence(EthShort(channel))).ToList();

        Assert.All(sentences, sentence => Assert.NotEmpty(sentence));
        Assert.Equal(channels.Length, sentences.Distinct().Count());
    }

    // An unknown channel must not invent a pattern that was not found - it falls back to
    // the plain-signal sentence, which claims nothing about the shape of the move.
    [Fact]
    public void An_unknown_channel_falls_back_rather_than_inventing_a_pattern()
    {
        Assert.Equal(Sentence(EthShort("Standard")), Sentence(EthShort("SomethingNewNextMonth")));
        Assert.Equal(Sentence(EthShort("Standard")), Sentence(EthShort(null)));
    }

    [Fact]
    public void The_regime_line_carries_the_two_readings_the_flip_gate_weighs()
    {
        var text = EthShort("ShortReclaim");

        Assert.Contains("BTC per parą +0,85 %", text);
        Assert.Contains("ETH per parą", text);
    }

    // No reading, no claim: the line disappears rather than printing a zero that would
    // read as "BTC did not move".
    [Fact]
    public void A_missing_regime_reading_is_omitted_not_zeroed()
    {
        var text = FuturesEntryAnnouncement.Compose(
            "ETH/USD", "SHORT", EthPrice, "ShortReclaim",
            EthTarget, EthStop, 4m, 2m,
            btc24hChangePct: null, pair24hChangePct: null);

        Assert.DoesNotContain("per parą", text);
        Assert.Contains("Tikslas", text);
    }

    private static string EthShort(string? channel) =>
        FuturesEntryAnnouncement.Compose(
            "ETH/USD", "SHORT", EthPrice, channel,
            EthTarget, EthStop, 4m, 2m,
            btc24hChangePct: 0.85m, pair24hChangePct: 1.66m);

    private static string Sentence(string text)
    {
        var start = text.IndexOf("Kodėl:", StringComparison.Ordinal);
        var rest = text[start..];
        return rest[..rest.IndexOf('\n')];
    }
}
