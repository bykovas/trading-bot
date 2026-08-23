// --------------------------------------------------------------------
// The share card, rendered when it is asked for.
//
// The PNG used to be built by hand and committed, which froze the figures on
// it at the second the build ran. A card is most often shared on the day
// something happened — that is exactly when a stale number is worst — so the
// picture is now made per request, off the same /api/dashboard the page reads
// and the same equity-day.js arithmetic the page uses.
//
//   GET /og/card.png?bot=<botInstanceId>&theme=luko|byko
//   GET /og/tags?bot=<botInstanceId>&theme=…&origin=https://…   (nginx SSI)
//   GET /healthz
//
// Rules this thing lives by:
//  - never 5xx to a crawler. A card is a picture; if the data is unavailable,
//    the last good picture is better than an error page in a share preview.
//  - never render a card with blank fields. No numbers means no number block.
//  - one browser for the process, not one per request.
// --------------------------------------------------------------------

import { createServer } from "node:http";
import { createHash } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";

import { cardHtml, cardCss, cardBody, THEMES } from "./og-card.mjs";
import { fontsFor } from "./og-fonts.mjs";
import {
  trimToLaunch, num, signedPercent, formatMoney, formatSignedMoney, tradesWord
} from "../public/equity-day.js";

const here = dirname(fileURLToPath(import.meta.url));

const PORT = Number(process.env.OG_PORT || 8090);
const API_BASE = (process.env.OG_API_BASE || "http://trading-bot-api:8080").replace(/\/+$/, "");
const CACHE_DIR = process.env.OG_CACHE_DIR || "/var/cache/og";
const API_TIMEOUT_MS = Number(process.env.OG_API_TIMEOUT_MS || 4000);
// Long enough that a burst of crawlers costs one render, short enough that a
// card shared minutes after a good day carries that day's figure.
const FRESH_SECONDS = Number(process.env.OG_FRESH_SECONDS || 300);

// Which account each theme belongs to. A card with the wrong bot is worse than
// no card: it prints someone else's money under this name.
const THEME_BOTS = {
  luko: process.env.OG_BOT_LUKO || "futures-lukas-live",
  byko: process.env.OG_BOT_BYKO || "futures-live"
};
const THEME_DOMAINS = {
  luko: process.env.OG_DOMAIN_LUKO || "blynai.meetluko.eu",
  byko: process.env.OG_DOMAIN_BYKO || "blynai.bykovas.lt"
};

let browser = null;
let browserPromise = null;
// bot+theme → { etag, png, builtAt }
const memory = new Map();
// bot+theme → { rev, at }. The tags are pulled in by nginx on every page load,
// so this must not call the API once per visitor.
const revisions = new Map();

async function getBrowser() {
  if (browser && browser.isConnected()) return browser;
  // Two requests arriving cold must not launch two Chromiums.
  if (!browserPromise) {
    browserPromise = chromium.launch({ args: ["--no-sandbox", "--disable-dev-shm-usage"] })
      .then(instance => { browser = instance; browserPromise = null; return instance; })
      .catch(error => { browserPromise = null; throw error; });
  }
  return browserPromise;
}

async function fetchDashboard(bot) {
  const url = `${API_BASE}/api/dashboard?botInstanceId=${encodeURIComponent(bot)}`;
  const response = await fetch(url, { signal: AbortSignal.timeout(API_TIMEOUT_MS) });
  if (!response.ok) throw new Error(`dashboard ${response.status}`);
  return response.json();
}

// The card's figures, in the same words the page prints them. Anything that
// disagrees with the page here is a card that lies about the account.
function cardValues(data, theme) {
  const symbol = (data && data.summary && data.summary.cashQuoteCurrency) === "EUR" ? "€" : "$";
  const equity = trimToLaunch(data && data.equity);
  const best = equity && equity.bestDay;
  const domain = THEME_DOMAINS[theme];
  if (!best) return { domain, stat: null };

  // The money that was actually at work, falling back to the portfolio on a day
  // with nothing open — the same choice, in the same order, as the page's card.
  const base = best.peakMarginEur > 0 ? best.peakMarginEur : best.open;
  const trades = Number(best.closedTrades) || 0;
  return {
    domain,
    stat: {
      label: `${best.date} · GERIAUSIA PARA`,
      from: formatMoney(base, 2, symbol),
      to: formatMoney(base + best.botEur, 2, symbol),
      gain: formatSignedMoney(best.botEur, 2, symbol),
      pct: signedPercent(best.marginPercent ?? best.botPercent, 1),
      trades: trades > 0 ? `${num(trades, 0)} ${tradesWord(trades)}` : "",
      // A losing best day is still the best day, and says so.
      down: best.botEur < 0
    }
  };
}

