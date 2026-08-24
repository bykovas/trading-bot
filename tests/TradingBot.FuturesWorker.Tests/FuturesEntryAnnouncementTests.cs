using TradingBot.Core.Common;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

// The channel posts. Openings state a fact - the label, the direction, the money put in,
// the exits placed. Closings state the outcome in the reader's own money and say why the
// position left. Both bots write the same channel, so the label at the head is what keeps
// two voices from reading as one bot contradicting itself.
public sealed class FuturesEntryAnnouncementTests
{
    // Real numbers: futures-lukas-live opened this ETH short on 2026-08-24 at 04:10.
    private const decimal EthPrice = 2436.367213114754m;
    private const decimal EthStop = 2485.09455738m;
    private const decimal EthTarget = 2338.91252459m;

    [Fact]
    public void An_opening_names_the_label_the_fact_and_the_money_put_in()
    {
        var text = EthShort("ShortReclaim");

        Assert.StartsWith("\U0001F534 LUKO · ETH/USD SHORT", text);
        Assert.Contains("Atidariau ETH/USD žemyn, kaina 2 436,37 $.", text);
        Assert.Contains("Įdėjau 15,00 $ savo pinigų (pozicijoje dirba 150,00 $, svertas 10×).", text);
        Assert.DoesNotContain("atidaryčiau", text);
    }

    [Fact]
    public void The_exits_are_one_sentence_with_percent_and_level()
    {
        var text = EthShort("ShortReclaim");

        Assert.Contains(
            "\"Take Profit\" limitą pastačiau −4 % (ties 2 338,91 $), \"stop-loss\" +2 % (ties 2 485,09 $)",
            text);
    }

    [Fact]
    public void A_long_targets_upward_and_stops_downward()
    {
        var text = FuturesEntryAnnouncement.Compose(
            "BYKO", "ARB/USD", "LONG", 0.10329m, 15m, 150m, 10m, "Breakout",
            0.1074216m, 0.1012242m, 4m, 2m, 0.85m, 2.42m);

        Assert.StartsWith("\U0001F7E2 BYKO ·", text);
        Assert.Contains("Atidariau ARB/USD į viršų", text);
        Assert.Contains("\"Take Profit\" limitą pastačiau +4 % (ties 0,10742 $)", text);
        Assert.Contains("\"stop-loss\" −2 % (ties 0,10122 $)", text);
    }

