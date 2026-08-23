// Faces for the share card, embedded as data URIs.
//
// The renderer builds the page with setContent, so there is no document URL to
// resolve ./fonts/ against — and it must not reach fonts.googleapis.com either:
// a card is rendered while a crawler waits, and a face that arrives late or not
// at all is baked into the PNG as a system substitute or, where the claim uses
// background-clip:text, as nothing at all.
//
// TrueType rather than woff2 for the reason the original cards give: the Google
// Fonts CSS API serves woff2 only as unicode-range subsets, and the first subset
// a modern browser is handed is vietnamese — "uždirbam" rasterised as tofu.

import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));

const FACES = {
  anton: { file: "Anton-Regular.ttf", family: "Anton", weight: "400" },
  archivoLight: { file: "Archivo-ExtraLight.ttf", family: "Archivo", weight: "200" },
  archivoBold: { file: "Archivo-Bold.ttf", family: "Archivo", weight: "700" },
  dmMono: { file: "DMMono-Medium.ttf", family: "DM Mono", weight: "500" },
  inter: { file: "Inter-Medium.ttf", family: "Inter", weight: "500" },
  // Only SemiBold is vendored, and the claim asks for 700. Declared as a range
  // so Chromium uses this file for both rather than synthesising a fake bold —
  // a smeared outline is very visible under a gradient clipped to the text.
  interBold: { file: "Inter-SemiBold.ttf", family: "Inter", weight: "600 700" },
  jetBrains: { file: "JetBrainsMono-Medium.ttf", family: "JetBrains Mono", weight: "500" },
  grotesk: { file: "SpaceGrotesk-Bold.ttf", family: "Space Grotesk", weight: "700" }
};

const THEME_FACES = {
  luko: ["anton", "archivoLight", "dmMono", "grotesk"],
  byko: ["inter", "interBold", "archivoBold", "jetBrains", "grotesk"]
};

const cache = new Map();

async function faceCss(key) {
  const face = FACES[key];
  const bytes = await readFile(resolve(here, "fonts", face.file));
  return `@font-face{font-family:'${face.family}';` +
    `src:url(data:font/ttf;base64,${bytes.toString("base64")}) format('truetype');` +
    `font-weight:${face.weight};font-style:normal;font-display:block}`;
}

// Read once per process. A renderer that re-read a megabyte of TrueType on every
// crawler hit would spend more time on disk than on the picture.
export async function fontsFor(theme) {
  const key = THEME_FACES[theme] ? theme : "luko";
  if (!cache.has(key)) {
    cache.set(key, (await Promise.all(THEME_FACES[key].map(faceCss))).join(""));
  }
  return cache.get(key);
}

export function faceNames(theme) {
  return (THEME_FACES[theme] || THEME_FACES.luko).map(key => FACES[key].file);
}
