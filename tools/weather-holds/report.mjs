// Task 4.05 PR B1: the wind-hold report (demo 94). Reads test-results/weather-holds/data.json, which
// WeatherHoldReportTests writes, and writes report.html beside it: held hours a season at the summit for every site
// and WindScale, the days with a hold, and the summit's gust spread. PR B2: when steered.json is beside it (the drawn
// resort on Jackson Hole 5 km, MP_JH5_PACKAGE), a section compares B1's height rule with each lift's own steered cell.
// Usage: node tools/weather-holds/report.mjs [data.json]
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";

const dataPath = resolve(process.argv[2] ?? "test-results/weather-holds/data.json");
const data = JSON.parse(readFileSync(dataPath, "utf8"));
const names = {
  "jackson-hole-2km": "Jackson Hole, WY", "olympic-valley-4km": "Olympic Valley, CA", "stevens-pass-2km": "Stevens Pass, WA",
  "winter-park-2km": "Winter Park, CO", "jackson-nh-4km": "Jackson, NH", "snowshoe-2km": "Snowshoe, WV", "snow-ridge-2km": "Snow Ridge, NY",
};
const esc = (s) => String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;");
const fmt = (v, d = 0) => v.toLocaleString("en-US", { minimumFractionDigits: d, maximumFractionDigits: d });
const mean = (a) => a.reduce((x, y) => x + y, 0) / a.length;
const scales = data.scales.map((s) => s.windScale);
const sites = data.scales[0].sites.map((s) => s.site);
const at = (scale, site) => data.scales.find((s) => s.windScale === scale).sites.find((s) => s.site === site);
// lift index 1 = summit fixed-grip quad, 2 = summit detachable quad, 0 = base fixed-grip quad
function stats(scale, site, lift, key = "hours") {
  const s = at(scale, site);
  const v = s.seasons.map((x) => x[key][lift]);
  const open = mean(s.seasons.map((x) => x.openHours));
  return { mean: mean(v), min: Math.min(...v), max: Math.max(...v), open, share: mean(v) / open };
}

// ---- a horizontal grouped bar chart: one row per site, one bar per WindScale, whiskers for the five winters ----
function bars(title, sub, lift, key, unit) {
  const rowH = 54, barH = 12, gap = 2, left = 150, right = 40, top = 10, width = 640;
  const plotW = width - left - right;
  const max = Math.max(...scales.flatMap((sc) => sites.map((st) => stats(sc, st, lift, key).max)));
  const niceMax = Math.ceil(max / 50) * 50 || 10;
  const x = (v) => left + (v / niceMax) * plotW;
  const h = top + sites.length * rowH + 30;
  let svg = `<svg viewBox="0 0 ${width} ${h}" role="img" aria-label="${esc(title)}">`;
  for (let t = 0; t <= niceMax; t += niceMax / 5) {
    svg += `<line x1="${x(t)}" x2="${x(t)}" y1="${top}" y2="${h - 26}" class="grid"/>`;
    svg += `<text x="${x(t)}" y="${h - 10}" class="tick" text-anchor="middle">${fmt(t)}</text>`;
  }
  sites.forEach((st, i) => {
    const y0 = top + i * rowH + 6;
    svg += `<text x="${left - 10}" y="${y0 + 22}" class="label" text-anchor="end">${esc(names[st] ?? st)}</text>`;
    scales.forEach((sc, k) => {
      const s = stats(sc, st, lift, key);
      const y = y0 + k * (barH + gap);
      const w = Math.max(0, x(s.mean) - left);
      const tip = `${names[st]} · WindScale ${(sc / 1000).toFixed(2)}: ${fmt(s.mean, 1)} ${unit} a season (winters ${fmt(s.min)}–${fmt(s.max)})` +
        (key === "hours" ? `, ${fmt(100 * s.share, 0)}% of open hours` : "");
      svg += `<g class="hit"><title>${esc(tip)}</title><rect x="${left}" y="${y - 1}" width="${plotW}" height="${barH + 2}" fill="transparent"/>`;
      if (w > 0) svg += `<path d="M${left},${y} h${Math.max(0, w - 4)} a4,4 0 0 1 4,4 v${barH - 8} a4,4 0 0 1 -4,4 h-${Math.max(0, w - 4)} z" class="s${k + 1}"/>`;
      svg += `<line x1="${x(s.min)}" x2="${x(s.max)}" y1="${y + barH / 2}" y2="${y + barH / 2}" class="whisker"/>`;
      if (k === 0 || s.mean > 0) svg += `<text x="${Math.max(x(s.max), x(s.mean)) + 6}" y="${y + barH - 2}" class="value">${fmt(s.mean, s.mean < 10 ? 1 : 0)}</text>`;
      svg += `</g>`;
    });
  });
  svg += `<line x1="${left}" x2="${left}" y1="${top}" y2="${h - 26}" class="axis"/></svg>`;
  return `<figure><figcaption><b>${esc(title)}</b><span>${esc(sub)}</span></figcaption>${svg}</figure>`;
}

