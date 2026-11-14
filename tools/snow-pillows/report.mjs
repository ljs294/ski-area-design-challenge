// Task 4.07 PR A1: the snow pillow report (demo 112). Reads test-results/snow-pillows/<tag>.json, which SnowPillowTests
// writes, and writes <tag>.html beside it: the pass marks at every pillow, each pillow's median winter (the pillow, the
// new snow energy on the gauge, 4.05's reference model, the game's clock, the melt-only play from the measured peak),
// and the month-by-month energy and water ledgers; first, the fix round's before and after (fix-round-a1.json beside this script).
// Usage: node tools/snow-pillows/report.mjs [test-results/snow-pillows/run.json]
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, basename, join, resolve } from "node:path";

const dataPath = resolve(process.argv[2] ?? "test-results/snow-pillows/run.json");
const data = JSON.parse(readFileSync(dataPath, "utf8"));
const names = {
  "jackson-hole-2km": "Jackson Hole, WY", "olympic-valley-4km": "Olympic Valley, CA", "stevens-pass-2km": "Stevens Pass, WA",
  "winter-park-2km": "Winter Park, CO", "jackson-nh-4km": "Jackson, NH", "snowshoe-2km": "Snowshoe, WV", "snow-ridge-2km": "Snow Ridge, NY",
};
const esc = (s) => String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/"/g, "&quot;");
const fmt = (v, d = 0) => (v == null || Number.isNaN(v) ? "–" : v.toLocaleString("en-US", { minimumFractionDigits: d, maximumFractionDigits: d }));
const median = (a) => {
  const v = a.filter((x) => x != null && !Number.isNaN(x)).sort((x, y) => x - y);
  if (!v.length) return NaN;
  const m = v.length >> 1;
  return v.length % 2 ? v[m] : (v[m - 1] + v[m]) / 2;
};
const MONTHS = ["Oct", "Nov", "Dec", "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul"];
const MONTH_DAY = [0, 31, 61, 92, 123, 151, 182, 212, 243, 273];
const TWO_GATE = new Set(["791:WA:SNTL", "809:CA:SNTL"]);   // the midwinter-loss pillows: Jan 1 and Apr 1 water are gated there
const label = (p) => `${p.name} (${p.station.split(":").slice(0, 2).join(" ")}), ${fmt(p.feet)} ft`;

// ---- the gates: one row per gate, one dot per pillow, the passing band shaded ----
const GATES = [
  { key: "meltOutErr", title: "Melt-out date", sub: "Melt only, from the measured peak: days early (−) or late (+)", lo: -10, hi: 10, unit: " d", d: 0, ref: "refMeltOutErr" },
  { key: "rateRatio", title: "Spring melt rate", sub: "Melt only: modelled ÷ measured", lo: 0.75, hi: 1.25, unit: "×", d: 2 },
  { key: "jan1", title: "January 1 water", sub: "On the gauge: modelled ÷ measured (gated at Stevens Pass and Tahoe City only)", lo: 0.85, hi: 1.15, unit: "×", d: 2, only: TWO_GATE },
  { key: "apr1", title: "April 1 water", sub: "On the gauge: modelled ÷ measured (gated at Stevens Pass and Tahoe City only)", lo: 0.85, hi: 1.15, unit: "×", d: 2, only: TWO_GATE, ref: "refApr1" },
  { key: "mae", title: "Daily error", sub: "Mean absolute daily water error on the gauge (in): must match or beat the reference model's", unit: " in", d: 2, ref: "refMae", beat: true },
  { key: "gameVsHour", title: "The game's clock", sub: "April 1 water stepped in face hours (K = 2.88 h) ÷ stepped hourly", lo: 0.9, hi: 1.1, unit: "×", d: 2 },
];
const passes = (g, p) => {
  const v = p.gates[g.key];
  if (g.only && !g.only.has(p.station)) return null;
  if (g.beat) return v <= p.gates[g.ref] + 1e-9;
  return v >= g.lo - 1e-9 && v <= g.hi + 1e-9;
};

function gateChart(g) {
  const ps = data.pillows, rowH = 28, left = 130, right = 58, top = 8, width = 480, plotW = width - left - right;
  const vals = ps.flatMap((p) => [p.gates[g.key], g.ref ? p.gates[g.ref] : null]).filter((v) => v != null && !Number.isNaN(v));
  let lo = Math.min(...vals, g.lo ?? Infinity), hi = Math.max(...vals, g.hi ?? -Infinity);
  if (g.beat) lo = 0;
  const pad = (hi - lo) * 0.08 || 1; lo -= g.beat ? 0 : pad; hi += pad;
  const x = (v) => left + ((v - lo) / (hi - lo)) * plotW;
  const h = top + ps.length * rowH + 28;
  let svg = `<svg viewBox="0 0 ${width} ${h}" role="img" aria-label="${esc(g.title)}">`;
  if (g.lo != null) svg += `<rect x="${x(g.lo)}" y="${top}" width="${x(g.hi) - x(g.lo)}" height="${h - top - 28}" class="band"/>`;
  const step = niceStep((hi - lo) / 5);
  for (let t = Math.ceil(lo / step) * step; t <= hi + 1e-9; t += step) {
    svg += `<line x1="${x(t)}" x2="${x(t)}" y1="${top}" y2="${h - 28}" class="grid"/>`;
    svg += `<text x="${x(t)}" y="${h - 10}" class="tick" text-anchor="middle">${fmt(t, step < 0.1 ? 2 : step < 1 ? 1 : 0)}</text>`;
  }
  ps.forEach((p, i) => {
    const y = top + i * rowH + rowH / 2, v = p.gates[g.key], ok = passes(g, p);
    const rv = g.ref ? p.gates[g.ref] : null;
    const tip = `${label(p)}: ${fmt(v, g.d)}${g.unit}` + (rv != null ? `, reference model ${fmt(rv, g.d)}${g.unit}` : "") +
      (ok == null ? " (not gated here)" : ok ? " — passes" : " — misses");
    svg += `<g class="hit"><title>${esc(tip)}</title><rect x="0" y="${y - rowH / 2}" width="${width}" height="${rowH}" fill="transparent"/>`;
    svg += `<text x="${left - 10}" y="${y + 5}" class="label" text-anchor="end">${esc(p.name)}</text>`;
    if (rv != null && !Number.isNaN(rv)) {
      svg += `<line x1="${x(rv)}" x2="${x(v)}" y1="${y}" y2="${y}" class="link"/>`;
      svg += `<circle cx="${x(rv)}" cy="${y}" r="5" class="s2 ring"/>`;
    }
    svg += `<circle cx="${x(v)}" cy="${y}" r="6" class="s1 ring"/>`;
    const word = ok == null ? "not gated" : ok ? "✓ pass" : "✗ miss";
    svg += `<text x="${width - right + 8}" y="${y + 5}" class="${ok === false ? "miss" : "value"}">${word}</text></g>`;
  });
  svg += `</svg>`;
  return `<figure><figcaption><b>${esc(g.title)}</b><span>${esc(g.sub)}${g.lo != null ? `. The shaded band passes (${fmt(g.lo, g.d)}${g.unit} to ${fmt(g.hi, g.d)}${g.unit}).` : "."}</span></figcaption>${svg}</figure>`;
}

