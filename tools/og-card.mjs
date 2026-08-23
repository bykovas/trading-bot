// --------------------------------------------------------------------
// The share card, both themes, as one template.
//
// og-algo.html and og-algo-byko.html were two standalone files photographed
// by two build scripts. The numbers on them are now live, which means the
// markup that prints a number is written once or the two cards start saying
// different things about the same arithmetic — so this is a function, not a
// pair of files with placeholders in them.
//
// Everything that does not depend on the numbers is carried over unchanged
// from those two files: the coin, the grid, the mark, the band, the claim.
// --------------------------------------------------------------------

// The BlynAI mark. Same paths in both themes and at every size — only the
// viewport around it changes, so it is written once.
const MARK = `<svg viewBox="0 0 64 64" aria-hidden="true" style="display:block;width:100%;height:100%">` +
  `<rect x="1.5" y="1.5" width="61" height="61" rx="17" fill="#0A0A0A" stroke="#C9A86A" stroke-opacity=".55" stroke-width="1.6"></rect>` +
  `<g fill="none" stroke="#E8B84B" stroke-width="3.2" stroke-linecap="round" stroke-linejoin="round">` +
  `<path d="M23 22 15.5 32 23 42"></path><path d="M41 22 48.5 32 41 42"></path></g>` +
  `<g fill="#E8B84B"><circle cx="27.5" cy="27.5" r="2.5"></circle><circle cx="36.5" cy="27.5" r="2.5"></circle>` +
  `<circle cx="27.5" cy="36.5" r="2.5"></circle><circle cx="36.5" cy="36.5" r="2.5"></circle></g></svg>`;

// The mark used bare inside a tile: the tile draws its own plate, so the
// rounded rectangle of the logo would be a second frame inside the first.
const MARK_BARE = `<svg viewBox="8 8 48 48" aria-hidden="true" style="display:block;width:100%;height:100%">` +
  `<g fill="none" stroke="#E8B84B" stroke-width="3.2" stroke-linecap="round" stroke-linejoin="round">` +
  `<path d="M23 22 15.5 32 23 42"></path><path d="M41 22 48.5 32 41 42"></path></g>` +
  `<g fill="#E8B84B"><circle cx="27.5" cy="27.5" r="2.5"></circle><circle cx="36.5" cy="27.5" r="2.5"></circle>` +
  `<circle cx="27.5" cy="36.5" r="2.5"></circle><circle cx="36.5" cy="36.5" r="2.5"></circle></g></svg>`;

// LUKO's disc, unchanged from og-algo.html.
const LUKO_DISC = `<svg viewBox="137 118 242 242" aria-hidden="true">` +
  `<g opacity=".3"><path d="M144 164L182 122V167Z" fill="#C9A86A"></path>` +
  `<g fill="none" stroke="#C9A86A" stroke-width="15"><circle cx="306" cy="186" r="58"></circle><path d="M364 186V312"></path></g></g>` +
  `<path d="M182 122h52V312H346l16 16V358H182Z" fill="#C9A86A"></path></svg>`;

// BYKO's coin, unchanged from og-algo-byko.html.
const BYKO_COIN = `<svg viewBox="0 0 32 32" role="img" aria-label="BYKO" style="display:block;width:100%;height:100%">` +
  `<circle cx="16" cy="16" r="16" fill="#7CCBFF"></circle>` +
  `<g transform="translate(2.1875 1.25) scale(.3125)" fill="none" stroke="#0B0D10" stroke-width="13" stroke-linecap="butt" stroke-linejoin="miter">` +
  `<path d="M24 14V82"></path><path d="M40 20L70 48 40 76"></path></g></svg>`;

function esc(value) {
  return String(value == null ? "" : value)
    .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}

// The one row of numbers, written once for both themes. Only the colours
// differ, and those arrive as CSS classes the theme defines.
function statBlock(stat) {
  if (!stat) return "";
  const trades = stat.trades
    ? ` <i class="trades">(${esc(stat.trades)})</i>`
    : "";
  return `<div class="stat">` +
    `<span class="stat-l">${esc(stat.label)}</span>` +
    `<span class="stat-n"><i class="from">Iš ${esc(stat.from)}</i> <i class="x">&#8594;</i> ` +
    `<i class="to">${esc(stat.to)}</i> <i class="x">&#183;</i> <i class="gain">${esc(stat.gain)}</i> ` +
    `<i class="x">&#183;</i> <i class="pct">${esc(stat.pct)}</i>${trades}</span>` +
    `</div>`;
}