// The revision a platform can see. Facebook and LinkedIn fetch og:image once
// and hold it for days, so the URL has to change when the figure does — and
// must NOT change when it does not, or their cache is thrown away every hit.
function revisionOf(bot, values) {
  const stat = values.stat;
  const seed = stat ? `${bot}|${stat.label}|${stat.gain}|${stat.pct}|${stat.trades}` : `${bot}|empty`;
  return createHash("sha1").update(seed).digest("hex").slice(0, 10);
}

// One page per theme, kept alive with its fonts already parsed. Rebuilding the
// document for every request meant re-reading a megabyte of embedded TrueType
// each time, which on the server was most of the time a card took - and none of
// it was drawing. Recycled every so often so a page held open for weeks cannot
// quietly grow.
const RENDERS_PER_PAGE = 200;
const pages = new Map();
// Requests for the same theme share one page, so they have to queue behind each
// other rather than overwrite each other's markup mid-screenshot.
const queues = new Map();

async function pageFor(theme) {
  const held = pages.get(theme);
  if (held && !held.page.isClosed() && held.uses < RENDERS_PER_PAGE) return held;

  if (held && !held.page.isClosed()) await held.page.close().catch(() => {});
  const instance = await getBrowser();
  const page = await instance.newPage({ viewport: { width: 1200, height: 630 }, deviceScaleFactor: 1 });
  const fontsCss = await fontsFor(theme);
  await page.setContent(cardHtml({ theme, fontsCss, values: { domain: "", stat: null } }), { waitUntil: "load" });
  // The claim is painted with background-clip:text. An unloaded face there
  // renders nothing at all rather than something in the wrong font, so the card
  // would ship with its own headline missing. Waited for once, here.
  await page.evaluate(() => document.fonts.ready);
  const fresh = { page, uses: 0 };
  pages.set(theme, fresh);
  return fresh;
}

async function drawOn(theme, values) {
  const held = await pageFor(theme);
  held.uses += 1;
  await held.page.evaluate(([css, body]) => {
    document.getElementById("bc-theme").textContent = css;
    document.getElementById("bc-root").innerHTML = body;
  }, [cardCss(theme, !!values.stat), cardBody(theme, values)]);
  const box = await held.page.locator("#card").boundingBox();
  if (!box || Math.round(box.width) !== 1200 || Math.round(box.height) !== 630) {
    throw new Error(`card is ${box && box.width}x${box && box.height}, expected 1200x630`);
  }
  return await held.page.locator("#card").screenshot({ type: "png" });
}

function renderPng(theme, values) {
  const queued = (queues.get(theme) || Promise.resolve())
    .catch(() => {})
    .then(() => drawOn(theme, values));
  queues.set(theme, queued.catch(() => {}));
  return queued;
}

function cachePath(bot, theme) {
  return resolve(CACHE_DIR, `${theme}-${bot}.png`.replace(/[^a-zA-Z0-9._-]/g, "_"));
}

// The committed card is the floor: a container that has never rendered one and
// an API that will not answer still owes a crawler a picture. It carries no
// figures, so it cannot be out of date about the money - only about the design.
const SHIPPED = {
  luko: resolve(here, "../public/assets/og-algo-1200x630.png"),
  byko: resolve(here, "../public/assets/og-algo-byko-1200x630.png")
};

async function lastGood(bot, theme) {
  for (const path of [cachePath(bot, theme), SHIPPED[theme] || SHIPPED.luko]) {
    try {
      return await readFile(path);
    } catch {
      // try the next one
    }
  }
  return null;
}

async function keep(bot, theme, png) {
  try {
    await mkdir(CACHE_DIR, { recursive: true });
    await writeFile(cachePath(bot, theme), png);
  } catch (error) {
    // A card that cannot be cached is still a card. Say so and carry on.
    console.warn(`og: could not write ${cachePath(bot, theme)}: ${error.message}`);
  }
}

function themeOf(url) {
  const asked = (url.searchParams.get("theme") || "").toLowerCase();
  return THEMES.includes(asked) ? asked : "luko";
}

// Only the two accounts that have a card. The endpoint is public and the bot id
// travels in the query string, so without this anything could be asked for -
// which would put an unbounded set of keys in the caches and render a card under
// a brand for an account that brand does not own.
const KNOWN_BOTS = new Set(Object.values(THEME_BOTS));

function botOf(url, theme) {
  const asked = url.searchParams.get("bot");
  return asked && KNOWN_BOTS.has(asked) ? asked : THEME_BOTS[theme];
}