function niceStep(raw) {
  const p = Math.pow(10, Math.floor(Math.log10(raw))), f = raw / p;
  return (f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10) * p;
}

// ---- one pillow's median winter: water by day, five lines ----
const LINES = [
  { key: "measured", word: "The pillow (measured)", cls: "ink" },
  { key: "gauge", word: "Snow energy on the gauge (new)", cls: "l1" },
  { key: "reference", word: "4.05's reference model", cls: "l2" },
  { key: "game", word: "Snow energy at the game's clock", cls: "l3" },
  { key: "meltOnly", word: "Melt only, from the measured peak", cls: "l4" },
];

function seasonChart(p) {
  const days = p.winters[0].measured.length, w = 360, h = 210, left = 40, right = 10, top = 26, bottom = 24;
  const med = Object.fromEntries(LINES.map((l) => [l.key, Array.from({ length: days }, (_, d) => median(p.winters.map((x) => x[l.key][d])))]));
  const max = Math.max(1, ...LINES.flatMap((l) => med[l.key].filter((v) => !Number.isNaN(v))));
  const yStep = niceStep(max / 4), yMax = Math.ceil(max / yStep) * yStep;
  const x = (d) => left + (d / (days - 1)) * (w - left - right), y = (v) => top + (1 - v / yMax) * (h - top - bottom);
  let svg = `<svg viewBox="0 0 ${w} ${h}" role="img" aria-label="${esc(label(p))} median winter" data-days="${days}">`;
  svg += `<text x="${left}" y="14" class="label">${esc(p.name)} · ${fmt(p.feet)} ft</text>`;
  for (let t = 0; t <= yMax + 1e-9; t += yStep) {
    svg += `<line x1="${left}" x2="${w - right}" y1="${y(t)}" y2="${y(t)}" class="grid"/><text x="${left - 6}" y="${y(t) + 4}" class="tick" text-anchor="end">${fmt(t)}</text>`;
  }
  MONTH_DAY.forEach((d, i) => { if (i % 2 === 0) svg += `<text x="${x(d)}" y="${h - 6}" class="tick">${MONTHS[i]}</text>`; });
  for (const l of LINES) {
    let path = "", pen = false;
    med[l.key].forEach((v, d) => {
      if (Number.isNaN(v)) { pen = false; return; }
      path += `${pen ? "L" : "M"}${x(d).toFixed(1)},${y(v).toFixed(1)}`; pen = true;
    });
    svg += `<path d="${path}" class="line ${l.cls}"/>`;
  }
  // the hover layer: a crosshair and a tooltip per day, read from the medians
  const series = LINES.map((l) => med[l.key].map((v) => (Number.isNaN(v) ? null : +v.toFixed(2))));
  svg += `<line class="cross" x1="0" x2="0" y1="${top}" y2="${h - bottom}" visibility="hidden"/>`;
  svg += `<rect class="hover" x="${left}" y="${top}" width="${w - left - right}" height="${h - top - bottom}" fill="transparent" data-left="${left}" data-right="${right}" data-w="${w}" data-series='${JSON.stringify(series)}'/>`;
  svg += `</svg>`;
  return `<div class="cell">${svg}</div>`;
}

// ---- one pillow's month-by-month energy into the snow (median winter, W/m² while there is snow) ----
const FLUX = [
  { key: "fluxSun", word: "Absorbed sun", cls: "s1" },
  { key: "fluxLongwave", word: "Net longwave", cls: "s2" },
  { key: "fluxSensible", word: "Sensible (warm air)", cls: "s3" },
  { key: "fluxLatent", word: "Latent (sublimation, condensation)", cls: "s4" },
];
function energyChart(p) {
  const months = 8, w = 360, h = 200, left = 40, right = 8, top = 26, bottom = 22;
  const med = FLUX.map((f) => Array.from({ length: months }, (_, m) => median(p.winters.map((x) => x[f.key][m]))));
  const all = med.flat().filter((v) => !Number.isNaN(v));
  const hi = Math.ceil(Math.max(10, ...all) / 10) * 10, lo = Math.floor(Math.min(-10, ...all) / 10) * 10;
  const y = (v) => top + ((hi - v) / (hi - lo)) * (h - top - bottom);
  const gw = (w - left - right) / months, bw = (gw - 6) / FLUX.length;
  let svg = `<svg viewBox="0 0 ${w} ${h}" role="img" aria-label="${esc(label(p))} energy by month">`;
  svg += `<text x="${left}" y="14" class="label">${esc(p.name)}</text>`;
  for (let t = lo; t <= hi; t += 10) {
    if ((t / 10) % 2 && hi - lo > 60) continue;
    svg += `<line x1="${left}" x2="${w - right}" y1="${y(t)}" y2="${y(t)}" class="${t === 0 ? "axis" : "grid"}"/><text x="${left - 6}" y="${y(t) + 4}" class="tick" text-anchor="end">${t}</text>`;
  }
  for (let m = 0; m < months; m++) {
    const gx = left + m * gw + 3;
    svg += `<text x="${gx + gw / 2 - 3}" y="${h - 6}" class="tick" text-anchor="middle">${MONTHS[m]}</text>`;
    FLUX.forEach((f, k) => {
      const v = med[k][m];
      if (Number.isNaN(v)) return;
      const y0 = y(Math.max(0, v)), y1 = y(Math.min(0, v));
      const tip = `${p.name}, ${MONTHS[m]}: ${f.word} ${v >= 0 ? "+" : ""}${fmt(v, 1)} W/m²`;
      svg += `<g class="hit"><title>${esc(tip)}</title><rect x="${gx + k * bw}" y="${top}" width="${bw}" height="${h - top - bottom}" fill="transparent"/>` +
        `<rect x="${gx + k * bw + 1}" y="${y0}" width="${Math.max(1, bw - 2)}" height="${Math.max(0.5, y1 - y0)}" rx="1.5" class="${f.cls}"/></g>`;
    });
  }
  svg += `</svg>`;
  return `<div class="cell">${svg}</div>`;
}

