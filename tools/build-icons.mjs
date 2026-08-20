// Rasterises public/assets/favicon.svg into the PNG sizes browsers ask for, plus
// a multi-size favicon.ico for the bare /favicon.ico request.
// Run by hand when the mark changes — NOT part of the deploy pipeline.
//
//   node tools/build-icons.mjs
//
// Kept separate from build-og.mjs so that file stays exactly as handed off.

import { chromium } from "playwright";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";
import { readFile, writeFile } from "node:fs/promises";

const here = dirname(fileURLToPath(import.meta.url));
const svgPath = resolve(here, "../public/assets/favicon.svg");
const outDir = resolve(here, "../public/assets");

const svg = await readFile(svgPath, "utf8");
const sizes = [16, 32, 48, 180, 512];

const browser = await chromium.launch();
const page = await browser.newPage({ deviceScaleFactor: 1 });

for (const size of sizes) {
  await page.setViewportSize({ width: size, height: size });
  // omitBackground keeps the rounded corners transparent instead of white.
  await page.setContent(
    `<html><body style="margin:0">
       <div id="i" style="width:${size}px;height:${size}px">${svg}</div>
     </body></html>`,
    { waitUntil: "load" });
  await page.locator("#i svg").evaluate((node, s) => {
    node.setAttribute("width", s);
    node.setAttribute("height", s);
  }, size);

  const name = size === 180 ? "apple-touch-icon.png" : `favicon-${size}.png`;
  await page.locator("#i").screenshot({ path: resolve(outDir, name), omitBackground: true });
  console.log(`wrote ${name}`);
}

await browser.close();
