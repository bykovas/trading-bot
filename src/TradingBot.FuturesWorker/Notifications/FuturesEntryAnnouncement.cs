using System.Globalization;
using System.Text;

namespace TradingBot.FuturesWorker;

// What the bot says out loud when it opens a position. Deliberately NOT a report: no
// size, no leverage, no margin, no fees, no account figures. Only the intention - the
// pair, the direction, where it gets out either way, and why in a sentence a person can
// read. Money belongs on the dashboard, which is behind a link; a channel post that
// carries stakes turns every entry into an invitation to copy it.
//
// Composed as a pure function so the wording can be tested without a network.
internal static class FuturesEntryAnnouncement
{
    // Lithuanian number shape spelled out rather than taken from a culture: a container
    // built with InvariantGlobalization, or with DOTNET_SYSTEM_GLOBALIZATION_INVARIANT
    // set, silently hands back the invariant culture and the channel starts posting
    // "2,338.91" to Lithuanian readers. This cannot drift with the base image.
    // The quietest set of the four the owner reviewed: a bare diagonal arrow for the
    // bet, a verdict for the outcome. One glyph per state, no two states sharing a
    // silhouette, so a notification preview reads at a glance. The white circle stays
    // on the regime line as the one neutral mark.
    private const string OpenLong = "\u2197\uFE0F";
    private const string OpenShort = "\u2198\uFE0F";
    private const string CloseProfit = "\u2705";
    private const string CloseLoss = "\u274C";
    private const string Neutral = "\U000026AA";

    private static readonly NumberFormatInfo Lt = new()
    {
        NumberDecimalSeparator = ",",
        NumberGroupSeparator = " ",
        NumberGroupSizes = [3]
    };

