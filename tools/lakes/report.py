"""Task 4.09 (demo 101): the lakes report. Reads the CSVs the lake tests and the player's -lakereport wrote under
test-results/lakes and writes test-results/lakes/report.html: inline SVG charts, no packages needed.

  python tools/lakes/report.py [test-results/lakes]

- The fixture year (jackson-hole.csv, jackson-nh.csv): each body's ice and snow over the label year, dates marked.
- A real map (any folder with bodies.csv + daily.csv, e.g. katahdin/): the map of bodies, every body's frozen spans as a
  timeline (deepest at the bottom), and ice-on / ice-out against depth.
"""
import csv
import html
import os
import sys

MONTHS = ["Oct", "Nov", "Dec", "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep"]
# One categorical palette, light and dark safe; ice is the first, snow a light neutral.
PALETTE = ["#2E6FD8", "#E07B39", "#2A9D8F", "#B5487A", "#7A5CC7", "#C9A227", "#4E8F3A", "#D1495B"]
ICE, SNOW, GRID, INK, MUTED = "#2E6FD8", "#B8C4D0", "#D9DEE5", "#1F2937", "#6B7280"


def read_csv(path):
    with open(path, newline="", encoding="utf-8") as f:
        return list(csv.DictReader(f))


def month_ticks(rows):
    """(x day, label) where each label month starts."""
    ticks, last = [], None
    for r in rows:
        m = int(r["month"])
        if m != last:
            ticks.append((int(r["day"]), MONTHS[(m - 10) % 12]))
            last = m
    return ticks


def axis(svg, x0, y0, w, h, days, ticks, ymax, yunit):
    for d, label in ticks:
        x = x0 + w * d / days
        svg.append(f'<line x1="{x:.1f}" y1="{y0}" x2="{x:.1f}" y2="{y0 + h}" stroke="{GRID}" />')
        svg.append(f'<text x="{x + 3:.1f}" y="{y0 + h + 14}" font-size="11" fill="{MUTED}">{label}</text>')
    step = nice_step(ymax)
    v = 0.0
    while v <= ymax + 1e-9:
        y = y0 + h - h * v / ymax
        svg.append(f'<line x1="{x0}" y1="{y:.1f}" x2="{x0 + w}" y2="{y:.1f}" stroke="{GRID}" />')
        svg.append(f'<text x="{x0 - 6}" y="{y + 4:.1f}" font-size="11" fill="{MUTED}" text-anchor="end">{v:g}</text>')
        v += step
    svg.append(f'<text x="{x0 - 40}" y="{y0 - 8}" font-size="11" fill="{MUTED}">{yunit}</text>')


def nice_step(top):
    for s in (1, 2, 5, 10, 20, 50, 100):
        if top / s <= 6:
            return s
    return 200


def body_chart(rows, ice_key, snow_key, title, on, off):
    """One body: ice as a line, snow on it as a light area, the ice-on and ice-out days marked."""
    W, H, x0, y0, w, h = 520, 210, 50, 30, 450, 140
    days = len(rows)
    ice = [float(r[ice_key]) for r in rows]
    snow = [float(r[snow_key]) for r in rows]
    ymax = max(6.0, max(ice) * 1.15, max(snow) * 1.15)
    svg = [f'<svg viewBox="0 0 {W} {H}" width="{W}" height="{H}" role="img" aria-label="{html.escape(title)}">',
           f'<text x="{x0}" y="16" font-size="13" font-weight="600" fill="{INK}">{html.escape(title)}</text>']
    axis(svg, x0, y0, w, h, days, month_ticks(rows), ymax, "inches")

    def pts(vals):
        return " ".join(f"{x0 + w * i / days:.1f},{y0 + h - h * v / ymax:.1f}" for i, v in enumerate(vals))
    svg.append(f'<polygon points="{x0},{y0 + h} {pts(snow)} {x0 + w},{y0 + h}" fill="{SNOW}" fill-opacity="0.6" />')
    svg.append(f'<polyline points="{pts(ice)}" fill="none" stroke="{ICE}" stroke-width="2" />')
    for day, label in ((on, "ice on"), (off, "ice out")):
        if day is None or day < 0:
            continue
        x = x0 + w * day / days
        svg.append(f'<line x1="{x:.1f}" y1="{y0}" x2="{x:.1f}" y2="{y0 + h}" stroke="{INK}" stroke-dasharray="3 3" />')
        svg.append(f'<text x="{x + 3:.1f}" y="{y0 + 10}" font-size="11" fill="{INK}">{label} {date_of(rows, day)}</text>')
    svg.append("</svg>")
    return "".join(svg)


