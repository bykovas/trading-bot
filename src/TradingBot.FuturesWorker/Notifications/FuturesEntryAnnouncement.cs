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

        text.Append(isLong ? "🟢" : "🔴").Append(" BlynAI · ").Append(pair).Append(' ')
            .Append(isLong ? "LONG" : "SHORT").Append("\n\n");

        text.Append("Dabar atidaryčiau ").Append(pair).Append(isLong ? " į viršų" : " žemyn")
            .Append(", kaina ").Append(Money(price)).Append(" $.\n");

        if (!Reasons.TryGetValue(entryChannel ?? "Standard", out var reason))
        {
            reason = Reasons["Standard"];
        }

        text.Append("Kodėl: ").Append(reason).Append(".\n\n");

        // The target is the direction the position wants to go, the stop the direction it
        // must not: on a SHORT that means the target sits below and the stop above.
        // The bracket carries how far, twice: in percent and in the distance the price
        // has to travel. Both describe the price, not the stake - what the move would be
        // WORTH is the one number that would give the position size away.
        //
        // The signs need no special-casing: a level below the entry subtracts, and that
        // is exactly when its percentage is negative too.
        if (takeProfitPrice is { } tp)
        {
            text.Append("🎯 Tikslas  ").Append(Money(tp)).Append(" $   (")
                .Append(Signed(isLong ? takeProfitPercent : -takeProfitPercent)).Append(" % · ")
                .Append(SignedMoney(tp - price, price)).Append(" $)\n");
        }

        if (stopLossPrice is { } sl)
        {
            text.Append("🛑 Stopas   ").Append(Money(sl)).Append(" $   (")
                .Append(Signed(isLong ? -stopLossPercent : stopLossPercent)).Append(" % · ")
                .Append(SignedMoney(sl - price, price)).Append(" $)\n");
        }

        // The two readings the flip gate weighs. They are here because they explain what
        // the bot was looking at, not because anything was flipped - nothing on this
        // account is.
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
            text.Append('\n').Append(string.Join(" · ", regime));
        }

        AppendDetails(text, details, entryChannel);

        return text.ToString().TrimEnd();
    }

    // The same breakdown the dashboard shows under a decision, in the same words: what
    // each signal contributed to the score, then the context the score was read in.
    // Still no stake and no leverage - those say how much is on the table, and this
    // section is about why the bot thinks the trade is there at all.
    private static void AppendDetails(StringBuilder text, EntrySignalDetails? details, string? entryChannel)
    {
        if (details is null)
        {
            return;
        }

        text.Append("\n\n⚙️ Signalai  ").Append(details.Score.ToString("0.00", Lt));

        var parts = details.Contributions
            .Where(contribution => contribution.Value != 0m)
            .Select(contribution => $"{contribution.Name} {Signed(contribution.Value, 2)}")
            .ToList();
        if (parts.Count > 0)
        {
            text.Append('\n').Append(string.Join(" · ", parts));
        }

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

        text.Append("\n\nKontekstas\n").Append(string.Join(" · ", context));
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

    // A distance is shown at the precision of the price it belongs to: 97,46 next to
    // 2 436,37, but 0,00413 next to 0,10329, where two decimals would round it to zero.
    private static string SignedMoney(decimal value, decimal reference)
    {
        var text = Math.Abs(value).ToString("N" + Decimals(reference).ToString(CultureInfo.InvariantCulture), Lt);
        return (value > 0m ? "+" : value < 0m ? "−" : "") + text;
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
