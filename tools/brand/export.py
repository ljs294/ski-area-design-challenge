"""Export the Twin Peaks brand (task P2-03.1): outlined lockups and marks as SVG.

Writes
  docs/brand/masters/*.svg                                   ink (#1f1c19) masters for print, the store and task 10's icon
  Assets/MountainPlanner/Art/UI/Brand/Resources/Brand/*.svg  white single-fill layers the game tints from USS

The wordmarks are set in Overpass (tools/brand/fonts, OFL) at 900 for the game and 800 for the studio, outlined with
fontTools: the variable font is instanced at the weight, glyphs are placed with the font's own pair kerning and the
board's -0.012 em tracking, and every A is replaced by a peak (twinpeaks.peak_a), or in the Alpine Labs lockup by the
third peak rising behind the mark (twinpeaks.third_peak_a). Needs fontTools and shapely.
Usage: python tools/brand/export.py
"""
import os
import sys

from fontTools.pens.recordingPen import RecordingPen
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.ttLib import TTFont
from fontTools.varLib import instancer

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import twinpeaks as tp  # noqa: E402

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
FONT = os.path.join(ROOT, 'tools', 'brand', 'fonts', 'Overpass[wght].ttf')
MASTERS = os.path.join(ROOT, 'docs', 'brand', 'masters')
RUNTIME = os.path.join(ROOT, 'Assets', 'MountainPlanner', 'Art', 'UI', 'Brand', 'Resources', 'Brand')
INK, WHITE = '#1f1c19', '#ffffff'
TRACK = -0.012          # em, as the board set it
GAP = 3.5               # mark to wordmark, in grid units


class Face:
    """One weight of Overpass: outlines in font units, advances and pair kerning."""

    def __init__(self, weight):
        font = instancer.instantiateVariableFont(TTFont(FONT), {'wght': weight})
        self.font, self.upm = font, font['head'].unitsPerEm
        self.cap = font['OS/2'].sCapHeight / self.upm
        self.cmap, self.glyphs, self.hmtx = font.getBestCmap(), font.getGlyphSet(), font['hmtx']
        self.pairs, self.classes = {}, []
        gpos = font['GPOS'].table
        lookups = set()
        for fr in gpos.FeatureList.FeatureRecord:
            if fr.FeatureTag == 'kern':
                lookups.update(fr.Feature.LookupListIndex)
        for li in sorted(lookups):
            lk = gpos.LookupList.Lookup[li]
            for st in lk.SubTable:
                if lk.LookupType == 9:
                    if st.ExtensionLookupType != 2:
                        continue
                    st = st.ExtSubTable
                elif lk.LookupType != 2:
                    continue
                cov = st.Coverage.glyphs
                if st.Format == 1:
                    for first, ps in zip(cov, st.PairSet):
                        for r in ps.PairValueRecord:
                            v = getattr(r.Value1, 'XAdvance', 0) if r.Value1 else 0
                            self.pairs.setdefault((first, r.SecondGlyph), v)
                else:
                    c1, c2 = st.ClassDef1.classDefs, st.ClassDef2.classDefs
                    self.classes.append((set(cov), c1, c2, st.Class1Record))

    def glyph(self, ch):
        return self.cmap[ord(ch)]

    def kern(self, a, b):
        if (a, b) in self.pairs:
            return self.pairs[(a, b)]
        for cov, c1, c2, rec in self.classes:
            if a in cov:
                v = rec[c1.get(a, 0)].Class2Record[c2.get(b, 0)].Value1
                x = getattr(v, 'XAdvance', 0) if v else 0
                if x:
                    return x
        return 0

    def outline(self, name, x, base, scale):
        """SVG path data of one glyph, placed with its origin at (x, base), font units scaled by `scale` (y flipped)."""
        rec = RecordingPen()
        self.glyphs[name].draw(rec)
        pen = SVGPathPen(self.glyphs, ntos=lambda v: f'{round(v, 3):g}')
        rec.replay(TransformPen(pen, (scale, 0, 0, -scale, x, base)))
        return pen.getCommands()


