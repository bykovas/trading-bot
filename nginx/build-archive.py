"""Build the isolated, read-only LUKO archive from the existing journal UI."""
from pathlib import Path
import re
import sys


def archive(source):
    def cut(pattern, replacement=''):
        nonlocal source
        source, n = re.subn(pattern, replacement, source, flags=re.S)
        if n != 1:
            raise ValueError(f'Journal structure changed ({n} matches): {pattern}')

    cut(r'<title>.*?</title>', '<title>LUKO — sustabdyto boto žurnalas</title>')
    for key, value in {
        'URL': 'https://blynai.meetluko.eu/', 'SITE': 'LUKO',
        'TITLE': 'LUKO — sustabdyto boto žurnalas',
        'DESCRIPTION': 'Botas sustabdytas. Išsaugotas istorinis LUKO prekybos žurnalas.',
        'TWITTER_CARD': 'summary',
    }.items():
        source = source.replace(f'__OG_{key}__', value)
    source = source.replace('<!--#include virtual="/og/tags" -->', '')
    cut(r'<div class="bc-meta">.*?</div>')
    cut(r'<div class="bc-tenant">.*?</div>', '<span class="bc-tenant-l">LUKO · archyvas</span>')
    cut(r'<aside class="bc-coins">.*?</aside>')
    cut(r'<span class="bc-seg">.*?</span>')
    cut(r'<div class="bc-more">.*?</div>')
    cut(r'<dialog class="bc-modal bc-modal--pages".*?</dialog>')
    cut(r'<div class="bc-cta-wrap">.*?</div>')
    cut(r'<div class="bc-heur">.*?<div class="bc-next bc-next--live">', '<div class="bc-next bc-next--live">')
    source = source.replace('L&amp;D Finance Lab<sup class="bc-fn-mark">1</sup>', 'LUKO · istorinis žurnalas')
    source = source.replace('Uždaras testavimas · tikri pinigai', 'Botas sustabdytas · istorinis žurnalas')
    cut(r'<h1 class="bc-h1">.*?</h1>', '<h1 class="bc-h1">LUKO botas sustabdytas.</h1>')
    cut(r'<p class="bc-lead">.*?</p>', '<p class="bc-lead">Čia išsaugotas sustabdyto boto žurnalas. Rodomi paskutiniai įrašyti duomenys, ne vykstanti prekyba.</p>')
    source = source.replace('Toliau — gyvas pulsas', 'Toliau — išsaugoti duomenys')
    source = source.replace('LUKAS</b><span>&#183; Portfelio vert&#279; dabar &#183; gyvai', 'LUKO</b><span>&#183; Paskutinė įrašyta portfelio vertė')
    source = source.replace('<h2>Aktyvios pozicijos</h2>', '<h2>Pozicijos paskutiniame įraše</h2>')
    source = source.replace('Svetainės <b>blynai.bykovas.lt</b> ir <b>blynai.meetluko.eu</b> skirtos', 'Svetainė <b>blynai.meetluko.eu</b> skirta')
    cut(r'    // Two tenants,.*?    let lastGood = null;', '''    // Archive identity is fixed: URL parameters and old browser preferences cannot switch accounts.
    const TENANTS = { LUKAS: { botInstanceId: "futures-lukas-live", label: "LUKO" } };
    const tenant = "LUKAS";
    let lastGood = null;''')
    cut(r'    // ---- tenant switching.*?    // ---- disclaimer', '''    function initTenant() {
      els.liveTenant.textContent = "LUKO";
      els.statusLine.addEventListener("click", () => {
        const open = document.body.classList.toggle("bc-status-open");
        els.statusLine.setAttribute("aria-expanded", open ? "true" : "false");
      });
    }

    // ---- disclaimer''')
    cut(r'    function renderRate\(rates\).*?\n    }\n')
    source = source.replace('      renderRate(data.rates);\n', '')
    source = source.replace('      initModal("bcPages", "bcPagesOpen", "bcPagesClose");\n', '')
    cut(r'    function renderStatus\(data\).*?\n    }\n', '''    function renderStatus(data) {
      els.statusChips.textContent = "LUKO · sustabdytas";
      els.statusLineText.textContent = "Botas sustabdytas";
      els.statusLineAge.textContent = "";
      els.mbText.textContent = "Sustabdytas";
      els.mbAge.textContent = "";
      [els.statusLineDot, els.mbDot, els.liveDot].forEach(dot => dot.className = "bc-dot bc-dot--down");
    }
''')
    # A retired worker has no changing state to poll. Reload explicitly to read stored history.
    source = source.replace('    setInterval(refresh, REFRESH_MS);', '')
    # Remove the unused second-token palette after removing its entire card/rate renderer.
    source = re.sub(r'--byko:[^;]+;', '', source)
    source = re.sub(r'^.*\.bc-coin--byko.*\n', '', source, flags=re.M)
    source = re.sub(r'^.*//.*futures-live.*\n', '', source, flags=re.M)
    if re.search(r'byko|puko|bykovas|denisas|Kiti svetainės puslapiai|L&amp;D', source, re.I):
        raise ValueError('Personal/other-bot reference remains in archive')
    return source


if __name__ == '__main__':
    Path(sys.argv[2]).write_text(archive(Path(sys.argv[1]).read_text()), encoding='utf-8')