// ---- small multiples: the summit's top gust in open hours at WindScale 1.0, 5 mph bins, with the two hold lines ----
function gusts() {
  const scale = scales[0], w = 300, h = 120, left = 34, bottom = 22, top = 18;
  const cells = sites.map((st) => {
    const s = at(scale, st), bins = s.summitGust, total = bins.reduce((a, b) => a + b, 0) || 1;
    const shares = bins.map((b) => b / total), max = Math.max(...shares);
    const bw = (w - left - 8) / bins.length;
    let svg = `<svg viewBox="0 0 ${w} ${h}" role="img" aria-label="${esc(names[st])} summit gusts">`;
    svg += `<text x="${left}" y="12" class="label">${esc(names[st])}</text>`;
    shares.forEach((v, i) => {
      const bh = (v / max) * (h - top - bottom);
      svg += `<g class="hit"><title>${esc(`${names[st]}: ${i * 5}${i === bins.length - 1 ? "+" : "–" + (i * 5 + 4)} mph in ${fmt(100 * v, 1)}% of open hours`)}</title>` +
        `<rect x="${left + i * bw}" y="${top}" width="${bw}" height="${h - top - bottom}" fill="transparent"/>` +
        (bh > 0 ? `<rect x="${left + i * bw + 1}" y="${h - bottom - bh}" width="${bw - 2}" height="${bh}" rx="2" class="s1"/>` : "") + `</g>`;
    });
    const lim = s.limits;
    for (const [mph, cls, word] of [[lim[2], "hold2", "DQ"], [lim[1], "hold1", "FG"]]) {
      const xx = left + (mph / 5) * bw;
      const before = word === "DQ";   // the two labels sit either side of their lines, so they never overlap
      svg += `<line x1="${xx}" x2="${xx}" y1="${top}" y2="${h - bottom}" class="${cls}"/>` +
        `<text x="${before ? xx - 3 : xx + 3}" y="${top + 10}" class="tick" text-anchor="${before ? "end" : "start"}">${word} ${mph}</text>`;
    }
    for (let m = 0; m <= 75; m += 25) svg += `<text x="${left + (m / 5) * bw}" y="${h - 6}" class="tick" text-anchor="middle">${m}</text>`;
    svg += `</svg>`;
    return `<div class="cell">${svg}</div>`;
  });
  return `<figure><figcaption><b>How hard the summit blows when the lifts run</b><span>Each site's top-station gust in open face hours, WindScale 1.00, in 5 mph bins (mph along the bottom). The lines are the gusts that hold a detachable quad (DQ) and a fixed-grip quad (FG).</span></figcaption><div class="grid7">${cells.join("")}</div></figure>`;
}

// ---- PR B2: the drawn resort's six lifts, B1's height rule against each top station's steered cell ----
const steeredPath = join(dirname(dataPath), "steered.json");
const steered = existsSync(steeredPath) ? JSON.parse(readFileSync(steeredPath, "utf8")) : null;
const classWords = ["deep valley", "valley", "low slope", "slope", "open slope", "upper slope", "ridge", "summit"];

