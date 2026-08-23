# Builds public/aktas/index.html from the exported document bundle.
#
#   python3 tools/build-aktas.py ~/Downloads/blynai-aktas.html
#
# The export is a self-unpacking bundle: one JSON-encoded template inside a
# <script>, with the fonts and assets beside it. Everything this script adds
# goes INTO that template, because the wrapper replaces the whole document
# element as it unpacks - anything left outside would vanish a second after it
# appeared.
#
# It adds nothing to the document's own styling. That was tried once, scaling
# the sheet to fit a phone, and iOS answered by inflating the font sizes on its
# own against line-heights that do not move. The author's typography is the
# author's; this script only changes words, and adds a title, a download button
# and four links.
#
# Re-run it whenever a fresh export arrives; the edits below are re-applied.

import json
import re
import sys

SOURCE = sys.argv[1] if len(sys.argv) > 1 else "/Users/bykovas/Downloads/blynai-aktas.html"
TARGET = "public/aktas/index.html"

TITLE = "BlynAI steigėjų ketinimų aktas · BlynAI Capital"

# The document calls itself an internal document and is not signed. Reachable by
# the link from the dashboard, not collected by search engines.
HEAD = ('<title>%s</title>\n'
        '<meta name="robots" content="noindex">\n') % TITLE

BUTTON = '''<a class="bc-pdf" href="blynai-aktas.pdf" download>Atsisiųsti PDF</a>
<style>
/* The document reads the same on paper and on screen; this is the one thing on
   it that is neither, so it floats over the page and leaves the print alone. */
.bc-pdf{position:fixed;top:16px;right:16px;z-index:50;display:inline-flex;align-items:center;
  height:36px;padding:0 15px;border:1px solid rgba(138,90,18,.32);border-radius:999px;
  background:rgba(250,245,234,.94);-webkit-backdrop-filter:blur(6px);backdrop-filter:blur(6px);
  font:500 12.5px/1 'IBM Plex Mono',ui-monospace,monospace;letter-spacing:.06em;
  color:#8A5A12;text-decoration:none;box-shadow:0 2px 12px rgba(0,0,0,.1)}
.bc-pdf:hover{color:#C2871C;border-color:rgba(194,135,28,.5);background:#FAF5EA}
@media (max-width:640px){.bc-pdf{top:10px;right:10px;height:32px;padding:0 12px;font-size:11.5px}}
@media print{.bc-pdf{display:none}}
</style>
'''

# Signed as exported, the document would have been a founding contract missing
# what the law requires one to contain: 7.1 made it one on two signatures while
# 3.2 and the signature page left the contribution and the personal data for
# later. These edits make it an act of intent and leave the substance alone -
# 50/50, Lukas as vadovas, the joint-decision list, the profit split, 6.1.
EDITS = [
    ('>Mažosios bendrijos steigimo sutartis</p>', '>Steigėjų ketinimų aktas</p>'),
    ('>MB „BlynAI“<br>steigimo sutarties projektas</h1>',
     '>Blyn<em style="font-style:normal;color:#8A5A12">AI</em><br>steigėjų ketinimų aktas</h1>'),

    # The stamp marked a draft. All it has to say now is that nobody has signed yet.
    ('<span style="font:700 22px/1 \'Space Grotesk\',sans-serif;letter-spacing:.14em;color:#B0007F">PROJEKTAS</span>',
     '<span style="font:700 16px/1 \'Space Grotesk\',sans-serif;letter-spacing:.14em;color:#B0007F">NEPASIRAŠYTA</span>'),
    ('<span style="font:500 9px \'IBM Plex Mono\',monospace;letter-spacing:.18em;color:#B0007F">NEPASIRAŠYTA</span>',
     '<span style="font:500 9px \'IBM Plex Mono\',monospace;letter-spacing:.18em;color:#B0007F">KETINIMŲ AKTAS</span>'),

    ('Statusas: <b style="color:#1E1408">PROJEKTAS</b>. Šis dokumentas netampa sutartimi, kol jo nepasirašo abu steigėjai ir bendrija neįregistruojama Juridinių asmenų registre.',
     '<b style="color:#1E1408">Šis dokumentas patvirtina steigėjų ketinimus</b>, tačiau nėra mažosios bendrijos steigimo sutartis ir savaime nesukuria mažosios bendrijos, narystės joje ar teisės veikti būsimos bendrijos vardu. Mažoji bendrija steigiama atskira steigimo sutartimi ir įregistruojama Juridinių asmenų registre.'),

    ('Šis projektas tampa steigimo sutartimi nuo abiejų steigėjų parašų.',
     'Šis aktas įsigalioja nuo abiejų steigėjų parašų ir patvirtina jų ketinimus. Jis nėra mažosios bendrijos steigimo sutartis ir savaime nesukuria mažosios bendrijos, narystės joje ar teisės veikti būsimos bendrijos vardu.'),
    ('Bendrija laikoma įsteigta nuo įregistravimo Juridinių asmenų registre.',
     'Mažoji bendrija steigiama atskira steigimo sutartimi ir laikoma įsteigta nuo įregistravimo Juridinių asmenų registre.'),

    ('Asmens kodai ir adresai nurodomi pasirašant. Iki tol dokumentas lieka projektu.',
     'Asmens kodai ir adresai šiam aktui nereikalingi — jie nurodomi sudarant steigimo sutartį.'),

    ('<span>MB „BLYNAI“ · STEIGIMO SUTARTIES PROJEKTAS</span>', '<span>BLYNAI · STEIGĖJŲ KETINIMŲ AKTAS</span>'),
    ('<span>PROJEKTAS · NESUKURIA TEISINIŲ PAREIGŲ</span>', '<span>KETINIMŲ AKTAS · NE STEIGIMO SUTARTIS</span>'),
    ('<span>MB „BLYNAI“ · PROJEKTAS Nr. BC‑2026‑001</span>', '<span>BLYNAI · KETINIMŲ AKTAS Nr. BC‑2026‑001</span>'),
    ('<span>MB „BLYNAI“ · PRIEDAS Nr. 1</span>', '<span>BLYNAI · PRIEDAS Nr. 1</span>'),

    ('šiuo projektu', 'šiuo aktu'),
    ('Priedas yra planavimo dokumentas, o ne šios sutarties dalis',
     'Priedas yra planavimo dokumentas, o ne šio akto dalis'),
    ('Planavimo dokumentas; nėra steigimo sutarties dalis, teisiškai narių neįpareigoja.',
     'Planavimo dokumentas; nėra šio akto dalis, teisiškai steigėjų neįpareigoja.'),

    # The deed lives on meetluko.eu, and algo.* is not a host that serves anything now.
    ('algo.meetluko.eu/deed', 'meetluko.eu/deed'),
]