// ---- LUKO -----------------------------------------------------------
// Apricot, dotted, magenta band. ALGO in 280px Anton is gone: the mark that
// used to be a credit in the corner is the subject now, and the numbers stand
// under it. Everything else is og-algo.html byte for byte.
function lukoCss(hasStat) {
  return `
  html,body{margin:0;padding:0;background:#FFDFAF}
  #card{width:1200px;height:630px;box-sizing:border-box;overflow:hidden;
    background:#FFDFAF radial-gradient(#00000018 3px,transparent 3px) 0 0/12px 12px;
    color:#1E1408;display:flex;flex-direction:column;justify-content:space-between;
    font-family:Archivo,'Helvetica Neue',Helvetica,Arial,sans-serif}
  /* flex-start, not center: the top of the coin sits on the top of the plate.
     Centred, the plate floated against a disc twice its height. */
  .top{flex:1;display:flex;align-items:flex-start;justify-content:space-between;gap:40px;padding:32px 72px 24px;min-height:0}
  .lead{display:flex;flex-direction:column;align-items:flex-start;gap:34px;min-width:0}
  /* The mark and the wordmark keep their own colours, so they ride on an ink
     plate rather than on apricot — the plate reads as an inserted badge and
     neither brand has to be repainted. */
  .badge{display:flex;align-items:center;gap:32px;padding:29px 35px 27px;background:#0A1F36;
    border:1px solid rgba(111,174,255,.35);border-radius:32px}
  .badge-tile{flex:none;display:flex;align-items:center;justify-content:center;
    width:${hasStat ? 128 : 150}px;height:${hasStat ? 128 : 150}px;border-radius:29px;background:rgba(0,0,0,.3);
    border:1px solid rgba(111,174,255,.4);box-shadow:0 0 60px rgba(111,174,255,.14)}
  .badge-tile span{display:block;width:${hasStat ? 99 : 116}px;height:${hasStat ? 99 : 116}px}
  .badge-txt{display:flex;flex-direction:column;gap:11px;min-width:0}
  .badge-name{font-family:'Space Grotesk',Archivo,Helvetica,Arial,sans-serif;font-weight:700;
    font-size:56px;line-height:1;letter-spacing:-.01em;color:#EAF3FF;white-space:nowrap}
  .badge-name em{font-style:normal;color:#E8B84B}
  .badge-sub{font-family:'DM Mono',ui-monospace,Menlo,monospace;font-weight:500;font-size:23px;
    line-height:1;letter-spacing:.22em;color:#7CCBFF;white-space:nowrap}
  .stat{display:flex;flex-direction:column;gap:12px;min-width:0}
  .stat-l{font-family:'DM Mono',ui-monospace,Menlo,monospace;font-weight:500;font-size:22px;
    line-height:1;letter-spacing:.2em;color:#8A7454;white-space:nowrap}
  /* One line. Two lines would push the plate up and the coin would be cropped;
     if the figures ever outgrow the width, the type comes down, not the coin. */
  .stat-n{font-family:'Space Grotesk',Archivo,Helvetica,Arial,sans-serif;font-weight:700;
    font-size:30px;line-height:1.1;white-space:nowrap}
  .stat-n i{font-style:normal}
  .from{color:#A97213}
  .to{color:#0C7A45}
  .gain{color:#493721}
  .x,.pct{color:#8A7454}
  .trades{font-family:'DM Mono',ui-monospace,Menlo,monospace;font-weight:500;font-size:18px;color:#8A7454}
  /* A losing best day is still the best day. It says so in red rather than
     being swapped for a friendlier one. */
  .is-down .to,.is-down .gain{color:#A4162A}
  .brand{flex:none;display:flex;flex-direction:column;align-items:center;gap:20px}
  .disc{flex:none;width:270px;height:270px;display:flex;align-items:center;justify-content:center;background:#1E1408;border-radius:50%}
  .disc svg{display:block;width:155px;height:155px}
  .wordmark{font-family:Archivo,'Helvetica Neue',Helvetica,Arial,sans-serif;font-size:52px;font-weight:200;
    line-height:1;letter-spacing:.2em;padding-left:.2em;color:#1E1408}
  .band{flex:none;background:#B0007F;color:#FFDFAF;border-top:6px solid #1E1408;padding:30px 72px;
    display:flex;align-items:flex-end;justify-content:space-between;gap:32px}
  .claim{font-family:Anton,'Arial Narrow',sans-serif;font-size:52px;line-height:1.1}
  .claim em{font-style:normal;color:#8BFF6B}
  .domain{font-family:'DM Mono',ui-monospace,Menlo,monospace;font-weight:500;font-size:30px;
    line-height:1;letter-spacing:.04em;white-space:nowrap}`;
}

