import json, os, sys
here = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, here)
from mixmap import MIX
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.t2CharStringPen import T2CharStringPen
from fontTools.pens.transformPen import TransformPen
from fontTools.svgLib.path import parse_path

outlines = json.load(open(os.path.join(here, 'outlines.json')))
names = ['.notdef'] + [f'u{cp:04X}' for cp in sorted(MIX)]
fb = FontBuilder(1000, isTTF=False)
fb.setupGlyphOrder(names)
fb.setupCharacterMap({cp: f'u{cp:04X}' for cp in MIX})
charstrings = {}
empty = T2CharStringPen(1000, None); charstrings['.notdef'] = empty.getCharString()
for cp, key in MIX.items():
    pen = T2CharStringPen(1000, None)
    parse_path(outlines[key], TransformPen(pen, (1, 0, 0, -1, 0, 1000)))   # y up; like Segoe Fluent the glyph sits on the baseline, the whole em above it
    charstrings[f'u{cp:04X}'] = pen.getCharString()
fb.setupCFF('IvoIcons', {'FullName': 'Ivo Icons'}, charstrings, {})
fb.setupHorizontalMetrics({n: (1000, 0) for n in names})
fb.setupHorizontalHeader(ascent=1000, descent=0)
fb.setupNameTable({'familyName': 'Ivo Icons', 'styleName': 'Regular'})
fb.setupOS2(sTypoAscender=1000, sTypoDescender=0, sTypoLineGap=0, usWinAscent=1000, usWinDescent=0)
fb.setupPost()
out = os.path.join(here, '..', '..', 'Assets', 'IvoIcons.otf')
fb.save(out)
print('saved', out, os.path.getsize(out), 'bytes', len(MIX), 'codepoints')