function ledgerTable(p) {
  const rows = [["Snow fallen", "ledgerSnow"], ["Rain", "ledgerRain"], ["Run off", "ledgerRunoff"], ["Sublimated", "ledgerSublimated"]];
  const air = ["Mean air (°F)", "airF"], cloud = ["Mean cloud (%)", "cloud"], surf = ["Mean snow surface (°C)", "surfaceC"];
  const cell = (key, m, d) => fmt(median(p.winters.map((x) => (x[key] ?? [])[m])), d);
  const body = rows.map(([w, k]) => `<tr><th>${w} (in of water)</th>${MONTHS.map((_, m) => `<td>${cell(k, m, 2)}</td>`).join("")}</tr>`).join("") +
    [air, cloud, surf].filter(([, k]) => p.winters[0][k]).map(([w, k]) => `<tr><th>${w}</th>${MONTHS.map((_, m) => `<td>${cell(k, m, 1)}</td>`).join("")}</tr>`).join("");
  return `<h3>${esc(label(p))}</h3><table><thead><tr><th></th>${MONTHS.map((m) => `<th>${m}</th>`).join("")}</tr></thead><tbody>${body}</tbody></table>`;
}

function summary() {
  const rows = data.pillows.map((p) => `<tr><th>${esc(label(p))}</th><td>${esc(names[p.site] ?? p.site)}</td><td>${p.winters.length}</td>` +
    GATES.map((g) => {
      const ok = passes(g, p), v = p.gates[g.key];
      const ref = g.ref ? ` <span class="muted">(ref ${fmt(p.gates[g.ref], g.d)})</span>` : "";
      return `<td class="${ok === false ? "miss" : ""}">${ok == null ? "" : ok ? "✓ " : "✗ "}${fmt(v, g.d)}${g.unit}${ref}</td>`;
    }).join("") + `</tr>`).join("");
  return `<table><thead><tr><th>Pillow</th><th>Site</th><th>Winters</th>${GATES.map((g) => `<th>${esc(g.title)}</th>`).join("")}</tr></thead><tbody>${rows}</tbody></table>`;
}


// ---- the season report (SnowSeasonReportTests writes season.json beside the data): natural snow at every site ----
const seasonPath = join(dirname(dataPath), "season.json");
const season = existsSync(seasonPath) ? JSON.parse(readFileSync(seasonPath, "utf8")) : null;
const HEIGHTS = ["z1", "z2", "z3"];
const dateWord = (d) => (d == null || d < 0 ? "never"
  : new Date(Date.UTC(2025, 9, 1 + d)).toLocaleDateString("en-US", { month: "short", day: "numeric", timeZone: "UTC" }));
