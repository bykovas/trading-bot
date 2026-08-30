using System.Globalization;
using System.Text;

namespace TradingBot.FuturesWorker;

// What the bot says out loud when it opens and closes a position. Two lines each: a head
// that names the instance, the pair and the direction, and one spoken line of figures.
// The head carries a fixed family of marks - a dollar sign for an opening, a money bag
// plus a face for a close - so a notification preview reads at a glance without colour,
// which Telegram does not give a bot.
//
// Composed as a pure function so the wording can be tested without a network.
internal static class FuturesEntryAnnouncement
{
    // One mark per event, one glyph per state. 💲 opens, 💰 closes; the close then carries
    // the outcome face (🤩 earned, 😭 lost) and the opening carries the direction arrow at
    // the end of its head line (↗️ long, ↘️ short). The instance's own face (😋 / 😎) is
    // configuration and precedes every mark.
    private const string OpenMark = "\U0001F4B2";   // 💲
    private const string CloseMark = "\U0001F4B0";  // 💰
    private const string ArrowLong = "↗️";  // ↗️
    private const string ArrowShort = "↘️"; // ↘️
    private const string OutcomeProfit = "\U0001F929"; // 🤩
    private const string OutcomeLoss = "\U0001F62D";   // 😭

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

    // The open post. Two lines: the head, then one spoken sentence that puts the money the
    // reader cares about first - what he put in - and folds the working capital, the price
    // and both exits into a single parenthetical. No reason paragraph and no signal
    // breakdown: those live on the dashboard, behind a link. The extra parameters are kept
    // so the call site does not move while the strategy path around it is still changing.
    public static string Compose(
        string emoji,
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
        EntrySignalDetails? details = null,
        string? strategy = null)
    {
        var isLong = side.Equals("LONG", StringComparison.OrdinalIgnoreCase);
        var text = new StringBuilder();

        // The strategy that opened it - Momentum or Reversal - closes the head line, so
        // which book a trade belongs to is visible at a glance and never mixed.
        text.Append(Head(emoji, OpenMark, DivergesFromLuko(strategy))).Append(label).Append(" · ").Append(pair)
            .Append(' ').Append(Bold(isLong ? "LONG" : "SHORT")).Append(' ')
            .Append(isLong ? ArrowLong : ArrowShort);
        if (!string.IsNullOrWhiteSpace(strategy))
        {
            text.Append(" · ").Append(strategy);
        }

        text.Append('\n');

        // The working capital, the fill price and both exits as one plain detail block;
        // only the reader's own stake is emphasised. On a short the target percentage is
        // negative and the stop positive, which is the direction the price actually moves.
        var detail = new List<string>
        {
            "pozicijoje dirba " + Money(notionalUsd) + " $",
            "svertas " + leverage.ToString("0.##", Lt) + "×",
            "kaina " + Money(price) + " $"
        };
        if (takeProfitPrice is not null)
        {
            detail.Add("TP " + Signed(isLong ? takeProfitPercent : -takeProfitPercent) + "%");
        }

        if (stopLossPrice is not null)
        {
            detail.Add("SL " + Signed(isLong ? -stopLossPercent : stopLossPercent) + "%");
        }

        text.Append("Įdėjau ").Append(Bold(Money(marginUsd) + " $"))
            .Append(" savo pinigų (").Append(string.Join(", ", detail)).Append(").");

        return text.ToString();
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
        // Not a profit-taking exit: six hours passed without the position moving into
        // real profit, so a slim trailing stop pushed it out near breakeven to free the
        // slot. Says so plainly, instead of claiming a peak the price never reached.
        ["SELL_MAX_HOLD_RELEASE"] = "6 valandų pozicija taip ir neišjudėjo į pelną — slankiuoju stopu išstūmiau ją apie nulį, kad atlaisvinčiau vietą",
        ["EXCHANGE_MAX_HOLD_RELEASE"] = "6 valandų pozicija taip ir neišjudėjo į pelną — slankiuoju stopu išstūmiau ją apie nulį, kad atlaisvinčiau vietą",
        ["SELL_TAKE_PROFIT"] = "pasiektas tikslas biržoje",
        ["EXCHANGE_TAKE_PROFIT"] = "pasiektas tikslas biržoje",
        ["SELL_MAX_HOLD"] = "per 6 valandas pozicija neišjudėjo į pelną — uždariau seną nuostolį",
        ["SIGNAL_REVERSAL"] = "signalas, dėl kurio atidariau, užgeso — uždariau",
        ["EXCHANGE_CLOSE"] = "uždaryta ne boto orderiu — rankomis",
        ["EXCHANGE_LIQUIDATION"] = "pozicija likviduota biržos"
    };

