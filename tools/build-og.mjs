// Generates public/assets/og-algo-1200x630.png from og-algo.html.
// Run by hand when the card changes — NOT part of the deploy pipeline.
//
//   npm i -D playwright && npx playwright install chromium
//   node tools/build-og.mjs
//
// Before the first run, swap the Google Fonts <link> in og-algo.html for the
// local @font-face block (see the comment at the top of that file) and put
// Anton-Regular.woff2, Archivo-ExtraLight.woff2 and DMMono-Medium.woff2 in
// tools/fonts/. All three are required: Anton (ALGO + claim), Archivo 200
// (LUKO wordmark), DM Mono 500 (domain).

import { chromium } from "playwright";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";
import { mkdir, stat } from "node:fs/promises";

const here = dirname(fileURLToPath(import.meta.url));
const source = resolve(here, "og-algo.html");
const out = resolve(here, "../public/assets/og-algo-1200x630.png");

const browser = await chromium.launch();
const page = await browser.newPage({
  viewport: { width: 1200, height: 630 },
  deviceScaleFactor: 1,
});

await page.goto("file://" + source, { waitUntil: "load" });
await page.evaluate(() => document.fonts.ready);

// The card must be exactly 1200×630 — a stray margin or a wrapped line would
// otherwise be baked into a PNG that ships as a permanent og:image.
const box = await page.locator("#card").boundingBox();
if (!box || Math.round(box.width) !== 1200 || Math.round(box.height) !== 630) {
  throw new Error(`#card is ${box && box.width}×${box && box.height}, expected 1200×630`);
}

await mkdir(dirname(out), { recursive: true });
await page.locator("#card").screenshot({ path: out });
await browser.close();

const { size } = await stat(out);
console.log(`wrote ${out} (${Math.round(size / 1024)} KB)`);
if (size > 300 * 1024) console.warn("over 300 KB — run it through pngquant before committing");