const medianDay = (days) => {
  const v = days.map((x) => (x < 0 ? Infinity : x)).sort((a, b) => a - b), m = v[v.length >> 1];
  return m === Infinity ? -1 : m;
};
function naturalChart(st) {
  const w = 360, h = 210, left = 40, right = 10, top = 26, bottom = 24, days = st.heights[0].median.length;
  const max = Math.max(13, ...st.heights.flatMap((z) => z.p90));
  const yStep = niceStep(max / 4), yMax = Math.ceil(max / yStep) * yStep;
  const x = (d) => left + (d / (days - 1)) * (w - left - right), y = (v) => top + (1 - v / yMax) * (h - top - bottom);
  let svg = `<svg viewBox="0 0 ${w} ${h}" role="img" aria-label="${esc(st.name)} natural snow depth">`;
  svg += `<text x="${left}" y="14" class="label">${esc(names[st.site] ?? st.name)}</text>`;
  for (let t = 0; t <= yMax + 1e-9; t += yStep)
    svg += `<line x1="${left}" x2="${w - right}" y1="${y(t)}" y2="${y(t)}" class="grid"/><text x="${left - 6}" y="${y(t) + 4}" class="tick" text-anchor="end">${fmt(t)}</text>`;
  for (const c of [6, 12])
    svg += `<line x1="${left}" x2="${w - right}" y1="${y(c)}" y2="${y(c)}" class="cover"/><text x="${w - right}" y="${c === 6 ? y(c) + 11 : y(c) - 3}" class="tick" text-anchor="end">${c} in</text>`;
  MONTH_DAY.forEach((d, i) => { if (i % 2 === 0) svg += `<text x="${x(d)}" y="${h - 6}" class="tick">${MONTHS[i]}</text>`; });
  st.heights.forEach((z, k) => {
    let band = z.p90.map((v, d) => `${d ? "L" : "M"}${x(d).toFixed(1)},${y(v).toFixed(1)}`).join("");
    for (let d = days - 1; d >= 0; d--) band += `L${x(d).toFixed(1)},${y(z.p10[d]).toFixed(1)}`;
    svg += `<path d="${band}Z" class="band-${HEIGHTS[k]}"/>`;
  });
  st.heights.forEach((z, k) => {
    svg += `<path d="${z.median.map((v, d) => `${d ? "L" : "M"}${x(d).toFixed(1)},${y(v).toFixed(1)}`).join("")}" class="line ${HEIGHTS[k]}"/>`;
  });
  const words = st.heights.map((z) => `${z.label} (${z.feet.toLocaleString("en-US")} ft)`);
  svg += `<line class="cross" x1="0" x2="0" y1="${top}" y2="${h - bottom}" visibility="hidden"/>`;
  svg += `<rect class="hover" x="${left}" y="${top}" width="${w - left - right}" height="${h - top - bottom}" fill="transparent" data-left="${left}" data-right="${right}" data-w="${w}" data-words='${esc(JSON.stringify(words))}' data-cls='${JSON.stringify(HEIGHTS)}' data-series='${JSON.stringify(st.heights.map((z) => z.median))}'/>`;
  return `<div class="cell">${svg}</svg></div>`;
}
function seasonTable() {
  const by = 80;   // December 20, from October 1
  const rows = season.sites.map((st) => `<tr><th>${esc(names[st.site] ?? st.name)}</th>` + st.heights.map((z) => {
    const ok = z.first12.filter((d) => d >= 0 && d <= by).length;
    return `<td>${fmt(z.feet)} ft</td><td>${dateWord(medianDay(z.first6))}</td><td>${dateWord(medianDay(z.first12))}</td><td>${ok} of ${z.first12.length}</td>`;
  }).join("") + "</tr>").join("");
  const head = ["Base", "Mid", "Summit"].map((w) => `<th colspan="4">${w}</th>`).join("");
  const sub = [0, 1, 2].map(() => "<th>Height</th><th>6 in</th><th>12 in</th><th>12 in by Dec 20</th>").join("");
  return `<table><thead><tr><th></th>${head}</tr><tr><th>Site</th>${sub}</tr></thead><tbody>${rows}</tbody></table>`;
}
function seasonSection() {
  if (!season) return "";
  return `<h2>Natural snow through the season, at every site</h2>
<p>Each site's weather rows as the game ships them, ${season.seeds} seasons, stepped at the game's clock on open flat ground at the site's base, mid and summit heights. Lines are the median depth (inches of settled snow); the shading runs from the 10th to the 90th percentile season. The dashed lines are SN-20's cover: 6 in on a graded trail, 12 in on a natural one. No snowmaking (that is 4.16), no grooming, no skiers. Hover a chart to read the depths on a day.</p>
<div class="legend"><span class="key"><i class="k-z1"></i>Base</span><span class="key"><i class="k-z2"></i>Mid</span><span class="key"><i class="k-z3"></i>Summit</span></div>
<div class="grid3">${season.sites.map(naturalChart).join("")}</div>
<p>The first day the depth reaches each cover line and holds it three days (the median over the seasons; "never" when most seasons don't), and how many seasons have 12 in by December 20:</p>
${seasonTable()}`;
}

// ---- the fix round (owner 2026-11-13): the four open issues, before and after; fix-round-a1.json beside this script ----
const roundPath = new URL("./fix-round-a1.json", import.meta.url);
const round = existsSync(roundPath) ? JSON.parse(readFileSync(roundPath, "utf8")) : null;
const SHORT = { "jackson-hole-2km": "Jackson Hole", "olympic-valley-4km": "Olympic Valley", "stevens-pass-2km": "Stevens Pass",
  "winter-park-2km": "Winter Park", "jackson-nh-4km": "Jackson NH", "snowshoe-2km": "Snowshoe", "snow-ridge-2km": "Snow Ridge" };

// A dumbbell: one row per item, a hollow dot before, a filled dot after, each row's passing band shaded.
function dumbbell({ title, sub, rows, d = 1, unit = "", names = ["Before", "After"] }) {
  const rowH = 26, left = 150, right = 70, top = 6, width = 480, plotW = width - left - right;
  const vals = rows.flatMap((r) => [r.before, r.after, r.lo, r.hi]).filter((v) => v != null && Number.isFinite(v));
  let lo = Math.min(...vals), hi = Math.max(...vals); const pad = (hi - lo) * 0.06 || 1; lo -= pad; hi += pad;
  const x = (v) => left + ((v - lo) / (hi - lo)) * plotW, h = top + rows.length * rowH + 26;
  let svg = `<svg viewBox="0 0 ${width} ${h}" role="img" aria-label="${esc(title)}">`;
  const step = niceStep((hi - lo) / 5);
  for (let t = Math.ceil(lo / step) * step; t <= hi + 1e-9; t += step)
    svg += `<line x1="${x(t)}" x2="${x(t)}" y1="${top}" y2="${h - 26}" class="grid"/><text x="${x(t)}" y="${h - 8}" class="tick" text-anchor="middle">${fmt(t, step < 0.1 ? 2 : step < 1 ? 1 : 0)}</text>`;
  rows.forEach((r, i) => {
    const y = top + i * rowH + rowH / 2;
    if (r.lo != null) svg += `<rect x="${x(r.lo)}" y="${y - rowH / 2 + 2}" width="${x(r.hi) - x(r.lo)}" height="${rowH - 4}" class="band"/>`;
  });
  rows.forEach((r, i) => {
    const y = top + i * rowH + rowH / 2, ok = r.lo == null ? null : r.after >= r.lo - 1e-9 && r.after <= r.hi + 1e-9;
    const tip = `${r.label}: ${names[0].toLowerCase()} ${fmt(r.before, d)}${unit}, ${names[1].toLowerCase()} ${fmt(r.after, d)}${unit}` +
      (r.lo != null ? ` (passes ${fmt(r.lo, d)} to ${fmt(r.hi, d)}${unit})` : "");
    svg += `<g class="hit"><title>${esc(tip)}</title><rect x="0" y="${y - rowH / 2}" width="${width}" height="${rowH}" fill="transparent"/>`;
    svg += `<text x="${left - 10}" y="${y + 4}" class="label" text-anchor="end">${esc(r.label)}</text>`;
    if (r.before != null && r.after != null) svg += `<line x1="${x(r.before)}" x2="${x(r.after)}" y1="${y}" y2="${y}" class="link"/>`;
    if (r.before != null) svg += `<circle cx="${x(r.before)}" cy="${y}" r="5" class="before"/>`;
    if (r.after != null) svg += `<circle cx="${x(r.after)}" cy="${y}" r="6" class="s1 ring"/>`;
    const word = (ok == null ? "" : ok ? "✓ " : "✗ ") + fmt(r.after, d) + unit;
    svg += `<text x="${width - right + 8}" y="${y + 4}" class="${ok === false ? "miss" : "value"}">${word}</text></g>`;
  });
  svg += `</svg>`;
  return `<figure><figcaption><b>${esc(title)}</b><span>${esc(sub)}</span></figcaption>${svg}</figure>`;
}