def date_of(rows, day):
    if day is None or day < 0 or day >= len(rows):
        return "never"
    r = rows[day]
    return f'{MONTHS[(int(r["month"]) - 10) % 12]} {r["dom"]}'


def legend():
    return (f'<p class="legend"><span style="background:{ICE}"></span>ice thickness '
            f'<span style="background:{SNOW}"></span>snow on the ice</p>')


def fixture_section(root, name, label):
    path = os.path.join(root, name + ".csv")
    if not os.path.exists(path):
        return ""
    rows = read_csv(path)
    dates = {r["body"]: (int(r["ice_on_day"]), int(r["ice_out_day"])) for r in read_csv(os.path.join(root, name + "-dates.csv"))}
    names = [k[:-len(" ice in")] for k in rows[0].keys() if k.endswith(" ice in")]
    out = [f"<h2>{html.escape(label)}</h2>", legend(), '<div class="grid">']
    for n in names:
        on, off = dates.get(n, (-1, -1))
        out.append(body_chart(rows, n + " ice in", n + " snow in", f"{n}: on {date_of(rows, on)}, out {date_of(rows, off)}", on, off))
    out.append("</div>")
    return "".join(out)


def map_section(root, sub):
    folder = os.path.join(root, sub)
    if not os.path.exists(os.path.join(folder, "bodies.csv")):
        return ""
    bodies = read_csv(os.path.join(folder, "bodies.csv"))
    rows = read_csv(os.path.join(folder, "daily.csv"))
    site = open(os.path.join(folder, "site.txt"), encoding="utf-8").read() if os.path.exists(os.path.join(folder, "site.txt")) else ""
    days = len(rows)
    out = [f"<h2>{html.escape(sub)}: every water body</h2><pre>{html.escape(site)}</pre>"]
    if os.path.exists(os.path.join(folder, "bodies.png")):
        out.append(f'<p><img src="{sub}/bodies.png" alt="the water bodies, a colour per lake" style="max-width:520px;image-rendering:pixelated"></p>')

    # The timeline: one row per body, its frozen days (ice over 1 in) as bars; deepest at the bottom.
    order = sorted(range(len(bodies)), key=lambda b: float(bodies[b]["depth_ft"]))
    W, row, x0 = 760, 12, 230
    w = W - x0 - 20
    H = 40 + row * len(order) + 30
    svg = [f'<svg viewBox="0 0 {W} {H}" width="{W}" height="{H}" role="img" aria-label="frozen spans per body">',
           f'<text x="{x0}" y="16" font-size="13" font-weight="600" fill="{INK}">Frozen (ice over 1 in), each body, shallow to deep</text>']
    for d, label in month_ticks(rows):
        x = x0 + w * d / days
        svg.append(f'<line x1="{x:.1f}" y1="26" x2="{x:.1f}" y2="{H - 24}" stroke="{GRID}" />')
        svg.append(f'<text x="{x + 3:.1f}" y="{H - 10}" font-size="11" fill="{MUTED}">{label}</text>')
    for n, b in enumerate(order):
        y = 30 + n * row
        info = bodies[b]
        name = "streams" if info["kind"] == "Streams" else f'lake {b} · {float(info["acres"]):.0f} ac · {float(info["depth_ft"]):.0f} ft'
        svg.append(f'<text x="{x0 - 6}" y="{y + 9}" font-size="10" fill="{INK}" text-anchor="end">{html.escape(name)}</text>')
        start = None
        for d in range(days + 1):
            frozen = d < days and float(rows[d][f"ice_{b}"]) > 1.0
            if frozen and start is None:
                start = d
            if not frozen and start is not None:
                svg.append(f'<rect x="{x0 + w * start / days:.1f}" y="{y + 1}" width="{max(1.0, w * (d - start) / days):.1f}" height="{row - 3}" fill="{ICE}" />')
                start = None
    svg.append("</svg>")
    out.append("".join(svg))

    # Ice-on and ice-out against depth.
    W, H, x0, y0, w, h = 560, 300, 60, 30, 470, 220
    pts = [(float(r["depth_ft"]), int(r["ice_on_day"]), int(r["ice_out_day"])) for r in bodies]
    dmax = max(10.0, max(p[0] for p in pts) * 1.1)
    svg = [f'<svg viewBox="0 0 {W} {H}" width="{W}" height="{H}" role="img" aria-label="ice dates against depth">',
           f'<text x="{x0}" y="16" font-size="13" font-weight="600" fill="{INK}">Ice-on and ice-out by depth</text>']
    for d, label in month_ticks(rows):
        y = y0 + h * d / days
        svg.append(f'<line x1="{x0}" y1="{y:.1f}" x2="{x0 + w}" y2="{y:.1f}" stroke="{GRID}" />')
        svg.append(f'<text x="{x0 - 6}" y="{y + 4:.1f}" font-size="11" fill="{MUTED}" text-anchor="end">{label}</text>')
    step = nice_step(dmax)
    v = 0.0
    while v <= dmax:
        x = x0 + w * v / dmax
        svg.append(f'<text x="{x:.1f}" y="{y0 + h + 16}" font-size="11" fill="{MUTED}" text-anchor="middle">{v:g} ft</text>')
        v += step
    for depth, on, off in pts:
        x = x0 + w * depth / dmax
        if on >= 0:
            svg.append(f'<circle cx="{x:.1f}" cy="{y0 + h * on / days:.1f}" r="3.5" fill="{PALETTE[1]}" />')
        if off >= 0:
            svg.append(f'<circle cx="{x:.1f}" cy="{y0 + h * off / days:.1f}" r="3.5" fill="{PALETTE[0]}" />')
    svg.append("</svg>")
    out.append("".join(svg))
    out.append(f'<p class="legend"><span style="background:{PALETTE[1]}"></span>ice on <span style="background:{PALETTE[0]}"></span>ice out</p>')
    return "".join(out)


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else os.path.join("test-results", "lakes")
    parts = [fixture_section(root, "jackson-hole", "Fixture bodies, a generated Jackson Hole year (West typical rows)"),
             fixture_section(root, "jackson-nh", "Fixture bodies, a generated Jackson, New Hampshire year (East rows)")]
    for sub in sorted(os.listdir(root)):
        if os.path.isdir(os.path.join(root, sub)):
            parts.append(map_section(root, sub))
    page = f"""<!DOCTYPE html><html><head><meta charset="utf-8"><title>Lakes freeze and thaw (4.09)</title>
<style>
body {{ font-family: system-ui, sans-serif; color: {INK}; margin: 24px; max-width: 1120px; }}
h1 {{ font-size: 22px; }} h2 {{ font-size: 17px; margin-top: 32px; }}
.grid {{ display: grid; grid-template-columns: repeat(2, 520px); gap: 12px; }}
.legend span {{ display: inline-block; width: 12px; height: 12px; margin: 0 4px 0 12px; vertical-align: -1px; }}
pre {{ color: {MUTED}; font-size: 12px; }}
</style></head><body>
<h1>Lakes freeze and thaw (task 4.09)</h1>
<p>Each water body's ice and the snow on it over one label year (October 1 to September 30), played by the simulation's
lake rules (LK-03 v2 to LK-08 v2). A frozen body under an inch of ice still reads as open water.</p>
{''.join(parts)}
</body></html>"""
    path = os.path.join(root, "report.html")
    with open(path, "w", encoding="utf-8") as f:
        f.write(page)
    print(f"[lakes] wrote {path}")


if __name__ == "__main__":
    main()
