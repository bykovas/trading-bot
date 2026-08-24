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
    // One series, three colours. Filled circles only - a dart board, a road sign and a
    // cog render in three different drawing styles and read as borrowed clip art.
    private const string Green = "\U0001F7E2";
    private const string Red = "\U0001F534";
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
        string pair,
        string side,
        decimal price,
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
        text.Append(isLong ? Green : Red).Append(" BlynAI · ").Append(pair).Append(' ')
            .Append(isLong ? "LONG" : "SHORT").Append('\n');

        if (!Reasons.TryGetValue(entryChannel ?? "Standard", out var reason))
        {
            reason = Reasons["Standard"];
        }

        text.Append("Dabar atidaryčiau ").Append(pair).Append(isLong ? " į viršų" : " žemyn")
            .Append(", kaina ").Append(Money(price)).Append(" $. Kodėl: ").Append(reason).Append('.');

        // Both exits in one spoken sentence right under the reason, percent first and the
        // price level in brackets - the same voice as the intention above it, not a table.
        // On a short the target percentage is negative and the stop positive, which the
        // levels themselves already agree with.
        var exits = new List<string>();
        if (takeProfitPrice is { } tp)
        {
            exits.Add($"\"Take Profit\" limitą daryčiau {Signed(isLong ? takeProfitPercent : -takeProfitPercent)} % (ties {Money(tp)} $)");
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