    // Header, intent, money, exits, blank, then the tail block - pinned so a stray line
    // cannot creep back in.
    [Fact]
    public void The_post_keeps_its_agreed_shape()
    {
        var lines = WithDetails().Split('\n');

        Assert.StartsWith("\U0001F534 LUKO · ETH/USD SHORT", lines[0]);
        Assert.StartsWith("Atidariau", lines[1]);
        Assert.Contains("Kodėl:", lines[1]);
        Assert.StartsWith("Įdėjau", lines[2]);
        Assert.StartsWith("\"Take Profit\" limitą pastačiau", lines[3]);
        Assert.Equal("", lines[4]);
        Assert.StartsWith("\U000026AA BTC per parą", lines[5]);
        Assert.StartsWith("Signalai", lines[6]);
        Assert.StartsWith("Kontekstas:", lines[7]);
        Assert.Equal(8, lines.Length);
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

    [Fact]
    public void An_unknown_channel_falls_back_rather_than_inventing_a_pattern()
    {
        Assert.Equal(Sentence(EthShort("Standard")), Sentence(EthShort("SomethingNewNextMonth")));
        Assert.Equal(Sentence(EthShort("Standard")), Sentence(EthShort(null)));
    }

    [Fact]
    public void The_details_block_repeats_what_the_dashboard_shows()
    {
        var text = WithDetails();

        Assert.Contains("Signalai  0,85", text);
        Assert.Contains("EMA +0,30", text);
        Assert.Contains("spredas 0,24 %", text);
        Assert.Contains("kanalas ShortReclaim", text);
    }

    [Fact]
    public void A_missing_regime_reading_is_omitted_not_zeroed()
    {
        var text = FuturesEntryAnnouncement.Compose(
            "LUKO", "ETH/USD", "SHORT", EthPrice, 15m, 150m, 10m, "ShortReclaim",
            EthTarget, EthStop, 4m, 2m,
            btc24hChangePct: null, pair24hChangePct: null);

        Assert.DoesNotContain("per parą", text);
        Assert.Contains("Take Profit", text);
    }

    // ---- closes ---------------------------------------------------------

    [Fact]
    public void A_profitable_close_is_green_and_counts_from_the_money_put_in()
    {
        var text = FuturesEntryAnnouncement.ComposeClose(
            "LUKO", "XLM/USD", "LONG", 15m, 150m, 10m,
            entryPrice: 0.3812m, exitPrice: 0.3976m, pnlUsd: 6.45m,
            held: new TimeSpan(2, 14, 0), reasonCode: "SELL_TRAILING_STOP");

        Assert.StartsWith("\U0001F7E2 LUKO · XLM/USD LONG uždaryta", text);
        Assert.Contains("Įdėjau 15,00 $ savo pinigų (pozicijoje dirbo 150,00 $, svertas 10×).", text);
        Assert.Contains("Uždirbau +6,45 $ — tai +43 % nuo įdėtų.", text);
        Assert.Contains("Atidariau už 0,38120 $, uždariau už 0,39760 $ · laikiau 2 val. 14 min.", text);
        Assert.Contains("Kodėl uždaryta: kaina nuėjo į pelną ir atsitraukė nuo viršūnės — trailing stop.", text);
    }

    [Fact]
    public void A_losing_close_is_red_and_says_so_plainly()
    {
        var text = FuturesEntryAnnouncement.ComposeClose(
            "BYKO", "PENDLE/USD", "LONG", 15m, 150m, 10m,
            entryPrice: 2.4818m, exitPrice: 2.4278m, pnlUsd: -3.27m,
            held: TimeSpan.FromMinutes(52), reasonCode: "SELL_STOP_LOSS");

        Assert.StartsWith("\U0001F534 BYKO · PENDLE/USD LONG uždaryta", text);
        Assert.Contains("Praradau −3,27 $ — tai −22 % nuo įdėtų.", text);
        Assert.Contains("laikiau 52 min.", text);
        Assert.Contains("Kodėl uždaryta: kaina pasiekė stop-loss.", text);
    }

    // The one close the bot did not make must say so, not claim it as its own.
    [Fact]
    public void A_manual_close_is_attributed_to_the_hand()
    {
        var text = FuturesEntryAnnouncement.ComposeClose(
            "BYKO", "XMR/USD", "LONG", 15m, 150m, 10m,
            424.11m, 421.98m, -17.76m, TimeSpan.FromHours(3), "EXCHANGE_CLOSE");

        Assert.Contains("uždaryta ne boto orderiu — rankomis", text);
    }

    // A code added next month cannot silently say nothing: it falls through with the
    // code visible, so the channel shows something odd rather than something wrong.
    [Fact]
    public void An_unknown_close_reason_shows_its_code()
    {
        var text = FuturesEntryAnnouncement.ComposeClose(
            "LUKO", "ETH/USD", "SHORT", 15m, 150m, 10m,
            2436m, 2400m, 2.1m, TimeSpan.FromMinutes(9), "SOMETHING_NEW");

        Assert.Contains("uždaryta pagal boto taisykles (SOMETHING_NEW)", text);
    }

    [Fact]
    public void Hold_time_reads_naturally_at_every_scale()
    {
        string close(TimeSpan held) => FuturesEntryAnnouncement.ComposeClose(
            "LUKO", "ETH/USD", "LONG", 15m, 150m, 10m, 100m, 101m, 1m, held, "SELL_STOP_LOSS");

        Assert.Contains("laikiau 5 min.", close(TimeSpan.FromMinutes(5)));
        Assert.Contains("laikiau 1 min.", close(TimeSpan.FromSeconds(20)));
        Assert.Contains("laikiau 2 val. 14 min.", close(new TimeSpan(2, 14, 30)));
        Assert.Contains("laikiau 1 d. 3 val.", close(new TimeSpan(27, 5, 0)));
    }

    private static string WithDetails() =>
        FuturesEntryAnnouncement.Compose(
            "LUKO", "ETH/USD", "SHORT", EthPrice, 15m, 150m, 10m, "ShortReclaim",
            EthTarget, EthStop, 4m, 2m, 0.85m, 1.66m,
            new EntrySignalDetails(
                0.85m,
                [new SignalContribution("EMA", 0.30m, "")],
                0.24m, "FALLING", -0.10m, 0.22m, true));

    private static string EthShort(string? channel) =>
        FuturesEntryAnnouncement.Compose(
            "LUKO", "ETH/USD", "SHORT", EthPrice, 15m, 150m, 10m, channel,
            EthTarget, EthStop, 4m, 2m, 0.85m, 1.66m);

    private static string Sentence(string text)
    {
        var start = text.IndexOf("Kodėl:", StringComparison.Ordinal);
        var rest = text[start..];
        return rest[..rest.IndexOf('\n')];
    }
}
