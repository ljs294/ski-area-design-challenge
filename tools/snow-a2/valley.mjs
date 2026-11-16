// Task 4.07 PR A2, finding 2 (the valley goes green too soon): draws test-results/snow-a2/valley/probe.json, which
// SnowValleyProbeTests writes, as valley.html beside it. For each probed cell (the valley floor, the base's height, the
// deepest pile): the snow's depth hour by hour in both runs (thin snow bright from 20 mm of water, before; from 5 mm, after)
// against the depths that draw half white (3 in before, 1 in after), the air, the brightness the melt uses, and a table of
// how long each reads white.
// Usage: node tools/snow-a2/valley.mjs [test-results/snow-a2/valley/probe.json]
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";

const dataPath = resolve(process.argv[2] ?? "test-results/snow-a2/valley/probe.json");
const data = JSON.parse(readFileSync(dataPath, "utf8"));
const esc = (s) => String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/"/g, "&quot;");
const fmt = (v, d = 0) => (v == null || Number.isNaN(v) ? "–" : v.toLocaleString("en-US", { minimumFractionDigits: d, maximumFractionDigits: d }));
const niceStep = (raw) => { const p = Math.pow(10, Math.floor(Math.log10(raw))), f = raw / p; return (f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10) * p; };
const MONTHS = ["", "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
// A row: [faceDay, month, day*100+hour, air °F, wet bulb °F, sun W/m², snow µm, rain µm, water µm, depth mm, density, albedo ‰, type]
const label = (r) => `${MONTHS[r[1]]} ${Math.floor(r[2] / 100)}, ${String(r[2] % 100).padStart(2, "0")}:00`;
const IN = 25.4;
const HALF_NOW = 75, HALF_NEW = 25;   // mm: half of 15 cm and of 5 cm full cover

function chart(rows, lines, { title, unit, d = 0, refs = [], w = 1040, h = 250, min = 0 }) {
  const n = rows.length, left = 46, right = 150, top = 26, bottom = 26;
  const values = lines.flatMap((l) => rows.map(l.get));
  const lo = Math.min(min, ...values), hi = Math.max(1, ...values, ...refs.map((r) => r.v));
  const step = niceStep((hi - lo) / 5), y0 = Math.floor(lo / step) * step, y1 = Math.ceil(hi / step) * step;
  const x = (i) => left + (i / Math.max(1, n - 1)) * (w - left - right), y = (v) => top + (1 - (v - y0) / (y1 - y0)) * (h - top - bottom);
  let svg = `<svg viewBox="0 0 ${w} ${h}" role="img" aria-label="${esc(title)}"><text x="${left}" y="14" class="label">${esc(title)}</text>`;
  for (let t = y0; t <= y1 + 1e-9; t += step)
    svg += `<line x1="${left}" x2="${w - right}" y1="${y(t)}" y2="${y(t)}" class="${Math.abs(t) < 1e-9 ? "axis" : "grid"}"/><text x="${left - 6}" y="${y(t) + 4}" class="tick" text-anchor="end">${fmt(t)}</text>`;
  let lastMonth = 0;
  rows.forEach((r, i) => { if (r[1] !== lastMonth) { lastMonth = r[1]; svg += `<text x="${x(i)}" y="${h - 8}" class="tick">${MONTHS[r[1]]}</text><line x1="${x(i)}" x2="${x(i)}" y1="${top}" y2="${h - bottom}" class="grid"/>`; } });
  for (const r of refs) svg += `<line x1="${left}" x2="${w - right}" y1="${y(r.v)}" y2="${y(r.v)}" class="ref"/><text x="${w - right + 6}" y="${y(r.v) + 4}" class="value">${esc(r.text)}</text>`;
  for (const l of lines) svg += `<path d="${rows.map((r, i) => `${i ? "L" : "M"}${x(i).toFixed(1)},${y(l.get(r)).toFixed(1)}`).join("")}" class="line ${l.cls}"/>`;
  const series = lines.map((l) => rows.map((r) => +l.get(r).toFixed(d)));
  svg += `<line class="cross" x1="0" x2="0" y1="${top}" y2="${h - bottom}" visibility="hidden"/>`;
  svg += `<rect class="hover" x="${left}" y="${top}" width="${w - left - right}" height="${h - top - bottom}" fill="transparent" data-left="${left}" data-right="${right}" data-w="${w}"`
    + ` data-series='${JSON.stringify(series)}' data-labels='${JSON.stringify(rows.map(label))}' data-words='${JSON.stringify(lines.map((l) => l.word))}'`
    + ` data-cls='${JSON.stringify(lines.map((l) => l.cls))}' data-unit="${esc(unit)}" data-d="${d}"/>`;
  return svg + "</svg>";
}

// Days (face hours ÷ 24) at or over a depth, and the first and last hour it was under it after first being over it.
function whiteDays(rows, mm) { return rows.filter((r) => r[9] >= mm).length / 24; }
function sameWater(a, b) { return a.length === b.length && a.every((r, i) => r[8] === b[i][8]); }

let body = "";
const summary = [];
for (const p of data.points) {
  const [a, b] = p.runs, ra = a.hours, rb = b.hours;
  body += `<h2>${esc(p.name)}</h2><p>${esc(p.where)}: ${fmt(p.feet)} ft, grade ${p.grade}%, ${fmt(p.hvf)} ft over the valley floor.</p>`;
  body += `<div class="legend"><span class="key"><i class="k-l1"></i>${esc(a.label)}</span><span class="key"><i class="k-l2"></i>${esc(b.label)}</span><span class="key"><i class="k-ref"></i>depth that draws half white</span></div>`;
  const deep = Math.max(...ra.map((r) => r[9]), ...rb.map((r) => r[9])) / IN;
  body += chart(ra.map((r, i) => [...r, rb[i]?.[9] ?? 0]), [
    { get: (r) => r[9] / IN, word: a.label, cls: "l1" },
    { get: (r) => r[13] / IN, word: b.label, cls: "l2" },
  ], { title: "Snow depth (in)", unit: " in", d: 1, refs: deep < 40 ? [{ v: 3, text: "3 in: half white before" }, { v: 1, text: "1 in: half white after" }] : [] });
  body += chart(ra, [{ get: (r) => r[3], word: "Air", cls: "l3" }], { title: "Air at the cell (°F)", unit: " °F", d: 0, refs: [{ v: 32, text: "32 °F" }], h: 190, min: 0 });
  body += chart(ra.map((r, i) => [...r, rb[i]?.[11] ?? 0]), [
    { get: (r) => r[11] / 10, word: a.label, cls: "l1" },
    { get: (r) => r[13] / 10, word: b.label, cls: "l2" },
  ], { title: "Brightness the melt uses (%; 0 with no snow)", unit: " %", d: 0, h: 190 });
  for (const run of p.runs)
    summary.push({ point: p.name, run: run.label, now: whiteDays(run.hours, HALF_NOW), fix: whiteDays(run.hours, HALF_NEW), deepest: Math.max(...run.hours.map((r) => r[9])) / IN });
  if (p.name === "Deepest pile")
  {
    const peakA = Math.max(...ra.map((r) => r[8])), peakB = Math.max(...rb.map((r) => r[8]));
    const most = Math.max(...ra.map((r, i) => Math.abs(r[8] - (rb[i]?.[8] ?? 0))));
    body += sameWater(ra, rb)
      ? `<p><b>The pile's water is identical in both runs, every hour, to the µm.</b></p>`
      : `<p><b>The pile's peak water: ${fmt(peakA / 1000)} mm before, ${fmt(peakB / 1000)} mm after (${fmt(100 * (peakB - peakA) / peakA, 1)}%); the most the two runs ever differ is ${fmt(most / 1000, 1)} mm.</b> The pile starts the season as thin snow too, and the slopes that shed onto it keep their early snow longer; once deep it melts the same.</p>`;
  }
}
const total = data.points[0].runs[0].hours.length / 24;
let table = `<table><tr><th>Cell</th><th>Run</th><th>Face days drawn at least half white, 6 in for full white (before)</th><th>… 2 in for full white (after)</th><th>Deepest (in)</th></tr>`;
for (const s of summary) table += `<tr><td>${esc(s.point)}</td><td>${esc(s.run)}</td><td>${fmt(s.now, 1)} of ${fmt(total, 0)}</td><td>${fmt(s.fix, 1)}</td><td>${fmt(s.deepest, 0)}</td></tr>`;
table += "</table>";

const html = `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>The valley's snow, hour by hour (4.07 finding 2)</title>
<style>
.viz-root{color-scheme:light;--surface-1:#fcfcfb;--text-primary:#0b0b0b;--text-secondary:#52514e;--muted:#898781;--grid:#e1e0d9;--axis:#c3c2b7;
--series-1:#2a78d6;--series-2:#eb6834;--series-3:#1baf7a}
@media (prefers-color-scheme:dark){.viz-root{color-scheme:dark;--surface-1:#1a1a19;--text-primary:#f0efec;--text-secondary:#c3c2b7;--grid:#2c2c2a;--axis:#383835;
--series-1:#3987e5;--series-2:#d95926;--series-3:#199e70}}
body{margin:0;background:var(--surface-1)}
.viz-root{background:var(--surface-1);color:var(--text-primary);font:15px/1.5 system-ui,sans-serif;max-width:1120px;margin:0 auto;padding:28px}
h1{font-size:24px;margin:0 0 4px}h2{font-size:20px;margin:34px 0 4px}p{color:var(--text-secondary);margin:6px 0 12px}
svg{width:100%;height:auto;display:block;overflow:visible;margin:6px 0 10px}.grid{stroke:var(--grid)}.axis{stroke:var(--axis)}.ref{stroke:var(--muted);stroke-dasharray:4 4}
.tick{fill:var(--muted);font-size:11px}.label{fill:var(--text-primary);font-size:13px}.value{fill:var(--text-secondary);font-size:12px}
.line{fill:none;stroke-width:2;stroke-linejoin:round}.l1{stroke:var(--series-1)}.l2{stroke:var(--series-2)}.l3{stroke:var(--series-3)}
.cross{stroke:var(--muted);stroke-width:1}
.legend{display:flex;flex-wrap:wrap;gap:8px 18px;margin:8px 0}.key{display:flex;align-items:center;gap:6px;color:var(--text-secondary);font-size:14px}
.key i{width:16px;height:4px;border-radius:2px;display:inline-block}.k-l1{background:var(--series-1)}.k-l2{background:var(--series-2)}.k-ref{border-top:2px dashed var(--muted);height:0!important}
table{border-collapse:collapse;font-size:13px;margin-top:8px}th,td{padding:4px 8px;border-bottom:1px solid var(--grid);text-align:left;vertical-align:top}
#tip{position:fixed;pointer-events:none;background:var(--surface-1);color:var(--text-primary);border:1px solid var(--axis);border-radius:6px;padding:6px 9px;font-size:12px;display:none;box-shadow:0 2px 8px #0002;z-index:9}
</style></head><body><div class="viz-root">
<h1>The valley's snow, hour by hour</h1>
<p>Task 4.07 PR A2, finding 2: Jackson Hole under its frozen weather rows, bare October 1 to December 15, each cell on its own 2 km crop (made ${esc(data.made)}). Two runs: thin snow bright from 20 mm of water (before) and from 5 mm (after). A face hour is about 2.9 real hours.</p>
<h2>How long each reads white</h2>${table}
${body}
<div id="tip"></div></div>
<script>
const tip = document.getElementById("tip");
for (const r of document.querySelectorAll("rect.hover")) {
  const svg = r.ownerSVGElement, cross = svg.querySelector(".cross"), series = JSON.parse(r.dataset.series), labels = JSON.parse(r.dataset.labels);
  const words = JSON.parse(r.dataset.words), left = +r.dataset.left, right = +r.dataset.right, w = +r.dataset.w, unit = r.dataset.unit, n = labels.length;
  r.addEventListener("mousemove", (e) => {
    const pt = svg.createSVGPoint(); pt.x = e.clientX; pt.y = e.clientY;
    const p = pt.matrixTransform(svg.getScreenCTM().inverse());
    const i = Math.max(0, Math.min(n - 1, Math.round((p.x - left) / (w - left - right) * (n - 1))));
    const x = left + i / Math.max(1, n - 1) * (w - left - right);
    cross.setAttribute("x1", x); cross.setAttribute("x2", x); cross.setAttribute("visibility", "visible");
    tip.innerHTML = "<b>" + labels[i] + "</b><br>" + words.map((wd, k) => wd + ": " + series[k][i] + unit).join("<br>");
    tip.style.display = "block"; tip.style.left = (e.clientX + 14) + "px"; tip.style.top = (e.clientY + 10) + "px";
  });
  r.addEventListener("mouseleave", () => { cross.setAttribute("visibility", "hidden"); tip.style.display = "none"; });
}
</script></body></html>`;
const out = join(dirname(dataPath), "valley.html");
writeFileSync(out, html);
console.log(`wrote ${out}`);