function steeredBars(scale) {
  const sc = steered.scales.find((x) => x.windScale === scale);
  const lifts = steered.lifts;
  const rowH = 44, barH = 14, gap = 3, left = 170, right = 40, top = 10, width = 640, plotW = width - left - right;
  const max = Math.max(1, ...steered.scales.flatMap((x) => [...x.heightRule, ...x.steered]));
  const niceMax = Math.ceil(max / 10) * 10;
  const x = (v) => left + (v / niceMax) * plotW;
  const h = top + lifts.length * rowH + 30;
  let svg = `<svg viewBox="0 0 ${width} ${h}" role="img" aria-label="Held hours by lift at WindScale ${(scale / 1000).toFixed(2)}">`;
  for (let t = 0; t <= niceMax; t += niceMax / 5) {
    svg += `<line x1="${x(t)}" x2="${x(t)}" y1="${top}" y2="${h - 26}" class="grid"/>`;
    svg += `<text x="${x(t)}" y="${h - 10}" class="tick" text-anchor="middle">${fmt(t)}</text>`;
  }
  lifts.forEach((l, i) => {
    const y0 = top + i * rowH + 4;
    svg += `<text x="${left - 10}" y="${y0 + 13}" class="label" text-anchor="end">${esc(l.name)}</text>`;
    svg += `<text x="${left - 10}" y="${y0 + 29}" class="tick" text-anchor="end">${fmt(l.topFeet)} ft · ${esc(classWords[l.exposure] ?? "?")}</text>`;
    [["heightRule", "B1's height rule", 1], ["steered", "its own steered cell", 2]].forEach(([key, word, k], j) => {
      const v = sc[key][i], y = y0 + j * (barH + gap), w = Math.max(0, x(v) - left);
      const tip = `${l.name} (holds at ${l.limitMph} mph), WindScale ${(scale / 1000).toFixed(2)}, ${word}: ${fmt(v, 1)} held face hours a season`;
      svg += `<g class="hit"><title>${esc(tip)}</title><rect x="${left}" y="${y - 1}" width="${plotW}" height="${barH + 2}" fill="transparent"/>`;
      if (w > 0) svg += `<path d="M${left},${y} h${Math.max(0, w - 4)} a4,4 0 0 1 4,4 v${barH - 8} a4,4 0 0 1 -4,4 h-${Math.max(0, w - 4)} z" class="s${k}"/>`;
      svg += `<text x="${x(v) + 6}" y="${y + barH - 3}" class="value">${fmt(v, v < 10 ? 1 : 0)}</text></g>`;
    });
  });
  svg += `<line x1="${left}" x2="${left}" y1="${top}" y2="${h - 26}" class="axis"/></svg>`;
  return `<figure><figcaption><b>WindScale ${(scale / 1000).toFixed(2)}${scale === 850 ? " (today)" : ""}</b><span>Held face hours a season, December to March, mean of ${steered.years} winters</span></figcaption>${svg}</figure>`;
}