function lukoBody(values) {
  return `<div id="card"${values.stat && values.stat.down ? ' class="is-down"' : ""}>
  <div class="top">
    <div class="lead">
      <div class="badge">
        <span class="badge-tile"><span>${MARK_BARE}</span></span>
        <span class="badge-txt">
          <span class="badge-name">Blyn<em>AI</em> Capital</span>
          <span class="badge-sub">L&amp;D FINANCE LAB</span>
        </span>
      </div>
      ${statBlock(values.stat)}
    </div>
    <div class="brand">
      <div class="disc">${LUKO_DISC}</div>
      <div class="wordmark">LUKO</div>
    </div>
  </div>
  <div class="band">
    <div class="claim">Botas prekiauja. <em>Mes uždirbam.</em> Kol kas.</div>
    <div class="domain">${esc(values.domain)}</div>
  </div>
</div>`;
}

// ---- BYKO -----------------------------------------------------------
// Dark, gridded, no band. Same move as LUKO: ALGO in 250px Inter is gone and
// the mark takes its place, at the scale the original badge had — every ratio
// of it kept, multiplied by 3.71.
function bykoCss(hasStat) {
  return `
  html,body{margin:0;padding:0;background:#0B0D10}
  #card{width:1200px;height:630px;box-sizing:border-box;overflow:hidden;position:relative;
    background:#0B0D10;color:#E8EAEE;font-family:Inter,system-ui,Helvetica,Arial,sans-serif;
    display:flex;flex-direction:column;justify-content:space-between;padding:40px 64px}
  .grid{position:absolute;inset:0;
    background-image:linear-gradient(rgba(255,255,255,.05) 1px,transparent 1px),linear-gradient(90deg,rgba(255,255,255,.05) 1px,transparent 1px);
    background-size:60px 60px;
    -webkit-mask-image:radial-gradient(circle at 50% 0%,#000 0%,transparent 72%);
    mask-image:radial-gradient(circle at 50% 0%,#000 0%,transparent 72%)}
  .row{position:relative;display:flex}
  .row-top{justify-content:flex-end}
  .domain{font-family:'JetBrains Mono',ui-monospace,Menlo,monospace;font-weight:500;font-size:28px;
    line-height:1;letter-spacing:.04em;color:#7CCBFF;white-space:nowrap}
  .row-main{align-items:center;justify-content:space-between;gap:44px}
  .coin{flex:none;display:flex;flex-direction:column;align-items:center;gap:16px}
  .coin-disc{width:196px;height:196px}
  .coin-name{font-family:Archivo,system-ui,sans-serif;font-weight:700;font-size:30px;line-height:1;
    letter-spacing:.2em;padding-left:.2em;color:#E8EAEE}
  .right{display:flex;flex-direction:column;align-items:flex-start;gap:20px;min-width:0}
  /* Proportional to the original 42px badge: tile = 2x the name, inner padding
     = tile x .107, radius = tile x .286, gap = name x .57, sub = name / 2. */
  .lockup{display:flex;align-items:center;gap:44px}
  .lockup-tile{flex:none;display:flex;align-items:center;justify-content:center;
    width:156px;height:156px;padding:17px;box-sizing:border-box;border-radius:45px;
    background:rgba(0,0,0,.28);border:1px solid rgba(245,184,79,.28);
    box-shadow:inset 0 1px 0 rgba(255,255,255,.04),0 0 22px rgba(245,184,79,.14)}
  .lockup-tile span{display:block;width:122px;height:122px}
  .lockup-txt{display:flex;flex-direction:column;gap:15px;min-width:0}
  .lockup-name{font-family:'Space Grotesk',Inter,sans-serif;font-weight:700;font-size:78px;
    line-height:.92;letter-spacing:-.03em;color:#F6EFE1;white-space:nowrap}
  .lockup-name em{font-style:normal;color:#E8B84B}
  .lockup-sub{font-family:'JetBrains Mono',ui-monospace,Menlo,monospace;font-weight:500;font-size:39px;
    line-height:1;letter-spacing:.22em;color:#9B8FD8;white-space:nowrap}
  .stat{display:flex;flex-direction:column;gap:10px;min-width:0}
  .stat-l{font-family:'JetBrains Mono',ui-monospace,Menlo,monospace;font-weight:500;font-size:19px;
    line-height:1;letter-spacing:.2em;color:#7C8794;white-space:nowrap}
  .stat-n{font-family:'Space Grotesk',Inter,sans-serif;font-weight:700;font-size:28px;line-height:1.15;
    white-space:nowrap}
  .stat-n i{font-style:normal}
  /* Inverted against the apricot card, not copied from it: on #0B0D10 the
     LUKO hexes are unreadable. */
  .from{color:#E8B84B}
  .to{color:#41EDA0}
  .gain{color:#C6CDD6}
  .x,.pct{color:#7C8794}
  .trades{font-family:'JetBrains Mono',ui-monospace,Menlo,monospace;font-weight:500;font-size:17px;color:#7C8794}
  .is-down .to,.is-down .gain{color:#FF6B7A}
  .band{position:relative;border-top:2px solid rgba(255,255,255,.1);padding-top:26px}
  .claim{font-size:40px;line-height:1.15;font-weight:500;color:#AEB6BF;text-wrap:pretty}
  /* background-clip:text has no fallback in a PNG: an unloaded face renders
     nothing at all rather than something black. document.fonts.ready before
     the screenshot is what keeps this line from vanishing. */
  .claim em{font-style:normal;font-weight:700;
    background:linear-gradient(90deg,#7CCBFF,#FFB088);-webkit-background-clip:text;background-clip:text;
    color:transparent}
  ${hasStat ? "" : ".right{justify-content:center}"}`;
}