def set_line(face, text, x0, base, cap, a_stem):
    """Set one wordmark line. `{A}` is drawn as the peak A. Returns (path data, peak-A shapes, right edge)."""
    em = cap / face.cap
    scale = em / face.upm
    x, parts, shapes, prev = x0, [], [], None
    tokens = []
    i = 0
    while i < len(text):
        if text.startswith('{A}', i):
            tokens.append('{A}'); i += 3
        else:
            tokens.append(text[i]); i += 1
    for t in tokens:
        if t == '{A}':
            if prev == ' ':
                x -= 0.020 * em                       # kern space to A
            x += 0.010 * em
            a, w = tp.peak_a(x, base, cap, cap * a_stem)
            shapes.append(a)
            x += w + 0.002 * em
            prev = 'A'
            continue
        g = face.glyph(t)
        if prev and prev != 'A':
            x += face.kern(face.glyph(prev), g) * scale
        if t != ' ':
            parts.append(face.outline(g, x, base, scale))
        x += face.hmtx[g][0] * scale + TRACK * em
        prev = t
    return ' '.join(parts), shapes, x


def svg(paths, fill, box):
    x0, y0, x1, y1 = box
    body = ''.join(f'<path fill="{fill}" d="{d}"/>' for d in paths if d)
    w, h = round(x1 - x0, 3), round(y1 - y0, 3)
    # width and height as well as the viewBox: without them Unity's importer sizes the vector image as a square
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{w:g}" height="{h:g}" viewBox="{x0:g} {y0:g} {w:g} {h:g}">'
            f'{body}</svg>\n')


def write(name, paths, box, runtime=False):
    os.makedirs(MASTERS, exist_ok=True)
    with open(os.path.join(MASTERS, name + '.svg'), 'w', encoding='utf-8', newline='\n') as f:
        f.write(svg(paths, INK, box))
    if runtime:
        os.makedirs(RUNTIME, exist_ok=True)
        with open(os.path.join(RUNTIME, name + '.svg'), 'w', encoding='utf-8', newline='\n') as f:
            f.write(svg(paths, WHITE, box))


def main():
    black, extra = Face(900), Face(800)
    plain, game = tp.peaks(1.0), tp.game_mark()
    small = tp.game_mark(line=1.25, trail=1.25)

    # Marks on the 24 grid: the master, the 32-64 px cut, the plain pair (studio, and the 24 px icon).
    full = (0, 0, 24, 24)
    write('game-mark', [tp.to_path(game)], full, runtime=True)
    write('game-mark-small', [tp.to_path(small)], full)
    write('studio-mark', [tp.to_path(plain)], full, runtime=True)
    write('icon-24', [tp.to_path(tp.peaks(1.5))], full)
    write('icon-16', [tp.to_path(tp.peaks(1.5, grid=16.0))], (0, 0, 16, 16))

    # The game's lockup: the mark, then "Ski Area" over "Design Challenge", the A's point on the back summit and the
    # second line's baseline on the ground line.
    x0 = 24 + GAP
    l1, a1, r1 = set_line(black, 'Ski {A}rea', x0, 10.11, 6.9, 0.218)
    l2, a2, r2 = set_line(black, 'Design Challenge', x0, 21.0, 6.9, 0.218)
    right = max(r1, r2) + 0.5
    write('game-lockup', [tp.to_path(game), l1, l2] + [tp.to_path(a) for a in a1 + a2], (0, 2.5, right, 24), runtime=True)
    write('game-wordmark', [l1, l2] + [tp.to_path(a) for a in a1 + a2], (x0 - 0.5, 2.5, right, 24))

    # Alpine Labs: the plain pair, its A a third peak rising from behind them, "lpine Labs" on the ground line.
    a3, end = tp.third_peak_a()
    l3, _, r3 = set_line(extra, 'lpine Labs', end - 0.29, 21.0, 9.4, 0.195)
    write('studio-lockup', [tp.to_path(plain), tp.to_path(a3), l3], (0, 2.5, r3 + 0.5, 24), runtime=True)

    # Below 32 px the tucked A merges into the peaks: the plain pair and an ordinary A.
    l4, _, r4 = set_line(extra, 'Alpine Labs', 24 + GAP, 21.0, 9.4, 0.195)
    write('studio-lockup-small', [tp.to_path(plain), l4], (0, 2.5, r4 + 0.5, 24))

    # The studio's name alone, its A a cap-height peak (the title name plate, under the game's lockup).
    l5, a5, r5 = set_line(extra, '{A}lpine Labs', 0.5, 10.0, 9.0, 0.195)
    write('studio-name', [l5] + [tp.to_path(a) for a in a5], (0, 0.4, r5 + 0.5, 12.6), runtime=True)
    print('wrote', MASTERS, 'and', RUNTIME)


if __name__ == '__main__':
    main()
