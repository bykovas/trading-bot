// Generates public/assets/og-algo-byko-1200x630.png from og-algo-byko.html.
// Run by hand when the card changes — NOT part of the deploy pipeline.
//
//   npm i -D playwright && npx playwright install chromium
//   node tools/build-og-byko.mjs
//
// Before the first run, swap the Google Fonts <link> in og-algo-byko.html for the
// local @font-face block (see the comment at the top of that file) and put all
// five faces in tools/fonts/: Inter 600 (ALGO), Inter 500 (the hook), Archivo 700
// (the BYKO wordmark), JetBrains Mono 500 (domain + L&D FINANCE LAB) and
// Space Grotesk 700 (the BlynAI Capital wordmark). Miss one and headless Chromium
// silently substitutes a system face into a permanent og:image.

import { chromium } from "playwright";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";
import { mkdir, stat } from "node:fs/promises";

const here = dirname(fileURLToPath(import.meta.url));
const source = resolve(here, "og-algo-byko.html");
const out = resolve(here, "../public/assets/og-algo-byko-1200x630.png");

const browser = await chromium.launch();
const page = await browser.newPage({
  viewport: { width: 1200, height: 630 },
  deviceScaleFactor: 1,
});

await page.goto("file://" + source, { waitUntil: "load" });
await page.evaluate(() => document.fonts.ready);

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