async function card(url) {
  const theme = themeOf(url);
  const bot = botOf(url, theme);
  const key = `${theme}|${bot}`;
  const held = memory.get(key);
  if (held && Date.now() - held.builtAt < FRESH_SECONDS * 1000) {
    return { ...held, fresh: true, theme, bot };
  }

  let values;
  try {
    values = cardValues(await fetchDashboard(bot), theme);
  } catch (error) {
    console.warn(`og: ${bot} data unavailable (${error.message}), serving the last good card`);
    const png = held ? held.png : await lastGood(bot, theme);
    if (!png) return null;
    return { png, etag: held ? held.etag : `"stale-${revisionOf(bot, { stat: null })}"`, stale: true, theme, bot };
  }

  const etag = `"${revisionOf(bot, values)}"`;
  // Same figures as the copy already in hand: keep the bytes, refresh the clock.
  if (held && held.etag === etag) {
    held.builtAt = Date.now();
    return { ...held, theme, bot };
  }

  const png = await renderPng(theme, values);
  const built = { png, etag, builtAt: Date.now() };
  memory.set(key, built);
  await keep(bot, theme, png);
  return { ...built, theme, bot };
}

async function revisionFor(theme, bot) {
  const key = `${theme}|${bot}`;
  const held = revisions.get(key);
  if (held && Date.now() - held.at < FRESH_SECONDS * 1000) return held.rev;
  try {
    const rev = revisionOf(bot, cardValues(await fetchDashboard(bot), theme));
    revisions.set(key, { rev, at: Date.now() });
    return rev;
  } catch (error) {
    console.warn(`og: revision for ${bot} unavailable (${error.message})`);
    if (held) return held.rev;
    const drawn = memory.get(key);
    return drawn ? drawn.etag.replace(/"/g, "") : "0";
  }
}

// The og:image tags, for nginx to pull in with SSI. They cannot be static: the
// URL has to carry the revision so a platform refetches when the number moves,
// and nginx cannot compute that on its own.
async function tags(url) {
  const theme = themeOf(url);
  const bot = botOf(url, theme);
  const origin = url.searchParams.get("origin") || `https://${THEME_DOMAINS[theme]}`;
  const alt = theme === "byko" ? "BYKO — botas, kuris prekiauja pats" : "LUKO — botas, kuris prekiauja pats";
  const rev = await revisionFor(theme, bot);
  // &amp; in an attribute: the tags land inside the page's <head>, and a bare
  // ampersand there is the parser's business, not ours.
  const image = `${origin}/og/card.png?bot=${encodeURIComponent(bot)}&amp;theme=${theme}&amp;v=${rev}`;
  return `<meta property="og:image" content="${image}">` +
    `<meta property="og:image:type" content="image/png">` +
    `<meta property="og:image:width" content="1200">` +
    `<meta property="og:image:height" content="630">` +
    `<meta property="og:image:alt" content="${alt}">` +
    `<meta name="twitter:image" content="${image}">`;
}

const server = createServer(async (request, response) => {
  const url = new URL(request.url, "http://og");
  try {
    if (url.pathname === "/healthz") {
      response.writeHead(200, { "content-type": "text/plain" });
      return response.end("ok\n");
    }

    if (url.pathname === "/og/tags") {
      const body = await tags(url);
      response.writeHead(200, {
        "content-type": "text/html; charset=utf-8",
        "cache-control": `public, max-age=${FRESH_SECONDS}`
      });
      return response.end(body);
    }

    if (url.pathname === "/og/card.png") {
      const made = await card(url);
      if (!made) {
        // No data and nothing cached: no card at all beats a card of dashes.
        response.writeHead(404, { "content-type": "text/plain", "cache-control": "public, max-age=60" });
        return response.end("no card yet\n");
      }
      if (request.headers["if-none-match"] === made.etag) {
        response.writeHead(304, { etag: made.etag });
        return response.end();
      }
      response.writeHead(200, {
        "content-type": "image/png",
        "content-length": made.png.length,
        etag: made.etag,
        "cache-control": made.stale
          ? "public, max-age=60"
          : `public, max-age=${FRESH_SECONDS}, stale-while-revalidate=86400`
      });
      return response.end(made.png);
    }

    response.writeHead(404, { "content-type": "text/plain" });
    response.end("not found\n");
  } catch (error) {
    console.error(`og: ${url.pathname} failed: ${error.stack || error.message}`);
    // Still not a 5xx if a picture was ever made for this card.
    const theme = themeOf(url);
    const png = await lastGood(botOf(url, theme), theme);
    if (url.pathname === "/og/card.png" && png) {
      response.writeHead(200, {
        "content-type": "image/png", "content-length": png.length,
        "cache-control": "public, max-age=60"
      });
      return response.end(png);
    }
    response.writeHead(url.pathname === "/og/tags" ? 200 : 503, { "content-type": "text/plain" });
    response.end("");
  }
});

server.listen(PORT, () => console.log(`og: listening on ${PORT}, api ${API_BASE}, cache ${CACHE_DIR}`));

for (const signal of ["SIGTERM", "SIGINT"]) {
  process.on(signal, async () => {
    server.close();
    if (browser) await browser.close().catch(() => {});
    process.exit(0);
  });
}