// A few lines over a shared x: [{ word, cls, pts: [[x, y]], dots }], with optional marks [{ x, y, word }].
function lines({ title, sub, xs, series, marks = [], xWord, yWord, yMax, xFmt = (v) => fmt(v), yFmt = (v) => fmt(v) }) {
  const w = 480, h = 230, left = 44, right = 14, top = 16, bottom = 34;
  const allY = series.flatMap((s) => s.pts.map((p) => p[1])).concat(marks.map((m) => m.y));
  const raw = Math.max(...allY), ys0 = niceStep(raw / 4);
  const ymax = yMax ?? Math.ceil(raw / ys0) * ys0;
  const x = (v) => left + ((v - xs[0]) / (xs[1] - xs[0])) * (w - left - right), y = (v) => top + (1 - v / ymax) * (h - top - bottom);
  let svg = `<svg viewBox="0 0 ${w} ${h}" role="img" aria-label="${esc(title)}">`;
  const ys = niceStep(ymax / 4);
  for (let t = 0; t <= ymax + 1e-9; t += ys) svg += `<line x1="${left}" x2="${w - right}" y1="${y(t)}" y2="${y(t)}" class="grid"/><text x="${left - 6}" y="${y(t) + 4}" class="tick" text-anchor="end">${yFmt(t)}</text>`;
  const xsn = niceStep((xs[1] - xs[0]) / 7);
  for (let t = Math.ceil(xs[0] / xsn) * xsn; t <= xs[1] + 1e-9; t += xsn) svg += `<text x="${x(t)}" y="${h - 18}" class="tick" text-anchor="middle">${xFmt(t)}</text>`;
  svg += `<text x="${(left + w - right) / 2}" y="${h - 2}" class="tick" text-anchor="middle">${esc(xWord)}</text>`;
  svg += `<text x="${left}" y="${top - 6}" class="tick">${esc(yWord)}</text>`;
  for (const s of series) {
    svg += `<path d="${s.pts.map((p, i) => `${i ? "L" : "M"}${x(p[0]).toFixed(1)},${y(p[1]).toFixed(1)}`).join("")}" class="line ${s.cls}"><title>${esc(s.word)}</title></path>`;
    if (s.dots) for (const p of s.pts) svg += `<g class="hit"><title>${esc(`${s.word}: ${xFmt(p[0])}, ${yFmt(p[1])}`)}</title><circle cx="${x(p[0])}" cy="${y(p[1])}" r="9" fill="transparent"/><circle cx="${x(p[0])}" cy="${y(p[1])}" r="3.5" class="dot-${s.cls}"/></g>`;
  }
  for (const m of marks) svg += `<g class="hit"><title>${esc(m.word)}</title><circle cx="${x(m.x)}" cy="${y(m.y)}" r="9" fill="transparent"/><circle cx="${x(m.x)}" cy="${y(m.y)}" r="5" class="mark ring"/></g>`;
  svg += `</svg>`;
  return `<figure><figcaption><b>${esc(title)}</b><span>${esc(sub)}</span></figcaption>${svg}</figure>`;
}

