// --------------------------------------------------------------------
// BlynAI Capital — what one day earned, and which day was best.
//
// "Geriausia para" is not a field the API returns; it is arithmetic, and
// this file is the only place it is done. The dashboard inlines this file
// at build time and the og:image renderer imports it as a module, so the
// share card and the page cannot drift apart. Two copies of this would be
// two answers waiting to disagree in public about the same account, which
// is exactly the dishonesty the rest of the project is built to avoid.
//
// Everything here is pure: no DOM, no fetch, no clock. Node and the
// browser must produce identical strings from identical input.
// --------------------------------------------------------------------

// The launch. Nothing on the page reaches back before it - not the chart, not
// the cards, not the honesty line - and there is no way to ask it to: whatever
// the account did earlier belongs to a run that is not being shown, and letting
// one number quietly come from there while the others do not is how a page
// starts lying without anyone deciding to.
export const LAUNCH_DATE = "2026-08-19";

// ---- formatting ----------------------------------------------------
// Lithuanian throughout: comma decimals, non-breaking space before the
// unit, currency symbol after the number, U+2212 for minus. The card and
// the page print the same characters or they are not the same number.
const MINUS = "−";
const NBSP = " ";

const nf = (digits) => new Intl.NumberFormat("lt-LT", {
  minimumFractionDigits: digits,
  maximumFractionDigits: digits
});

export function fixMinus(text) {
  return text.replace(/-/g, MINUS).replace(/−−/g, MINUS);
}

export function num(value, digits = 2) {
  const n = Number(value);
  if (!Number.isFinite(n)) return "—";
  return fixMinus(nf(digits).format(n));
}

export function signed(value, digits = 2) {
  const n = Number(value);
  if (!Number.isFinite(n)) return "—";
  return (n > 0 ? "+" : "") + num(n, digits);
}

// The currency symbol is passed in rather than held in a module variable:
// the renderer serves two accounts from one process and a symbol left over
// from the previous request would print the wrong one. The page wraps these
// two under its own `money`/`signedMoney`, which carry the symbol it is
// currently showing - hence the longer names here, so both can be in scope
// once this file is inlined into the page's own script.
export function formatMoney(value, digits = 2, symbol = "$") {
  const n = Number(value);
  if (!Number.isFinite(n)) return "—";
  return num(n, digits) + NBSP + symbol;
}

export function formatSignedMoney(value, digits = 2, symbol = "$") {
  const n = Number(value);
  if (!Number.isFinite(n)) return "—";
  return signed(n, digits) + NBSP + symbol;
}

export function percent(value, digits = 2) {
  const n = Number(value);
  if (!Number.isFinite(n)) return "—";
  return num(n, digits) + NBSP + "%";
}

export function signedPercent(value, digits = 2) {
  const n = Number(value);
  if (!Number.isFinite(n)) return "—";
  return signed(n, digits) + NBSP + "%";
}

// Lithuanian plurals: 1 sandoris, 2-9 sandoriai, 10-20 sandorių, 21 sandoris …
export function tradesWord(count) {
  const n = Math.abs(Math.round(Number(count) || 0));
  const last = n % 10;
  const lastTwo = n % 100;
  if (lastTwo >= 11 && lastTwo <= 19) return "sandorių";
  if (last === 0) return "sandorių";
  if (last === 1) return "sandoris";
  return "sandoriai";
}

// ---- the arithmetic ------------------------------------------------

// The same arithmetic the API uses for a day's result, applied to the trimmed
// series so that "best day" means the best day since the launch.
export function dayResult(day) {
  const open = Number(day.chainOpen ?? day.open);
  if (!Number.isFinite(open) || open <= 0) return null;
  const moved = Number(day.manualAdjustmentEur) || 0;
  const bot = Number(day.close) - open - moved;
  // The margin percentage is recomputed here rather than taken from the server:
  // the bot figure above is measured from the chained open, so a percentage
  // carried over from the server's own arithmetic would not match the money
  // printed beside it.
  const peak = Number(day.peakMarginEur);
  return {
    date: day.date, open, close: open + bot,
    manualAdjustmentEur: moved, botEur: bot, botPercent: bot / open * 100,
    peakMarginEur: peak > 0 ? peak : null,
    marginPercent: peak > 0 ? bot / peak * 100 : null,
    closedTrades: Number(day.closedTrades) || 0
  };
}

export function trimToLaunch(equity) {
  if (!equity || !Array.isArray(equity.days)) return equity;
  const raw = equity.days.filter(day => day.date >= LAUNCH_DATE);

  // A day opens where the previous one closed. Measuring it from its own first
  // observed cycle instead drops whatever happened between the two: on
  // futures-lukas-live 2026-08-19 closed at 80,99 and 2026-08-20 opened at 83,82,
  // and those 2,83 belonged to no day at all - the daily results summed to 24,22
  // while the account had actually made 27,06. The exception is a stretch nobody
  // watched, which stays unattributed and is drawn grey.
  const days = raw.map((day, index) => {
    if (index === 0) return Object.assign({}, day, { chainOpen: Number(day.open) });
    const prevClose = Number(raw[index - 1].close);
    const blind = Number(day.gapMinutes);
    const outage = Number.isFinite(prevClose) && Number.isFinite(blind) && blind > 60
      && Math.abs(Number(day.open) - prevClose) >= 0.01;
    return Object.assign({}, day, {
      chainOpen: outage || !Number.isFinite(prevClose) ? Number(day.open) : prevClose,
      outage
    });
  });
  const results = days.map(dayResult).filter(Boolean);
  return Object.assign({}, equity, {
    days,
    manualAdjustmentEur: days.reduce((sum, day) => sum + (Number(day.manualAdjustmentEur) || 0), 0),
    yesterday: results.length ? results[results.length - 1] : null,
    // Best by what the card prints: the return on the money that was at work.
    // Ranking by portfolio percent disagreed with the figure beside it - a day
    // that made 41.2% on its capital held the title while a later one made 55.6%
    // on its own and did not, because the account had grown and the same profit
    // was a smaller slice of it.
    bestDay: results.length
      ? results.reduce((best, result) =>
          (result.marginPercent ?? result.botPercent) > (best.marginPercent ?? best.botPercent)
            ? result : best)
      : null
  });
}