function steeredSection() {
  if (!steered) return "";
  const sum = (key, scale) => steered.scales.find((x) => x.windScale === scale)[key].reduce((a, b) => a + b, 0);
  const rows = steered.lifts.map((l, i) => `<tr><th>${esc(l.name)}</th><td>${esc(l.kind === "DetachableChair" ? "detachable" : "fixed grip")}, holds at ${l.limitMph} mph</td><td>${fmt(l.topFeet)} ft</td><td>${fmt(l.hvfFeet)} ft</td><td>${l.exposure} (${esc(classWords[l.exposure] ?? "?")})</td>` +
    steered.scales.map((sc) => `<td>${fmt(sc.heightRule[i], 1)} → ${fmt(sc.steered[i], 1)}</td>`).join("") + `</tr>`).join("");
  return `<h2 style="font-size:20px;margin-top:40px">PR B2: the same weather, each lift on its own ground</h2>
<p>The drawn resort's six lifts on Jackson Hole 5 km (Jackson Hole's frozen rows, ${steered.years} winters). <b>B1's height rule</b> gives a top station the exposure of its height between the area's base (${fmt(steered.baseFeet)} ft) and summit (${fmt(steered.summitFeet)} ft). <b>Its own steered cell</b> is PR B2's: the 8 m cell's exposure class (from its height above the valley floor and the rise around it) × the steered speed for the hour's wind (faster over a crest, × 0.7 behind higher ground upwind). In total the holds barely change (${fmt(sum("heightRule", 850), 1)} → ${fmt(sum("steered", 850), 1)} held hours a season at 0.85), but they move to the lifts whose tops stand in the wind.</p>
<div class="legend"><span class="key"><i class="s1"></i>B1's height rule</span><span class="key"><i class="s2"></i>Its own steered cell (PR B2)</span></div>
<div class="two">${steeredBars(1000)}${steeredBars(850)}</div>
<details><summary>The numbers (held face hours a season: height rule → steered)</summary><table><thead><tr><th>Lift</th><th>Kind</th><th>Top</th><th>Above the valley floor</th><th>Exposure class</th>${steered.scales.map((sc) => `<th>WindScale ${(sc.windScale / 1000).toFixed(2)}</th>`).join("")}</tr></thead><tbody>${rows}</tbody></table></details>`;
}

function table() {
  let rows = "";
  for (const st of sites) {
    const s = at(scales[0], st);
    const c = scales.map((sc) => {
      const fg = stats(sc, st, 1), dq = stats(sc, st, 2), fgd = stats(sc, st, 1, "days"), dqd = stats(sc, st, 2, "days");
      return `<td>${fmt(fg.mean, 1)} h · ${fmt(fgd.mean, 1)} d</td><td>${fmt(dq.mean, 1)} h · ${fmt(dqd.mean, 1)} d</td>`;
    }).join("");
    rows += `<tr><th>${esc(names[st])}</th><td>${fmt(s.baseFeet)}–${fmt(s.summitFeet)} ft</td><td>${fmt(stats(scales[0], st, 1).open, 0)}</td>${c}</tr>`;
  }
  const head = scales.map((sc) => `<th colspan="2">WindScale ${(sc / 1000).toFixed(2)}</th>`).join("");
  const sub = scales.map(() => `<th>Summit FG quad</th><th>Summit DQ</th>`).join("");
  return `<details><summary>The numbers (means of ${data.years} winters: held face hours · days with a hold)</summary><table><thead><tr><th>Site</th><th>Base–summit</th><th>Open hours</th>${head}</tr><tr><th></th><th></th><th></th>${sub}</tr></thead><tbody>${rows}</tbody></table></details>`;
}

