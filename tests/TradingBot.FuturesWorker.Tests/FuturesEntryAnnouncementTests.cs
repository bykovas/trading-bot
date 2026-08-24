using TradingBot.Core.Common;
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

    [Fact]
    public void The_bracket_carries_the_move_in_dollars_as_well_as_percent()
    {
        var text = EthShort("ShortReclaim");

        // 4% of 2 436,37 is 97,45; 2% is 48,73. The signs follow the levels: on a short
        // the target sits below the entry and the stop above it.
        Assert.Contains("(−4 % · −97,45 $)", text);
        Assert.Contains("(+2 % · +48,73 $)", text);
    }

    // A cent-priced pair would round to 0,00 at two decimals, which reads as "no move".
    [Fact]
    public void A_small_priced_pair_keeps_enough_decimals_to_show_the_move()
    {
        var text = FuturesEntryAnnouncement.Compose(
            "ARB/USD", "LONG", 0.10329m, "Breakout",
            0.1074216m, 0.1012242m, 4m, 2m, 0.85m, 2.42m);

        Assert.Contains("+0,00413 $", text);
        Assert.DoesNotContain("0,00 $", text);
    }

    [Fact]
    public void The_details_block_repeats_what_the_dashboard_shows()
    {
        var text = FuturesEntryAnnouncement.Compose(
            "ETH/USD", "SHORT", EthPrice, "ShortReclaim",
            EthTarget, EthStop, 4m, 2m, 0.85m, 1.66m,
            new EntrySignalDetails(
                0.85m,
                [new SignalContribution("EMA", 0.30m, "fast EMA above slow"),
                 new SignalContribution("RSI", 0.05m, "rsi in band")],
                SpreadPercent: 0.24m,
                PriceActionDirection: "FALLING",
                PriceActionTrendPercent: -0.10m,
                EmaGapPercent: 0.22m,
                EmaFullyConfirmed: true));

        Assert.Contains("Signalai  0,85", text);
        Assert.Contains("EMA +0,30 · RSI +0,05", text);
        Assert.Contains("spredas 0,24 %", text);
        Assert.Contains("PA FALLING −0,10 %", text);
        Assert.Contains("EMA tarpas +0,22 %", text);
        Assert.Contains("kanalas ShortReclaim", text);
    }

    // Details are optional: without them the post is still complete, and the stake still
    // never appears - the block adds reasoning, not money.
    [Fact]
    public void Without_details_the_post_is_still_whole()
    {
        var text = EthShort("ShortReclaim");

        Assert.DoesNotContain("Signalai", text);
        Assert.Contains("Tikslas", text);
        Assert.DoesNotContain("svertas", text);
    }

    // The shape of the post, pinned: header and intent together, a blank line, the two
    // levels, a blank line, then everything the bot was reading in one unbroken block.
    [Fact]
    public void The_post_keeps_its_agreed_shape()
    {
        var lines = WithDetails().Split('\n');

        Assert.StartsWith("\U0001F534 BlynAI · ETH/USD SHORT", lines[0]);
        Assert.StartsWith("Dabar atidaryčiau", lines[1]);
        Assert.Contains("Kodėl:", lines[1]);
        Assert.Equal("", lines[2]);
        Assert.StartsWith("\U0001F7E2 Tikslas", lines[3]);
        Assert.StartsWith("\U0001F534 Stopas", lines[4]);
        Assert.Equal("", lines[5]);
        Assert.StartsWith("BTC per parą", lines[6]);
        Assert.StartsWith("\U000026AA Signalai", lines[7]);
        Assert.StartsWith("Kontekstas:", lines[8]);
        Assert.Equal(9, lines.Length);
    }

    // One family of marks. A dart board, a road sign and a cog are three drawing styles
    // pretending to be a set; filled circles are a set.
    [Theory]
    [InlineData("🎯")]
    [InlineData("🛑")]
    [InlineData("⚙")]
    public void No_mark_comes_from_another_series(string stray)
    {
        Assert.DoesNotContain(stray, WithDetails());
    }

    [Fact]
    public void Every_mark_is_a_filled_circle()
    {
        var marks = WithDetails()
            .Where(character => character >= 0x2000)
            .Select(character => character.ToString())
            .Where(character => character is not ("·" or "—" or "−" or "\u00a0"))
            .Distinct()
            .ToList();

        Assert.All(marks, mark => Assert.Contains(mark, new[] { "\U0001F534", "\U0001F7E2", "\U000026AA" }
            .SelectMany(circle => circle.Select(part => part.ToString()))));
    }

    private static string WithDetails() =>
        FuturesEntryAnnouncement.Compose(
            "ETH/USD", "SHORT", EthPrice, "ShortReclaim",
            EthTarget, EthStop, 4m, 2m, 0.85m, 1.66m,
            new EntrySignalDetails(
                0.85m,
                [new SignalContribution("EMA", 0.30m, "")],
                0.24m, "FALLING", -0.10m, 0.22m, true));

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
