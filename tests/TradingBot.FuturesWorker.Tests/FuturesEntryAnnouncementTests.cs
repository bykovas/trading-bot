using TradingBot.Core.Common;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

// The channel posts, trimmed to what the owner actually reads. An opening states the
// stake, the price and the two exits; a closing states the outcome against the money
// put in and why the position left. Both bots write the same channel, so each post is
// headed by its instance's face and label - two voices without them read as one bot
// contradicting itself.
public sealed class FuturesEntryAnnouncementTests
{
    private const string Luko = "\U0001F60E";
    private const string Byko = "\U0001F60B";

    // Real numbers: futures-lukas-live opened this ETH short on 2026-08-24 at 04:10.
    private const decimal EthPrice = 2436.367213114754m;
    private const decimal EthStop = 2485.09455738m;
    private const decimal EthTarget = 2338.91252459m;

    [Fact]
    public void An_opening_is_the_face_the_direction_the_stake_and_the_exits()
    {
        var text = EthShort("ShortReclaim");
        var lines = text.Split('\n');

        Assert.Equal(2, lines.Length);
        Assert.Equal(Luko + "\U0001F4B2 LUKO · ETH/USD <b>SHORT</b> ↘️", lines[0]);
        Assert.Equal(
            "Įdėjau <b>15,00 $</b> savo pinigų (pozicijoje dirba 150,00 $, svertas 10×, kaina 2 436,37 $, TP −4%, SL +2%).",
            lines[1]);
    }

    // The prose, the regime line, the score breakdown and the context line are gone by
    // request. They were true and nobody read them.
    [Fact]
    public void An_opening_carries_no_prose_and_no_diagnostics()
    {
        var text = WithDetails();

        Assert.DoesNotContain("Kodėl:", text);
        Assert.DoesNotContain("Atidariau", text);
        Assert.DoesNotContain("per parą", text);
        Assert.DoesNotContain("Signalai", text);
        Assert.DoesNotContain("Kontekstas", text);
    }

    [Fact]
    public void A_long_targets_upward_and_stops_downward()
    {
        var text = FuturesEntryAnnouncement.Compose(
            Byko, "BYKO", "ARB/USD", "LONG", 0.103285m, 15m, 150m, 10m, "Breakout",
            0.10742m, 0.10122m, 4m, 2m, 0.85m, 1.66m);

        Assert.StartsWith(Byko + "\U0001F4B2 BYKO ·", text);
        Assert.EndsWith("<b>LONG</b> ↗️", text.Split('\n')[0]);
        Assert.Contains("TP +4%", text);
        Assert.Contains("SL −2%", text);
    }

    // An instance with no face configured still posts; the mark simply leads.
    [Fact]
    public void A_missing_face_leaves_the_mark_leading()
    {
        var text = FuturesEntryAnnouncement.Compose(
            "", "BlynAI", "ETH/USD", "SHORT", EthPrice, 15m, 150m, 10m, null,
            EthTarget, EthStop, 4m, 2m, null, null);

        Assert.StartsWith("\U0001F4B2 BlynAI ·", text);
    }

    [Fact]
    public void A_profitable_close_is_green_and_counts_from_the_money_put_in()
    {
        var text = FuturesEntryAnnouncement.ComposeClose(
            Luko, "LUKO", "XLM/USD", "LONG", 15m, 150m, 10m,
            entryPrice: 0.3812m, exitPrice: 0.3976m, pnlUsd: 6.45m,
            held: new TimeSpan(2, 14, 30), reasonCode: "SELL_TRAILING_STOP");
        var lines = text.Split('\n');

        Assert.Equal(2, lines.Length);
        Assert.Equal(Luko + "\U0001F4B0\U0001F929 LUKO · XLM/USD <b>LONG</b> uždaryta", lines[0]);
        Assert.Equal(
            "Uždirbau <b>+6,45 $</b> (43 % įdėtų pinigų) · laikiau 2 val. 14 min. · why: kaina nuėjo į pelną ir atsitraukė nuo viršūnės — trailing stop.",
            lines[1]);
    }

    // The entry and exit prices are gone with the "Įdėjau" line: the stake now sits in
    // the sentence that spends it.
    [Fact]
    public void A_close_no_longer_prints_the_two_prices()
    {
        var text = FuturesEntryAnnouncement.ComposeClose(
            Byko, "BYKO", "PENDLE/USD", "LONG", 15m, 150m, 10m,
            entryPrice: 2.4818m, exitPrice: 2.4278m, pnlUsd: -3.27m,
            held: TimeSpan.FromMinutes(52), reasonCode: "SELL_STOP_LOSS");

        Assert.StartsWith(Byko + "\U0001F4B0\U0001F62D BYKO · PENDLE/USD <b>LONG</b> uždaryta", text);
        Assert.Contains("Praradau <b>−3,27 $</b> (22 % įdėtų pinigų)", text);
        Assert.Contains("laikiau 52 min.", text);
        Assert.Contains("why: kaina pasiekė stop-loss.", text);
        Assert.DoesNotContain("Atidariau už", text);
        Assert.DoesNotContain("pozicijoje dirbo", text);
    }