const legend = scales.map((sc, k) => `<span class="key"><i class="s${k + 1}"></i>WindScale ${(sc / 1000).toFixed(2)}${sc === 850 ? " (today)" : ""}</span>`).join("");
const baseHeld = scales.map((sc) => sites.reduce((a, st) => a + stats(sc, st, 0).mean, 0));
const html = `<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>4.05 B1 · wind holds by site</title>
<style>
.viz-root{color-scheme:light;--surface-1:#fcfcfb;--text-primary:#0b0b0b;--text-secondary:#52514e;--grid:#e4e3df;--series-1:#2a78d6;--series-2:#eb6834;--series-3:#1baf7a;--hold:#52514e}
@media (prefers-color-scheme:dark){.viz-root{color-scheme:dark;--surface-1:#1a1a19;--text-primary:#f0efec;--text-secondary:#c3c2b7;--grid:#33322f;--series-1:#3987e5;--series-2:#d95926;--series-3:#199e70;--hold:#c3c2b7}}
body{margin:0;background:var(--surface-1)}
.viz-root{background:var(--surface-1);color:var(--text-primary);font:15px/1.5 system-ui,sans-serif;max-width:1000px;margin:0 auto;padding:28px}
h1{font-size:24px;margin:0 0 4px}p{color:var(--text-secondary);margin:6px 0 14px}
figure{margin:26px 0}figcaption b{display:block;font-size:17px}figcaption span{color:var(--text-secondary);font-size:14px}
svg{width:100%;height:auto;display:block}.grid{stroke:var(--grid)}.axis{stroke:var(--text-secondary)}
.tick{fill:var(--text-secondary);font-size:12px}.label{fill:var(--text-primary);font-size:14px}.value{fill:var(--text-secondary);font-size:12px}
.s1{fill:var(--series-1)}.s2{fill:var(--series-2)}.s3{fill:var(--series-3)}.whisker{stroke:var(--text-primary);stroke-width:1.5;opacity:.55}
.hit:hover rect[class]{opacity:.8}.hold1{stroke:var(--hold);stroke-width:2}.hold2{stroke:var(--hold);stroke-width:2;stroke-dasharray:4 3}
.legend{display:flex;gap:18px;margin:8px 0}.key{display:flex;align-items:center;gap:6px;color:var(--text-secondary);font-size:14px}
.key i{width:14px;height:14px;border-radius:3px;display:inline-block}.key i.s1{background:var(--series-1)}.key i.s2{background:var(--series-2)}.key i.s3{background:var(--series-3)}
.grid7{display:grid;grid-template-columns:repeat(auto-fill,minmax(280px,1fr));gap:12px}
.two{display:grid;grid-template-columns:1fr 1fr;gap:24px}@media (max-width:900px){.two{grid-template-columns:1fr}}
table{border-collapse:collapse;font-size:13px;margin-top:10px}th,td{padding:4px 8px;border-bottom:1px solid var(--grid);text-align:left}
ul{color:var(--text-secondary)}li{margin:4px 0}
</style></head><body><main class="viz-root">
<h1>Wind holds by site, and what WindScale does</h1>
<p>Task 4.05 PR B1. Each site plays its own frozen weather rows for ${data.years} winters (December to March, label months) with three lifts: a fixed-grip quad at the base, and a fixed-grip quad (holds at ${at(scales[0], sites[0]).limits[1]} mph) and a detachable quad (holds at ${at(scales[0], sites[0]).limits[2]} mph) at the summit reference point. Bars are the mean held face hours a season; the line through each bar runs from the calmest winter to the windiest. Hover a bar for its share of the open hours.</p>
<div class="legend">${legend}</div>
<div class="two">${bars("Summit fixed-grip quad", "Held face hours a season", 1, "hours", "held hours")}${bars("Summit detachable quad", "Held face hours a season", 2, "hours", "held hours")}</div>
<div class="two">${bars("Summit fixed-grip quad: days", "Days a season with any hold", 1, "days", "days")}${bars("Summit detachable quad: days", "Days a season with any hold", 2, "days", "days")}</div>
${gusts()}
${table()}
${steeredSection()}
<h2 style="font-size:17px">What this run can't say</h2>
<ul>
<li>B1's exposure is one rule at every site: the sheltered class at the base height, the exposed class (×1.6) at the summit height, in steps between. A lift's real exposure (a ridge, a lee bowl, trees) comes with B-fields' per-cell exposure, so a site whose summit is barely above its base (Snow Ridge, ${fmt(at(scales[0], "snow-ridge-2km").summitFeet - at(scales[0], "snow-ridge-2km").baseFeet)} ft) still gets the full summit exposure here.</li>
<li>The base fixed-grip quad never reached its hold gust anywhere: it held ${baseHeld.map((v) => fmt(v, 1)).join(" / ")} face hours a season summed over all seven sites at the three scales.</li>
<li>Only Jackson Hole has terrain in TestData; the other six sites use their rows' own base and summit heights.</li>
</ul>
</main></body></html>`;
const out = join(dirname(dataPath), "report.html");
writeFileSync(out, html);
console.log("wrote " + out);