function bykoBody(values) {
  return `<div id="card"${values.stat && values.stat.down ? ' class="is-down"' : ""}>
  <div class="grid"></div>
  <div class="row row-top"><span class="domain">${esc(values.domain)}</span></div>
  <div class="row row-main">
    <div class="coin">
      <span class="coin-disc">${BYKO_COIN}</span>
      <span class="coin-name">BYKO</span>
    </div>
    <div class="right">
      <div class="lockup">
        <span class="lockup-tile"><span>${MARK_BARE}</span></span>
        <span class="lockup-txt">
          <span class="lockup-name">Blyn<em>AI</em> Capital</span>
          <span class="lockup-sub">L&amp;D FINANCE LAB</span>
        </span>
      </div>
      ${statBlock(values.stat)}
    </div>
  </div>
  <div class="band">
    <span class="claim">Botas prekiauja. <em>Mes uždirbam.</em> Kol kas.</span>
  </div>
</div>`;
}

export const THEMES = ["luko", "byko"];

// Split from the body on purpose. The renderer keeps one page alive per theme
// with the fonts already parsed and swaps only these two on each request: a
// megabyte of embedded TrueType re-read for every crawler is most of the time a
// card takes to draw, and none of it is drawing.
export function cardCss(theme, hasStat) {
  return theme === "byko" ? bykoCss(hasStat) : lukoCss(hasStat);
}

export function cardBody(theme, values) {
  return theme === "byko" ? bykoBody(values) : lukoBody(values);
}

// fontsCss is the @font-face block with the faces embedded as data URIs. It is
// passed in rather than read here so the files are read once per process.
export function cardHtml({ theme = "luko", fontsCss = "", values = {} }) {
  const hasStat = !!values.stat;
  return `<!doctype html>
<html lang="lt">
<head>
<meta charset="utf-8">
<title>${theme === "byko" ? "BYKO" : "LUKO"} — og:image 1200&#215;630</title>
<style>${fontsCss}</style>
<style id="bc-theme">${cardCss(theme, hasStat)}</style>
</head>
<body><div id="bc-root">${cardBody(theme, values)}</div></body>
</html>`;
}