    // The one close the bot did not make must say so, not claim it as its own.
    [Fact]
    public void A_manual_close_is_attributed_to_the_hand()
    {
        var text = FuturesEntryAnnouncement.ComposeClose(
            Byko, "BYKO", "XMR/USD", "LONG", 15m, 150m, 10m,
            424.11m, 421.98m, -17.76m, TimeSpan.FromHours(3), "EXCHANGE_CLOSE");

        Assert.Contains("uždaryta ne boto orderiu — rankomis", text);
    }

    // A code added next month cannot silently say nothing: it falls through with the
    // code visible, so the channel shows something odd rather than something wrong.
    [Fact]
    public void An_unknown_close_reason_shows_its_code()
    {
        var text = FuturesEntryAnnouncement.ComposeClose(
            Luko, "LUKO", "ETH/USD", "SHORT", 15m, 150m, 10m,
            2436m, 2400m, 2.1m, TimeSpan.FromMinutes(9), "SOMETHING_NEW");

        Assert.Contains("uždaryta pagal boto taisykles (SOMETHING_NEW)", text);
    }

    [Fact]
    public void Hold_time_reads_naturally_at_every_scale()
    {
        string close(TimeSpan held) => FuturesEntryAnnouncement.ComposeClose(
            Luko, "LUKO", "ETH/USD", "LONG", 15m, 150m, 10m, 100m, 101m, 1m, held, "SELL_STOP_LOSS");

        Assert.Contains("laikiau 5 min.", close(TimeSpan.FromMinutes(5)));
        Assert.Contains("laikiau 1 min.", close(TimeSpan.FromSeconds(20)));
        Assert.Contains("laikiau 2 val. 14 min.", close(new TimeSpan(2, 14, 30)));
        Assert.Contains("laikiau 1 d. 3 val.", close(new TimeSpan(27, 5, 0)));
    }

    // The two faces must differ, or the label is doing all the work again.
    [Fact]
    public void The_two_instances_wear_different_faces()
    {
        Assert.NotEqual(Luko, Byko);
        Assert.StartsWith(Luko, EthShort(null));
        Assert.StartsWith(Byko, FuturesEntryAnnouncement.ComposeClose(
            Byko, "BYKO", "ETH/USD", "LONG", 15m, 150m, 10m, 100m, 101m, 1m, TimeSpan.FromMinutes(5), "SELL_STOP_LOSS"));
    }

    // The strategy that opened the trade closes the head line, on both posts, so which
    // book a trade belongs to is never mixed in the channel.
    [Fact]
    public void The_strategy_closes_the_open_head_line()
    {
        var text = FuturesEntryAnnouncement.Compose(
            Byko, "BYKO", "ARB/USD", "LONG", 0.103285m, 15m, 150m, 10m, "Breakout",
            0.10742m, 0.10122m, 4m, 2m, 0.85m, 1.66m, null, "Reversal");

        Assert.Equal(Byko + "\U0001F4B2 BYKO · ARB/USD <b>LONG</b> ↗️ · Reversal", text.Split('\n')[0]);
    }

    [Fact]
    public void The_strategy_closes_the_close_head_line()
    {
        var text = FuturesEntryAnnouncement.ComposeClose(
            Luko, "LUKO", "XLM/USD", "LONG", 15m, 150m, 10m,
            0.3812m, 0.3976m, 6.45m, new TimeSpan(2, 14, 30), "SELL_TRAILING_STOP", "Momentum");

        Assert.Equal(Luko + "\U0001F4B0\U0001F929 LUKO · XLM/USD <b>LONG</b> uždaryta · Momentum", text.Split('\n')[0]);
    }

    // No strategy given (spot, legacy, or a caller that does not track it): the head
    // line ends exactly as before, with no trailing separator.
    [Fact]
    public void A_missing_strategy_leaves_the_head_line_unchanged()
    {
        var open = EthShort("ShortReclaim");
        var close = FuturesEntryAnnouncement.ComposeClose(
            Byko, "BYKO", "XMR/USD", "LONG", 15m, 150m, 10m,
            424.11m, 421.98m, -17.76m, TimeSpan.FromHours(3), "EXCHANGE_CLOSE");

        Assert.EndsWith("<b>SHORT</b> ↘️", open.Split('\n')[0]);
        Assert.EndsWith("uždaryta", close.Split('\n')[0]);
    }

    private static string WithDetails() =>
        FuturesEntryAnnouncement.Compose(
            Luko, "LUKO", "ETH/USD", "SHORT", EthPrice, 15m, 150m, 10m, "ShortReclaim",
            EthTarget, EthStop, 4m, 2m, 0.85m, 1.66m,
            new EntrySignalDetails(
                0.85m,
                [new SignalContribution("EMA", 0.30m, "")],
                0.24m, "FALLING", -0.10m, 0.22m, true));

    private static string EthShort(string? channel) =>
        FuturesEntryAnnouncement.Compose(
            Luko, "LUKO", "ETH/USD", "SHORT", EthPrice, 15m, 150m, 10m, channel,
            EthTarget, EthStop, 4m, 2m, 0.85m, 1.66m);
}