    // The close post. Two lines: the head carries the money bag and the outcome face, then
    // one spoken line - what it did to the money he put in, how long it was held, and why.
    // The unused price/size parameters are kept so the call site does not move.
    public static string ComposeClose(
        string emoji,
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
        string reasonCode,
        string? strategy = null)
    {
        var text = new StringBuilder();
        text.Append(Head(emoji, CloseMark + (pnlUsd > 0m ? OutcomeProfit : OutcomeLoss), DivergesFromLuko(strategy, reasonCode)))
            .Append(label).Append(" · ").Append(pair).Append(' ')
            .Append(Bold(side.ToUpperInvariant())).Append(" uždaryta");
        if (!string.IsNullOrWhiteSpace(strategy))
        {
            text.Append(" · ").Append(strategy);
        }

        text.Append('\n');

        var pct = marginUsd > 0m ? pnlUsd / marginUsd * 100m : 0m;
        text.Append(pnlUsd > 0m ? "Uždirbau " : "Praradau ")
            .Append(Bold(Signed(pnlUsd, 2) + " $"))
            .Append(" (").Append(Math.Abs(pct).ToString("0", Lt)).Append(" % įdėtų pinigų)")
            .Append(" · laikiau ").Append(Hold(held))
            .Append(" · why: ");

        if (!CloseReasons.TryGetValue(reasonCode ?? "", out var why))
        {
            why = $"uždaryta pagal boto taisykles ({reasonCode})";
        }

        text.Append(why).Append('.');
        return text.ToString();
    }

    // ⚠️ marks a trade LUKO would NOT have opened or closed - a decision divergence, not the
    // TP/SL parameter differences (those differ on every arm trade and would make the flag
    // meaningless). Today that means a Reversal-book entry (LUKO has no reversal) or a max-hold
    // release close (LUKO has no max-hold). Both are dormant under the current arm config, so
    // the flag stays off until such a rule is turned on - forward-looking by design. Derived
    // from the strategy tag and the close reason, so no new persisted state is needed.
    internal static bool DivergesFromLuko(string? strategy, string? reasonCode = null) =>
        (strategy?.Equals("Reversal", StringComparison.OrdinalIgnoreCase) == true)
        || (reasonCode is not null
            && (reasonCode.Equals("EXCHANGE_MAX_HOLD_RELEASE", StringComparison.OrdinalIgnoreCase)
                || reasonCode.Equals("SELL_MAX_HOLD_RELEASE", StringComparison.OrdinalIgnoreCase)
                || reasonCode.Equals("SELL_MAX_HOLD", StringComparison.OrdinalIgnoreCase)));

    // The instance's face, then the mark for what happened, then a ⚠️ when the trade diverges
    // from LUKO, then a space before the label. An instance without a configured emoji simply
    // starts with the mark, as before.
    private static string Head(string emoji, string mark, bool diverges = false)
    {
        var head = string.IsNullOrWhiteSpace(emoji) ? mark : emoji + mark;
        return diverges ? head + "⚠️ " : head + " ";
    }

    // Telegram gives a bot bold but no colour, so the figures carry the emphasis and the
    // icons carry the verdict. HTML rather than MarkdownV2: the only characters needing an
    // escape are the three below, where MarkdownV2 would demand a backslash in front of
    // every dot and dash in a price.
    private static string Bold(string text) => "<b>" + Escape(text) + "</b>";

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

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

    // Prices here span 0.000012 to 75 000, so a fixed number of decimals is either useless
    // at the bottom or absurd at the top.
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
