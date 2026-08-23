// Writes the committed fallback cards to public/assets/.
//
// The live card is rendered per request by tools/og-server.mjs. These two PNGs
// are what it serves when it has never managed to render one itself — a cold
// container with the API down, which is precisely when a crawler must still get
// a picture rather than a 404. They carry no figures, because a figure baked in
// months ago is worse than none: the number block is left out entirely, which is
// the same thing the live card does for an account with no closed days.
//
//   npm i -D playwright && npx playwright install chromium
//   node tools/build-og.mjs
//
// Fonts are embedded from tools/fonts/ by og-fonts.mjs, so this never touches
// the network. Run it when the card design changes, and commit both PNGs.

import { chromium } from "playwright";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";
import { mkdir, stat } from "node:fs/promises";

import { cardHtml, THEMES } from "./og-card.mjs";
import { fontsFor } from "./og-fonts.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const OUT = {
  luko: resolve(here, "../public/assets/og-algo-1200x630.png"),
  byko: resolve(here, "../public/assets/og-algo-byko-1200x630.png")
};
const DOMAIN = { luko: "blynai.meetluko.eu", byko: "blynai.bykovas.lt" };

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1200, height: 630 }, deviceScaleFactor: 1 });

for (const theme of THEMES) {
  const fontsCss = await fontsFor(theme);
  await page.setContent(cardHtml({ theme, fontsCss, values: { domain: DOMAIN[theme], stat: null } }),
    { waitUntil: "load" });
  await page.evaluate(() => document.fonts.ready);

  // Exactly 1200x630 — a stray margin or a wrapped line would otherwise be baked
  // into a PNG that ships as the permanent fallback.
  const box = await page.locator("#card").boundingBox();
  if (!box || Math.round(box.width) !== 1200 || Math.round(box.height) !== 630) {
    throw new Error(`${theme}: card is ${box && box.width}x${box && box.height}, expected 1200x630`);
  }

  await mkdir(dirname(OUT[theme]), { recursive: true });
  await page.locator("#card").screenshot({ path: OUT[theme] });
  const { size } = await stat(OUT[theme]);
  console.log(`wrote ${OUT[theme]} (${Math.round(size / 1024)} KB)`);
  if (size > 300 * 1024) console.warn("over 300 KB — run it through pngquant before committing");
}

await browser.close();