    // Why this entry, in the reader's language. The keys are what ClassifyEntryChannel
    // returns; anything unmapped falls through to the plain-signal sentence rather than
    // inventing a pattern that was not found.
    private static readonly Dictionary<string, string> Reasons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Breakout"] = "kaina ką tik prasimušė pro paros viršūnę ir virš jos išsilaikė — imu tęsinį, ne atšokimą",
        ["Continuation"] = "kyla be sustojimo ir laikosi paros diapazono viršuje — einu kartu su judėjimu",
        ["Reclaim"] = "po plataus svyravimo kaina grįžo į vidurį ir vėl kyla — imu atsigavimą",
        ["DipBounce"] = "kaina smuko ir atšoko nuo dugno — imu atšokimą, kol jis dar šviežias",
        ["ShortBreakdown"] = "kaina pralaužė paros dugną žemyn ir žemiau jo laikosi",
        ["ShortContinuation"] = "krinta be sustojimo ir laikosi paros diapazono apačioje — einu kartu su judėjimu",
        ["ShortReclaim"] = "po smūgio aukštyn kaina vėl gręžiasi žemyn ir laikosi paros diapazono viduryje — tai ne dugno gaudymas, o posūkis",
        ["Standard"] = "ryškaus modelio nėra, bet signalas surinko pakankamai balų"
    };

    public static string Compose(
        string label,
        string pair,
        string side,
        decimal price,
        decimal marginUsd,
        decimal notionalUsd,
        decimal leverage,
        string? entryChannel,
        decimal? takeProfitPrice,
        decimal? stopLossPrice,
        decimal takeProfitPercent,
        decimal stopLossPercent,
        decimal? btc24hChangePct,
        decimal? pair24hChangePct,
        EntrySignalDetails? details = null)
    {
        var isLong = side.Equals("LONG", StringComparison.OrdinalIgnoreCase);
        var text = new StringBuilder();

        // One family of marks throughout: a filled circle, and the colour carries the
        // meaning - green is up or good, red is down or bad, white is neither. A dart
        // board, a road sign and a cog are three different drawing styles pretending to
        // be a set.
        text.Append(isLong ? OpenLong : OpenShort).Append(' ').Append(label).Append(" · ").Append(pair)
            .Append(' ').Append(isLong ? "LONG" : "SHORT").Append('\n');

        if (!Reasons.TryGetValue(entryChannel ?? "Standard", out var reason))
        {
            reason = Reasons["Standard"];
        }

        text.Append("Atidariau ").Append(pair).Append(isLong ? " į viršų" : " žemyn")
            .Append(", kaina ").Append(Money(price)).Append(" $. Kodėl: ").Append(reason).Append('.');
        text.Append("\nĮdėjau ").Append(Money(marginUsd)).Append(" $ savo pinigų (pozicijoje dirba ")
            .Append(Money(notionalUsd)).Append(" $, svertas ").Append(leverage.ToString("0.##", Lt)).Append("×).");

        // Both exits in one spoken sentence right under the reason, percent first and the
        // price level in brackets - the same voice as the intention above it, not a table.
        // On a short the target percentage is negative and the stop positive, which the
        // levels themselves already agree with.
        var exits = new List<string>();
        if (takeProfitPrice is { } tp)
        {
            exits.Add($"\"Take Profit\" limitą pastačiau {Signed(isLong ? takeProfitPercent : -takeProfitPercent)} % (ties {Money(tp)} $)");
        }

        if (stopLossPrice is { } sl)
        {
            exits.Add($"\"stop-loss\" {Signed(isLong ? -stopLossPercent : stopLossPercent)} % (ties {Money(sl)} $)");
        }

        if (exits.Count > 0)
        {
            text.Append('\n').Append(string.Join(", ", exits));
        }

        // Everything the bot was reading, in one unbroken block: the regime the flip gate
        // weighs, what each signal contributed, and the context it was read in.
        var tail = new List<string>();

        var regime = new List<string>();
        if (btc24hChangePct is { } btc)
        {
            regime.Add($"BTC per parą {Signed(btc)} %");
        }

        if (pair24hChangePct is { } own)
        {
            regime.Add($"{BaseAsset(pair)} per parą {Signed(own)} %");
        }

        if (regime.Count > 0)
        {
            tail.Add(Neutral + " " + string.Join(" · ", regime));
        }

        tail.AddRange(DetailLines(details, entryChannel));

        if (tail.Count > 0)
        {
            text.Append("\n\n").Append(string.Join("\n", tail));
        }

        return text.ToString().TrimEnd();
    }

    // What each way of leaving a position sounds like in the reader's language. Keys are
    // exit-reason codes from every close site; anything unmapped falls through to the
    // plain sentence with the code attached, so a new code cannot silently say nothing.
    private static readonly Dictionary<string, string> CloseReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SELL_STOP_LOSS"] = "kaina pasiekė stop-loss",
        ["EXCHANGE_STOP_LOSS"] = "kaina pasiekė stop-loss",
        ["SELL_TRAILING_STOP"] = "kaina nuėjo į pelną ir atsitraukė nuo viršūnės — trailing stop",
        ["EXCHANGE_TRAILING_STOP"] = "kaina nuėjo į pelną ir atsitraukė nuo viršūnės — trailing stop",
        ["SELL_TAKE_PROFIT"] = "pasiektas tikslas biržoje",
        ["EXCHANGE_TAKE_PROFIT"] = "pasiektas tikslas biržoje",
        ["SELL_MAX_HOLD"] = "per 6 valandas pozicija neišjudėjo į pelną — uždariau seną nuostolį",
        ["SIGNAL_REVERSAL"] = "signalas, dėl kurio atidariau, užgeso — uždariau",
        ["EXCHANGE_CLOSE"] = "uždaryta ne boto orderiu — rankomis",
        ["EXCHANGE_LIQUIDATION"] = "pozicija likviduota biržos"
    };

    // The close post: the outcome in the reader's money first, the prices and the hold
    // time after it, the reason last. The circle is the OUTCOME - green earned, red
    // lost - where the opening's circle is the direction; an opening has no outcome yet
    // and a close has no intention left.
    public static string ComposeClose(
        string label,
        string pair,
        string side,
        decimal marginUsd,
        decimal notionalUsd,
        decimal leverage,
        decimal entryPrice,
        decimal exitPrice,
        decimal pnlUsd,
        TimeSpan held,
        string reasonCode)
    {
        var text = new StringBuilder();
        text.Append(pnlUsd > 0m ? CloseProfit : CloseLoss).Append(' ').Append(label).Append(" · ")
            .Append(pair).Append(' ').Append(side.ToUpperInvariant()).Append(" uždaryta\n");
        text.Append("Įdėjau ").Append(Money(marginUsd)).Append(" $ savo pinigų (pozicijoje dirbo ")
            .Append(Money(notionalUsd)).Append(" $, svertas ").Append(leverage.ToString("0.##", Lt)).Append("×).\n");

        var pct = marginUsd > 0m ? pnlUsd / marginUsd * 100m : 0m;
        text.Append(pnlUsd > 0m ? "Uždirbau " : "Praradau ")
            .Append(Signed(pnlUsd, 2)).Append(" $ — tai ").Append(Signed(pct, 0)).Append(" % nuo įdėtų.\n");

        text.Append("Atidariau už ").Append(Money(entryPrice)).Append(" $, uždariau už ")
            .Append(Money(exitPrice)).Append(" $ · laikiau ").Append(Hold(held)).Append('\n');

        if (!CloseReasons.TryGetValue(reasonCode ?? "", out var why))
        {
            why = $"uždaryta pagal boto taisykles ({reasonCode})";
        }

        text.Append("Kodėl uždaryta: ").Append(why).Append('.');
        return text.ToString();
    }

    private static string Hold(TimeSpan held)
    {
        if (held < TimeSpan.Zero)
        {
            held = TimeSpan.Zero;
        }

        return held.TotalDays >= 1
            ? $"{(int)held.TotalDays} d. {held.Hours} val."
            : held.TotalHours >= 1
                ? $"{(int)held.TotalHours} val. {held.Minutes} min."
                : $"{Math.Max(1, held.Minutes)} min.";
    }

    // The same breakdown the dashboard shows under a decision, in the same words: what
    // each signal contributed to the score, then the context the score was read in.
    // Still no stake and no leverage - those say how much is on the table, and this
    // section is about why the bot thinks the trade is there at all.
    private static List<string> DetailLines(EntrySignalDetails? details, string? entryChannel)
    {
        var lines = new List<string>();
        if (details is null)
        {
            return lines;
        }

        var signals = new List<string> { details.Score.ToString("0.00", Lt) };
        // A contribution that scored nothing says nothing, and a list padded with +0,00
        // pushes the ones that mattered off the first screen.
        signals.AddRange(details.Contributions
            .Where(contribution => contribution.Value != 0m)
            .Select(contribution => $"{contribution.Name} {Signed(contribution.Value, 2)}"));
        lines.Add($"Signalai  {string.Join(" · ", signals)}");

        var context = new List<string>();
        if (details.SpreadPercent is { } spread)
        {
            context.Add($"spredas {spread.ToString("0.###", Lt)} %");
        }

        if (!string.IsNullOrWhiteSpace(details.PriceActionDirection))
        {
            var move = details.PriceActionTrendPercent is { } trend ? $" {Signed(trend, 2)} %" : "";
            context.Add($"PA {details.PriceActionDirection}{move}");
        }

        if (details.EmaGapPercent is { } gap)
        {
            context.Add($"EMA tarpas {Signed(gap, 2)} %");
        }

        context.Add(details.EmaFullyConfirmed ? "EMA patvirtinta" : "EMA nepatvirtinta");
        context.Add($"kanalas {entryChannel ?? "Standard"}");
        lines.Add($"Kontekstas: {string.Join(" · ", context)}");

        return lines;
    }

    // "XMR/USD" -> "XMR". The quote side is always USD here and repeating it in a line
    // about the pair's own move reads as noise.
    private static string BaseAsset(string pair)
    {
        var slash = pair.IndexOf('/');
        return slash > 0 ? pair[..slash] : pair;
    }

    // Prices here span 0.000012 to 75 000, so a fixed number of decimals is either
    // useless at the bottom or absurd at the top.
    private static string Money(decimal value) =>
        value.ToString("N" + Decimals(value).ToString(CultureInfo.InvariantCulture), Lt);

    private static int Decimals(decimal value)
    {
        var magnitude = Math.Abs(value);
        return magnitude >= 1m ? 2
            : magnitude >= 0.01m ? 5
            : 8;
    }

    private static string Signed(decimal value, int? fixedDecimals = null) =>
        (value > 0m ? "+" : value < 0m ? "−" : "")
        + Math.Abs(value).ToString(fixedDecimals is { } d ? "F" + d.ToString(CultureInfo.InvariantCulture) : "0.##", Lt);
}

// What the dashboard shows under a decision, carried into the post so a reader sees the
// same breakdown in both places rather than two versions of the same trade.
internal sealed record EntrySignalDetails(
    decimal Score,
    IReadOnlyList<SignalContribution> Contributions,
    decimal? SpreadPercent,
    string? PriceActionDirection,
    decimal? PriceActionTrendPercent,
    decimal? EmaGapPercent,
    bool EmaFullyConfirmed);