function roundSection() {
  if (!round) return "";
  const F = Object.fromEntries(round.fidelity.map((m) => [m.key, m]));
  const fidRows = (k) => F[k].sites.filter((s) => s.before && s.after).map((s) => ({ label: SHORT[s.site], before: s.before.z, after: s.after.z, lo: s.after.lo, hi: s.after.hi }));
  const shortTitle = (t) => t.replace("Snowpack ", "Pack ").replace(" with ≥ 1 in of water on the ground", " cover");
  const shared = round.fidelity.filter((m) => m.shared.before && m.shared.after)
    .map((m) => ({ label: shortTitle(m.title), before: m.shared.before.z, after: m.shared.after.z, lo: m.shared.after.lo, hi: m.shared.after.hi }));
  const pil = (k, gated) => round.pillows.map((p) => {
    const g = gated && TWO_GATE.has(p.station);
    return { label: p.name, before: p.before[k], after: p.after[k], lo: g ? 0.85 : null, hi: g ? 1.15 : null };
  });
  const ramp = (a, b) => { const pts = []; for (let t = 31; t <= 38.001; t += 0.25) pts.push([t, t <= a ? 0 : t >= b ? 100 : (100 * (t - a)) / (b - a)]); return pts; };
  const dew = round.dew, xmas = round.christmas;
  const qWord = (q) => (q === 0 ? "driest" : q === 50 ? "median" : `${q}th percentile`);
  const dumbbellKey = `<div class="legend"><span class="key"><i class="k-before"></i>Before this round</span><span class="key"><i class="k-after"></i>After</span><span class="key"><i class="k-band"></i>Passes</span></div>`;
  const dewSeries = [
    { word: "Record", cls: "ink", dots: true, pts: dew.hours.map((hh, i) => [hh, dew.record[i]]) },
    { word: "Game before", cls: "l2", dots: true, pts: dew.hours.map((hh, i) => [hh, dew.before[i]]) },
    { word: "Game after", cls: "l1", dots: true, pts: dew.hours.map((hh, i) => [hh, dew.after[i]]) },
  ];
  return `<h2>This round: the four open issues, before and after</h2>
<p>You asked for A1's four open issues to be fixed in this PR, with the model iterating to a steady state: one hypothesis at a time, one set of numbers for every site, no gate loosened. With the snow energy judging them, <b>${round.passes.after} of ${round.passes.of}</b> of 4.05's weather fidelity cells now pass (${round.passes.before} before), and every pillow gate passes (Stevens Pass April 1 was × 0.76). One miss is left. It is explained below as a limit of 20 winters of record. Hover any row or point for its numbers.</p>
<h3>Issues 1 and 2: Winter Park, and the bias every site shared</h3>
<p>The cause was the dew point, not the sun or the clock. In the record, the air dries out in the afternoon and is near saturation at night. The generator's dew depression was flat all day, so its spring afternoons were too humid, and its snow took 30 % more energy than the record's. Rows format 2 adds how far the dew point lags the day's swing (WX-26 v2). The rest closed when the month's rain was fitted over 600 seeds instead of 120: on 120 it had been fitting its own sample's noise.</p>
<div class="two">${lines({ title: "Winter Park in April: the dew depression through the day", sub: "Air temperature minus dew point (°F) over the mean day. The bigger it is, the drier the air and the slower the snow melts.", xs: [0, 21], xWord: "Hour of the day", yWord: "°F", xFmt: (v) => `${v}:00`, series: dewSeries })}
${dumbbell({ title: "The bias every site shared", sub: "The mean z over the seven sites; the band is the pass mark. Two failed before.", rows: shared, d: 2 })}</div>
<div class="legend"><span class="key"><i class="k-ink"></i>Record</span><span class="key"><i class="k-l2"></i>Game before</span><span class="key"><i class="k-l1"></i>Game after</span></div>
${dumbbellKey}
<div class="two">${dumbbell({ title: "Melt-out day, by site", sub: "z: how far the game's median sits from the record's, measured in the record's own noise. Winter Park melted out 14 days early.", rows: fidRows("meltout") })}
${dumbbell({ title: "Snowpack April 1, by site", sub: "z, as on the left.", rows: fidRows("apr1") })}
${dumbbell({ title: "Snowpack January 1, by site", sub: "z, as above.", rows: fidRows("jan1") })}
${dumbbell({ title: "Christmas with 1 in of water on the ground, by site", sub: "z, as above. Winter Park still misses (see below).", rows: fidRows("dec25") })}</div>
<h3>The one miss left: Winter Park's Christmases</h3>
<p>In the game, Winter Park has snow on December 25 in 87 % of winters; the record had it in all 20 of its winters. The gate's noise floor (1 ÷ 20) treats the record's 20 of 20 as exact. The gap isn't melt. The snow that falls by Christmas matches the record's mean (−3 %), but it varies more (sd 2.05 against 1.62 in), and 20 winters can't tell that from chance. A game that is right would show 20 of 20 covered about 6 % of the time. The extra variation comes from the weather engine's temperature anomaly, which remembers too long: its autumns vary up to 1.9 × the record's. A two-timescale anomaly would fix that; it is a follow-up for the weather engine. Fitting the memory over 1–5 days (H4) brought the western sites in line but failed the shared Christmas bias, so it was dropped.</p>
<div class="two">${dumbbell({ title: "Winter Park: snow water fallen by Christmas", sub: "Inches of water that fell as snow from October 1 to December 25. Percentiles of the 20 record winters and of 300 game winters.", rows: xmas.q.map((q, i) => ({ label: qWord(q), before: xmas.record[i], after: xmas.game[i] })), d: 2, unit: " in", names: ["Record", "Game"] })}
${dumbbell({ title: "Winter Park: water on the ground at Christmas", sub: "The same winters: what was left on December 25. The gate asks for 1 in.", rows: xmas.q.map((q, i) => ({ label: qWord(q), before: xmas.kept.record[i], after: xmas.kept.game[i] })), d: 2, unit: " in", names: ["Record", "Game"] })}</div>
<div class="legend"><span class="key"><i class="k-before"></i>Record</span><span class="key"><i class="k-after"></i>Game</span></div>
<h3>Issue 3: Stevens Pass April 1 (the rain/snow line)</h3>
<p>On days the pillows gained snow, the game had called much of it rain. At every western pillow, the half-rain, half-snow point sat at 33.7–35.0 °F wet bulb; the game's ramp put it at 33.8. Eight ramps were tried. Only 32.0 → 37.0 °F (half and half at 34.5) left no pillow gate missing; it becomes WX-03 v4.</p>
<div class="two">${lines({ title: "Rain share of the hour's precipitation", sub: "By wet-bulb temperature. The dots show where each pillow's record puts half and half.", xs: [31, 38], xWord: "Wet bulb (°F)", yWord: "% rain", yMax: 100, yFmt: (v) => fmt(v) + " %",
    series: [{ word: "Before (WX-03 v3: 32.0 → 35.6 °F)", cls: "l2", pts: ramp(round.ramp.old[0], round.ramp.old[1]) }, { word: "After (WX-03 v4: 32.0 → 37.0 °F)", cls: "l1", pts: ramp(round.ramp.now[0], round.ramp.now[1]) }],
    marks: round.ramp.pillows.map(([n, f]) => ({ x: f, y: 50, word: `${n}: half and half at ${f} °F wet bulb` })) })}
${dumbbell({ title: "April 1 water at each pillow", sub: "Modelled ÷ measured, on the pillow's gauge. Gated at Stevens Pass and Tahoe City (shaded band).", rows: pil("apr1", true), d: 2, unit: "×" })}</div>
<div class="legend"><span class="key"><i class="k-l2"></i>Before</span><span class="key"><i class="k-l1"></i>After</span><span class="key"><i class="k-mark"></i>A pillow's half-and-half point</span></div>
<div class="two">${dumbbell({ title: "January 1 water at each pillow", sub: "Modelled ÷ measured. Gated at Stevens Pass and Tahoe City.", rows: pil("jan1", true), d: 2, unit: "×" })}
${dumbbell({ title: "Daily error at each pillow", sub: "Mean absolute daily water error (in). It must match or beat 4.05's reference model, and it does at every pillow.", rows: pil("mae", false), d: 2, unit: " in" })}</div>
${dumbbellKey}
<h3>Issue 4: SN-15, your powder table against the physics</h3>
<p>Re-run under fair conditions (a pack that has lived through the weather, and a light valley wind), the physics still keeps powder warmer than your table (mean +11.6 °F). It falls from north to south the same way, though (spread 16.5 against 14 °F). As planned, your table stays the game's powder rule; the test pins the pattern, and it is back in CI.</p>
<h3>Everything that was tried</h3>
<table><thead><tr><th>Issue</th><th>Hypothesis</th><th>What it did</th><th>Kept</th></tr></thead><tbody>${round.log.map((r) => `<tr><td>${esc(r.issue)}</td><td>${esc(r.what)}</td><td>${esc(r.result)}</td><td>${esc(r.kept)}</td></tr>`).join("")}</tbody></table>
<p>Monthly liquid on fresh seeds, before → after the 600-seed fit: ${round.liquid.map((l) => `${l.site} ${l.month} ${fmt(l.before, 2)} → ${fmt(l.after, 2)} in (record ${fmt(l.target, 2)})`).join("; ")}.</p>`;
}

