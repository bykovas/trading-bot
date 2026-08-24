// Writes public/og.png — the share card for blynai.eu, the company page.
//
// Unlike the two account cards, this one carries no figures: the company does not
// change every cycle, so there is nothing to render per request. It is photographed
// once from og-blynai-eu.html and committed, and tools/og-server.mjs is not involved.
//
//   npx playwright install chromium
//   node tools/og/build-og.mjs
//
// This one DOES touch the network: the card asks Google Fonts for Space Grotesk 400/600
// and IBM Plex Mono, and tools/fonts/ carries neither at those weights. That is why the
// PNG is committed rather than built on deploy — a card is not worth a build that can
// fail on somebody else's CDN.
//
// Run it when the card design changes, and commit the PNG.

import { chromium } from "playwright";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";
import { stat } from "node:fs/promises";

const here = dirname(fileURLToPath(import.meta.url));
const source = "file://" + resolve(here, "og-blynai-eu.html");
const target = resolve(here, "..", "..", "public", "og.png");

// Twitter and LinkedIn sometimes show the card in a container wider than 1200, and a
// 1x image goes soft there. 2x costs nothing as long as the file stays under a megabyte.
const SCALE = 2;

const browser = await chromium.launch();
const page = await browser.newPage({
  viewport: { width: 1200, height: 630 },
  deviceScaleFactor: SCALE
});

await page.goto(source, { waitUntil: "load" });
// Without this the screenshot catches the fallback faces: the CSS has loaded but the
// font files have not, and the card goes out in system-ui.
await page.evaluate(() => document.fonts.ready);
await page.waitForTimeout(300);

const missing = await page.evaluate(() =>
  ["Space Grotesk", "IBM Plex Mono"].filter(family => !document.fonts.check(`16px "${family}"`)));
if (missing.length > 0) {
  await browser.close();
  throw new Error(`fonts never arrived: ${missing.join(", ")} — the card would render in system-ui`);
}

// The element, not the page: fullPage would carry the white body around the card.
await page.locator("#og").screenshot({ path: target });
await browser.close();

const { size } = await stat(target);
console.log(`OK -> ${target} (${(size / 1024).toFixed(0)} KB, ${1200 * SCALE}x${630 * SCALE})`);
if (size > 1024 * 1024) {
  console.warn("WARNING: over 1 MB. Run it through oxipng/pngquant, or drop SCALE to 1.");
}