# The owner badges become links. Each is a span carrying its own inline style;
# the tone in the border tells the two apart.
BADGE = re.compile(r'<span style="(display:inline-flex[^"]*rgba\((201,168,106|124,203,255),\.55\)[^"]*border-radius:999px[^"]*)">')
HREF = {'201,168,106': 'https://meetluko.eu', '124,203,255': 'https://byko.bykovas.lt'}


def matching_close(html, open_end):
    """Index of the </span> closing the span whose opening tag ends at open_end."""
    depth, i = 1, open_end
    while depth:
        nxt = re.search(r'<span\b|</span>', html[i:])
        if not nxt:
            raise SystemExit('unbalanced span in the export')
        i += nxt.end()
        depth += 1 if nxt.group(0) != '</span>' else -1
    return i - len('</span>')


def main():
    raw = open(SOURCE, encoding='utf-8').read()
    block = re.search(r'(<script type="__bundler/template">)(.*?)(</script>)', raw, re.S)
    if not block:
        raise SystemExit('no bundler template in %s - is it the exported bundle?' % SOURCE)
    tpl = json.loads(block.group(2).strip())

    for old, new in EDITS:
        if old not in tpl:
            raise SystemExit('NOT FOUND in the export: %r' % old[:80])
        tpl = tpl.replace(old, new)

    linked = 0
    while True:
        hit = BADGE.search(tpl)
        if not hit:
            break
        style, tone = hit.group(1), hit.group(2)
        end = matching_close(tpl, hit.end())
        tpl = (tpl[:hit.start()]
               + '<a href="%s" style="%s;text-decoration:none;color:inherit">' % (HREF[tone], style)
               + tpl[hit.end():end] + '</a>' + tpl[end + len('</span>'):])
        linked += 1

    head = re.search(r'<head[^>]*>', tpl, re.I)
    tpl = tpl[:head.end()] + '\n' + HEAD + tpl[head.end():]
    body = re.search(r'<body[^>]*>', tpl, re.I)
    tpl = tpl[:body.end()] + '\n' + BUTTON + tpl[body.end():]

    # JSON.parse reads this out of a <script> body, so no "</" may survive in it:
    # \/ is a valid JSON escape and keeps the parser from ending the element early.
    encoded = json.dumps(tpl, ensure_ascii=False).replace('</', r'<\/')
    out = raw[:block.start(2)] + encoded + raw[block.end(2):]
    out = out.replace('<title>Bundled Page</title>', '<title>%s</title>' % TITLE)

    open(TARGET, 'w', encoding='utf-8').write(out)
    print('%s <- %s' % (TARGET, SOURCE))
    print('  %d wording edits, %d badges linked' % (len(EDITS), linked))


main()