const missed = data.failures ?? [];
const legendLines = LINES.map((l) => `<span class="key"><i class="k-${l.cls}"></i>${esc(l.word)}</span>`).join("");
const legendFlux = FLUX.map((f) => `<span class="key"><i class="k-${f.cls}"></i>${esc(f.word)}</span>`).join("");
const html = `<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>4.07 A1 · the snow energy against the pillows</title>
<style>
.viz-root{color-scheme:light;--surface-1:#fcfcfb;--text-primary:#0b0b0b;--text-secondary:#52514e;--muted:#898781;--grid:#e1e0d9;--axis:#c3c2b7;--band:#e9f3e9;
--series-1:#2a78d6;--series-2:#eb6834;--series-3:#1baf7a;--series-4:#eda100;--critical:#d03b3b;--z1:#6da7ec;--z2:#2a78d6;--z3:#104281}
@media (prefers-color-scheme:dark){.viz-root{color-scheme:dark;--surface-1:#1a1a19;--text-primary:#f0efec;--text-secondary:#c3c2b7;--grid:#2c2c2a;--axis:#383835;--band:#1f2d1f;
--series-1:#3987e5;--series-2:#d95926;--series-3:#199e70;--series-4:#c98500;--critical:#e66767;--z1:#184f95;--z2:#3987e5;--z3:#9ec5f4}}
body{margin:0;background:var(--surface-1)}
.viz-root{background:var(--surface-1);color:var(--text-primary);font:15px/1.5 system-ui,sans-serif;max-width:1120px;margin:0 auto;padding:28px}
h1{font-size:24px;margin:0 0 4px}h2{font-size:20px;margin:36px 0 6px}h3{font-size:15px;margin:18px 0 4px}p{color:var(--text-secondary);margin:6px 0 14px}
figure{margin:22px 0}figcaption b{display:block;font-size:17px}figcaption span{color:var(--text-secondary);font-size:14px}
svg{width:100%;height:auto;display:block;overflow:visible}.grid{stroke:var(--grid)}.axis{stroke:var(--axis)}.band{fill:var(--band)}
.tick{fill:var(--muted);font-size:11px}.label{fill:var(--text-primary);font-size:13px}.value{fill:var(--text-secondary);font-size:12px}
.miss{fill:var(--critical);color:var(--critical);font-size:12px;font-weight:600}.muted{color:var(--muted)}
.s1{fill:var(--series-1)}.s2{fill:var(--series-2)}.s3{fill:var(--series-3)}.s4{fill:var(--series-4)}.ring{stroke:var(--surface-1);stroke-width:2}
.link{stroke:var(--muted);stroke-width:1.5}
.line{fill:none;stroke-width:2;stroke-linejoin:round}.ink{stroke:var(--text-primary);stroke-width:2.5}.l1{stroke:var(--series-1)}.l2{stroke:var(--series-2)}
.l3{stroke:var(--series-3);stroke-dasharray:5 3}.l4{stroke:var(--series-4);stroke-dasharray:2 3}
.cross{stroke:var(--muted);stroke-width:1}.before{fill:var(--surface-1);stroke:var(--muted);stroke-width:2}.mark{fill:var(--text-primary)}
.dot-ink{fill:var(--text-primary)}.dot-l1{fill:var(--series-1)}.dot-l2{fill:var(--series-2)}
.key i.k-before{background:var(--surface-1);border:2px solid var(--muted);width:10px;height:10px;border-radius:50%}
.key i.k-after{background:var(--series-1);width:12px;height:12px;border-radius:50%}.key i.k-band{background:var(--band);height:12px}.key i.k-mark{background:var(--text-primary);width:10px;height:10px;border-radius:50%}.cover{stroke:var(--muted);stroke-dasharray:3 3}
.z1{stroke:var(--z1)}.z2{stroke:var(--z2)}.z3{stroke:var(--z3)}.band-z1{fill:var(--z1);opacity:.12}.band-z2{fill:var(--z2);opacity:.12}.band-z3{fill:var(--z3);opacity:.12}
.k-z1{background:var(--z1)}.k-z2{background:var(--z2)}.k-z3{background:var(--z3)}
.legend{display:flex;flex-wrap:wrap;gap:8px 18px;margin:8px 0}.key{display:flex;align-items:center;gap:6px;color:var(--text-secondary);font-size:14px}
.key i{width:16px;height:4px;border-radius:2px;display:inline-block}.k-ink{background:var(--text-primary)}.k-l1,.k-s1{background:var(--series-1)}
.k-l2,.k-s2{background:var(--series-2)}.k-l3,.k-s3{background:var(--series-3)}.k-l4,.k-s4{background:var(--series-4)}.k-s1,.k-s2,.k-s3,.k-s4{height:14px;width:14px;border-radius:3px}
.grid3{display:grid;grid-template-columns:repeat(auto-fill,minmax(330px,1fr));gap:16px}
.two{display:grid;grid-template-columns:1fr 1fr;gap:8px 28px}@media (max-width:900px){.two{grid-template-columns:1fr}}
table{border-collapse:collapse;font-size:13px;margin-top:8px}th,td{padding:4px 8px;border-bottom:1px solid var(--grid);text-align:left;vertical-align:top}
td.miss{font-size:13px}ul{color:var(--text-secondary)}li{margin:4px 0}
#tip{position:fixed;pointer-events:none;background:var(--surface-1);color:var(--text-primary);border:1px solid var(--axis);border-radius:6px;padding:6px 9px;font-size:12px;display:none;box-shadow:0 2px 8px #0002}
#tip b{display:block;margin-bottom:2px}#tip span{display:flex;gap:6px;align-items:center}#tip i{width:10px;height:3px;border-radius:2px;display:inline-block}
</style></head><body><main class="viz-root">
<h1>The snow energy against the snow pillows</h1>
<p>Task 4.07 PR A1. ${data.pillows.length} western SNOTEL snow pillows, each played through the new snow energy (sun on the slope, brightness, longwave, warm and humid air, rain, two layers of cold content) with one set of numbers for every site. Three plays: <b>on the gauge</b> (the pillow's own precipitation, hourly, with the weather record lapsed to its height), <b>at the game's clock</b> (the same, stepped in face hours of 2.88 h), and <b>melt only</b> from the measured peak (the melt's own test, free of the gauge's catch). Pass marks are on the median over the winters. The pillow sits in a clearing: ${fmt(data.clearingSky / 10)}% open sky and ${fmt(data.clearingWind / 10)}% of the record's wind.</p>
<p><b>${missed.length === 0 ? "Every gate passes." : `${missed.length} gate${missed.length === 1 ? "" : "s"} missed:`}</b> ${esc(missed.join("; "))}</p>
${roundSection()}
<h2>Pass marks</h2>
<p>Blue dots are the snow energy; orange dots, where shown, are 4.05's reference model (a temperature index) on the same weather, for comparison. Hover a row for its numbers.</p>
<div class="legend"><span class="key"><i class="k-s1"></i>Snow energy (new)</span><span class="key"><i class="k-s2"></i>4.05's reference model</span></div>
<div class="two">${GATES.map(gateChart).join("")}</div>
<details><summary>The pass marks as a table</summary>${summary()}</details>
<h2>The median winter at each pillow</h2>
<p>Water in the pack (inches), October to July, the median over the winters day by day. Hover a chart to read every line on a day.</p>
<div class="legend">${legendLines}</div>
<div class="grid3">${data.pillows.map(seasonChart).join("")}</div>
<h2>Where the energy comes from</h2>
<p>The month's mean flux into the snow surface while there is snow (W/m², median winter; up warms or melts the snow, down cools it). Spring melt is the sun plus warm air, less the night sky's longwave and the cooling of sublimation.</p>
<div class="legend">${legendFlux}</div>
<div class="grid3">${data.pillows.map(energyChart).join("")}</div>
<details><summary>Month-by-month water ledgers (median winter)</summary>${data.pillows.map(ledgerTable).join("")}</details>
${seasonSection()}
<h2>What this run can't say</h2>
<ul>
<li>The gauge play only sees the precipitation the gauge caught. Snow gauges catch less than falls (wind blows snow past them), which is why the gauge plays and the reference model both sit under the pillow in midwinter; the melt-only play is the melt's own test.</li>
<li>The weather is the 4.05 record (NASA POWER on a 0.5° grid) lapsed to each pillow's height, not the pillow's own thermometer, so a warm or cold bias in that record shows up here as early or late melt.</li>
<li>Every pillow is western; the eastern sites (Jackson NH, Snowshoe, Snow Ridge) have no pillow in TestData. Their natural snow is in the season report.</li>
</ul>
<div id="tip"></div>
</main>
<script>
const tip = document.getElementById("tip");
const words = ${JSON.stringify(LINES.map((l) => l.word))}, cls = ${JSON.stringify(LINES.map((l) => l.cls))};
const colour = (c) => getComputedStyle(document.querySelector(".k-" + c)).backgroundColor;
const MONTH_DAY = ${JSON.stringify(MONTH_DAY)}, MONTHS = ${JSON.stringify(MONTHS)};
const dayWord = (d) => { let m = 0; while (m + 1 < MONTH_DAY.length && MONTH_DAY[m + 1] <= d) m++; return MONTHS[m] + " " + (d - MONTH_DAY[m] + 1); };
for (const r of document.querySelectorAll("rect.hover")) {
  const svg = r.ownerSVGElement, cross = svg.querySelector(".cross"), series = JSON.parse(r.dataset.series);
  const ws = r.dataset.words ? JSON.parse(r.dataset.words) : words, cs = r.dataset.cls ? JSON.parse(r.dataset.cls) : cls;
  const left = +r.dataset.left, w = +r.dataset.w, right = +r.dataset.right, days = series[0].length;
  r.addEventListener("mousemove", (e) => {
    const pt = svg.createSVGPoint(); pt.x = e.clientX; pt.y = e.clientY;
    const p = pt.matrixTransform(svg.getScreenCTM().inverse());
    const d = Math.max(0, Math.min(days - 1, Math.round((p.x - left) / (w - left - right) * (days - 1))));
    const cx = left + d / (days - 1) * (w - left - right);
    cross.setAttribute("x1", cx); cross.setAttribute("x2", cx); cross.setAttribute("visibility", "visible");
    tip.innerHTML = "<b>" + dayWord(d) + "</b>" + series.map((s, i) => s[d] == null ? "" : "<span><i style='background:" + colour(cs[i]) + "'></i>" + ws[i] + ": " + s[d].toFixed(1) + " in</span>").join("");
    tip.style.display = "block"; tip.style.left = (e.clientX + 14) + "px"; tip.style.top = (e.clientY + 14) + "px";
  });
  r.addEventListener("mouseleave", () => { tip.style.display = "none"; cross.setAttribute("visibility", "hidden"); });
}
</script></body></html>`;
const out = join(dirname(dataPath), basename(dataPath, ".json") + ".html");
writeFileSync(out, html);
console.log("wrote " + out);
