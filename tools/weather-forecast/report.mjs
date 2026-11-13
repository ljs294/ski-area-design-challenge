// Task 4.05 PR C: the forecast report (demo 93). Reads test-results/weather-forecast/data.json, which
// WeatherForecastReportTests writes, and writes report.html beside it: the forecast's skill by lead at every site at the
// week leans 0.65 and 0.85 (WX-54 v2), the named events a winter brings (WX-41), and the crowd's weather lines (WX-44).
// Usage: node tools/weather-forecast/report.mjs [data.json]
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";

const dataPath = resolve(process.argv[2] ?? "test-results/weather-forecast/data.json");
const data = JSON.parse(readFileSync(dataPath, "utf8"));
const names = {
  "jackson-hole-2km": "Jackson Hole, WY", "olympic-valley-4km": "Olympic Valley, CA", "stevens-pass-2km": "Stevens Pass, WA",
  "winter-park-2km": "Winter Park, CO", "jackson-nh-4km": "Jackson, NH", "snowshoe-2km": "Snowshoe, WV", "snow-ridge-2km": "Snow Ridge, NY",
};
const esc = (s) => String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;");
const fmt = (v, d = 0) => v == null ? "–" : v.toLocaleString("en-US", { minimumFractionDigits: d, maximumFractionDigits: d });
const pct = (v, d = 0) => v == null ? "–" : fmt(100 * v, d) + "%";
const mean = (a) => { const b = a.filter((x) => x != null); return b.length ? b.reduce((x, y) => x + y, 0) / b.length : null; };
const leans = data.leans.map((l) => l.lean);
const sites = data.leans[0].sites.map((s) => s.site);
const at = (lean, site) => data.leans.find((l) => l.lean === lean).sites.find((s) => s.site === site);
const leanWord = (l) => `week lean ${(l / 1000).toFixed(2)}${l === 850 ? " (the game, owner 2026-11-13)" : l === 650 ? " (the first design)" : ""}`;

// ---- grouped horizontal bars: one row per site, one bar per lean, a rule line ----
function siteBars(title, sub, value, max, rule, ruleWord, unit = "%") {
  const rowH = 40, barH = 13, gap = 2, left = 150, right = 54, top = 10, width = 640, plotW = width - left - right;
  const x = (v) => left + (v / max) * plotW;
  const h = top + sites.length * rowH + 30;
  let svg = `<svg viewBox="0 0 ${width} ${h}" role="img" aria-label="${esc(title)}">`;
  for (let t = 0; t <= max + 1e-9; t += max / 5) {
    svg += `<line x1="${x(t)}" x2="${x(t)}" y1="${top}" y2="${h - 26}" class="grid"/>`;
    svg += `<text x="${x(t)}" y="${h - 10}" class="tick" text-anchor="middle">${unit === "%" ? fmt(100 * t) + "%" : fmt(t)}</text>`;
  }
  sites.forEach((st, i) => {
    const y0 = top + i * rowH + 6;
    svg += `<text x="${left - 10}" y="${y0 + 17}" class="label" text-anchor="end">${esc(names[st] ?? st)}</text>`;
    leans.forEach((ln, k) => {
      const v = value(at(ln, st));
      const y = y0 + k * (barH + gap), w = Math.max(0, x(v ?? 0) - left);
      const shown = unit === "%" ? pct(v, 1) : fmt(v, 1) + " " + unit;
      svg += `<g class="hit"><title>${esc(`${names[st]} · ${leanWord(ln)}: ${shown}`)}</title><rect x="${left}" y="${y - 1}" width="${plotW}" height="${barH + 2}" fill="transparent"/>`;
      if (w > 0) svg += `<path d="M${left},${y} h${Math.max(0, w - 4)} a4,4 0 0 1 4,4 v${barH - 8} a4,4 0 0 1 -4,4 h-${Math.max(0, w - 4)} z" class="s${k + 1}"/>`;
      svg += `<text x="${x(v ?? 0) + 6}" y="${y + barH - 2}" class="value">${shown}</text></g>`;
    });
  });
  if (rule != null) svg += `<line x1="${x(rule)}" x2="${x(rule)}" y1="${top}" y2="${h - 26}" class="rule"/><text x="${x(rule) + 4}" y="${top + 8}" class="tick">${esc(ruleWord)}</text>`;
  svg += `<line x1="${left}" x2="${left}" y1="${top}" y2="${h - 26}" class="axis"/></svg>`;
  return `<figure><figcaption><b>${esc(title)}</b><span>${esc(sub)}</span></figcaption>${svg}</figure>`;
}

// ---- a line chart by lead (1-7 days), one line per lean (the mean of the seven sites), whiskers for the site range ----
function byLead(title, sub, key, max, unit, band) {
  const width = 460, h = 250, left = 46, right = 16, top = 14, bottom = 34, plotW = width - left - right, plotH = h - top - bottom;
  const x = (d) => left + ((d - 1) / 6) * plotW, y = (v) => top + plotH - (v / max) * plotH;
  let svg = `<svg viewBox="0 0 ${width} ${h}" role="img" aria-label="${esc(title)}">`;
  for (let t = 0; t <= max + 1e-9; t += max / 5) {
    svg += `<line x1="${left}" x2="${width - right}" y1="${y(t)}" y2="${y(t)}" class="grid"/>`;
    svg += `<text x="${left - 6}" y="${y(t) + 4}" class="tick" text-anchor="end">${unit === "%" ? fmt(t) + "%" : fmt(t)}</text>`;
  }
  for (let d = 1; d <= 7; d++) svg += `<text x="${x(d)}" y="${h - 12}" class="tick" text-anchor="middle">${d === 1 ? "1 day" : d}</text>`;
  if (band) for (const [d, lo, hi] of band) svg += `<line x1="${x(d)}" x2="${x(d)}" y1="${y(hi)}" y2="${y(lo)}" class="band"/>`;
  leans.forEach((ln, k) => {
    const pts = [];
    for (let d = 1; d <= 7; d++) {
      const vs = sites.map((st) => at(ln, st)[key][d - 1]).filter((v) => v != null);
      if (!vs.length) continue;
      pts.push([d, mean(vs), Math.min(...vs), Math.max(...vs)]);
    }
    const off = (k - 0.5) * 5;
    svg += `<path d="${pts.map(([d, m], i) => `${i ? "L" : "M"}${x(d) + off},${y(m)}`).join("")}" class="line l${k + 1}"/>`;
    for (const [d, m, lo, hi] of pts) {
      svg += `<g class="hit"><title>${esc(`${leanWord(ln)}, ${d} day${d > 1 ? "s" : ""} out: ${fmt(m, 1)}${unit} (sites ${fmt(lo, 1)}–${fmt(hi, 1)})`)}</title>` +
        `<line x1="${x(d) + off}" x2="${x(d) + off}" y1="${y(hi)}" y2="${y(lo)}" class="whisker"/><circle cx="${x(d) + off}" cy="${y(m)}" r="4.5" class="dot d${k + 1}"/>` +
        `<rect x="${x(d) + off - 9}" y="${top}" width="18" height="${plotH}" fill="transparent"/></g>`;
    }
  });
  svg += `<line x1="${left}" x2="${width - right}" y1="${y(0)}" y2="${y(0)}" class="axis"/></svg>`;
  return `<figure><figcaption><b>${esc(title)}</b><span>${esc(sub)}</span></figcaption>${svg}</figure>`;
}

// ---- events: a heat table, sites x kinds, a winter's count shaded by its share of the column's largest ----
const kinds = ["Storm", "BigStorm", "StormOfTheSeason", "PowderDay", "Bluebird", "Rain", "FreezingRain", "Thaw", "Refreeze", "ColdSnap", "WindEvent", "Inversion"];
const kindWords = { Storm: "Storms", BigStorm: "Big storms", StormOfTheSeason: "Storm of the season", PowderDay: "Powder days", Bluebird: "Bluebirds", Rain: "Rain days", FreezingRain: "Freezing rain", Thaw: "Thaws", Refreeze: "Refreezes", ColdSnap: "Cold snaps", WindEvent: "Wind events", Inversion: "Inversions" };
function eventTable() {
  const ev = (st) => data.events.find((e) => e.site === st);
  const colMax = Object.fromEntries(kinds.map((k) => [k, Math.max(...sites.map((st) => ev(st).began[k] ?? 0)) || 1]));
  const head = kinds.map((k) => `<th class="rot">${esc(kindWords[k])}</th>`).join("");
  const rows = sites.map((st) => `<tr><th>${esc(names[st])}</th>` + kinds.map((k) => {
    const v = ev(st).began[k] ?? 0, a = v / colMax[k];
    return `<td class="heat" style="--a:${(0.08 + 0.72 * a).toFixed(2)}" title="${esc(`${names[st]}: ${fmt(v, 1)} ${kindWords[k].toLowerCase()} a winter`)}">${fmt(v, v < 10 ? 1 : 0)}</td>`;
  }).join("") + `</tr>`).join("");
  return `<figure><figcaption><b>Named events a winter (WX-41)</b><span>October 1 to April 1, the mean of ${data.winters} winters; each column shaded against its own largest site</span></figcaption><table class="events"><thead><tr><th></th>${head}</tr></thead><tbody>${rows}</tbody></table></figure>`;
}

// ---- the crowd: powder mornings and forecast-cut days per winter, and the mean multiplier ----
function crowdBars() {
  const rowH = 40, barH = 13, gap = 2, left = 150, right = 120, top = 10, width = 640, plotW = width - left - right;
  const cs = sites.map((st) => data.events.find((e) => e.site === st).crowd);
  const max = Math.ceil(Math.max(...cs.flatMap((c) => [c.powderMornings, c.forecastCutDays, c.forecastLiftDays])) / 10) * 10 || 10;
  const x = (v) => left + (v / max) * plotW, h = top + sites.length * rowH + 30;
  let svg = `<svg viewBox="0 0 ${width} ${h}" role="img" aria-label="The crowd's weather days">`;
  for (let t = 0; t <= max; t += max / 5) svg += `<line x1="${x(t)}" x2="${x(t)}" y1="${top}" y2="${h - 26}" class="grid"/><text x="${x(t)}" y="${h - 10}" class="tick" text-anchor="middle">${fmt(t)}</text>`;
  sites.forEach((st, i) => {
    const c = cs[i], y0 = top + i * rowH + 6;
    svg += `<text x="${left - 10}" y="${y0 + 17}" class="label" text-anchor="end">${esc(names[st])}</text>`;
    [["powderMornings", "mornings with new snow at mid (more come)"], ["forecastCutDays", "days the evening sheet keeps some home"]].forEach(([key, word], k) => {
      const v = c[key], y = y0 + k * (barH + gap), w = Math.max(0, x(v) - left);
      svg += `<g class="hit"><title>${esc(`${names[st]}: ${fmt(v, 1)} ${word} a winter`)}</title><rect x="${left}" y="${y - 1}" width="${plotW}" height="${barH + 2}" fill="transparent"/>`;
      if (w > 0) svg += `<path d="M${left},${y} h${Math.max(0, w - 4)} a4,4 0 0 1 4,4 v${barH - 8} a4,4 0 0 1 -4,4 h-${Math.max(0, w - 4)} z" class="s${k + 1}"/>`;
      svg += `<text x="${x(v) + 6}" y="${y + barH - 2}" class="value">${fmt(v, 1)}</text></g>`;
    });
    svg += `<text x="${width - 4}" y="${y0 + 17}" class="label" text-anchor="end">×${c.meanBoth.toFixed(3)}</text>`;
  });
  svg += `<line x1="${left}" x2="${left}" y1="${top}" y2="${h - 26}" class="axis"/></svg>`;
  return `<figure><figcaption><b>The crowd reads the weather (WX-44)</b><span>December to March, a winter's mornings (mean of ${data.winters}); right: the mean of the two lines together over the ${cs[0].days} mornings</span></figcaption>
<div class="legend"><span class="key"><i class="s1"></i>Mornings with new snow at mid (× 1.0 rising to × 1.5 at twice the powder threshold)</span><span class="key"><i class="s2"></i>The evening sheet keeps some home (rain at the base in the ski day × 0.5, a storm gusting over 40 mph × 0.7, a cold snap × 0.7)</span></div>${svg}</figure>`;
}

function table() {
  const head = leans.map((l) => `<th colspan="6">${esc(leanWord(l))}</th>`).join("");
  const sub = leans.map(() => `<th>±2 °F, 1 day</th><th>±5 °F, 7 days</th><th>Right side, 7</th><th>Wrong day, 3</th><th>Busts</th><th>ms an issue</th>`).join("");
  const rows = sites.map((st) => `<tr><th>${esc(names[st])}</th>` + leans.map((l) => {
    const s = at(l, st);
    return `<td>${pct(s.within2[0], 1)}</td><td>${pct(s.within5[6], 1)}</td><td>${pct(s.rightSide7, 1)}</td><td>${pct(s.wrongDay3, 1)}</td><td>${pct(s.busts, 1)}</td><td>${fmt(s.msPerIssue, 2)}</td>`;
  }).join("") + `</tr>`).join("");
  return `<details><summary>The numbers (${data.skillSeeds} seasons a site, every evening sheet December to March)</summary><table><thead><tr><th>Site</th>${head}</tr><tr><th></th>${sub}</tr></thead><tbody>${rows}</tbody></table></details>`;
}

const fails = (l) => sites.filter((st) => at(l, st).within5[6] < 0.9);
const legend = leans.map((l, k) => `<span class="key"><i class="s${k + 1}"></i>${esc(leanWord(l))}</span>`).join("");
const html = `<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>4.05 PR C · the forecast, events and the crowd</title>
<style>
.viz-root{color-scheme:light;--surface-1:#fcfcfb;--text-primary:#0b0b0b;--text-secondary:#52514e;--grid:#e4e3df;--series-1:#2a78d6;--series-2:#eb6834;--heat:42,120,214;--rule:#52514e}
@media (prefers-color-scheme:dark){.viz-root{color-scheme:dark;--surface-1:#1a1a19;--text-primary:#f0efec;--text-secondary:#c3c2b7;--grid:#33322f;--series-1:#3987e5;--series-2:#d95926;--heat:57,135,229;--rule:#c3c2b7}}
body{margin:0;background:var(--surface-1)}
.viz-root{background:var(--surface-1);color:var(--text-primary);font:15px/1.5 system-ui,sans-serif;max-width:1000px;margin:0 auto;padding:28px}
h1{font-size:24px;margin:0 0 4px}h2{font-size:19px;margin:34px 0 4px}p{color:var(--text-secondary);margin:6px 0 14px}
figure{margin:22px 0}figcaption b{display:block;font-size:17px}figcaption span{color:var(--text-secondary);font-size:14px}
svg{width:100%;height:auto;display:block}.grid{stroke:var(--grid)}.axis{stroke:var(--text-secondary)}
.tick{fill:var(--text-secondary);font-size:12px}.label{fill:var(--text-primary);font-size:14px}.value{fill:var(--text-secondary);font-size:12px}
.s1{fill:var(--series-1)}.s2{fill:var(--series-2)}.rule{stroke:var(--rule);stroke-width:2;stroke-dasharray:4 3}
.line{fill:none;stroke-width:2}.l1{stroke:var(--series-1)}.l2{stroke:var(--series-2)}.dot{stroke:var(--surface-1);stroke-width:2}.d1{fill:var(--series-1)}.d2{fill:var(--series-2)}
.whisker{stroke:var(--text-primary);stroke-width:1.5;opacity:.45}.band{stroke:var(--grid);stroke-width:14;stroke-linecap:round}
.hit:hover path,.hit:hover circle{opacity:.8}
.legend{display:flex;flex-wrap:wrap;gap:18px;margin:8px 0}.key{display:flex;align-items:center;gap:6px;color:var(--text-secondary);font-size:14px}
.key i{width:14px;height:14px;border-radius:3px;display:inline-block}.key i.s1{background:var(--series-1)}.key i.s2{background:var(--series-2)}
.two{display:grid;grid-template-columns:1fr 1fr;gap:24px}@media (max-width:900px){.two{grid-template-columns:1fr}}
table{border-collapse:collapse;font-size:13px;margin-top:10px}th,td{padding:4px 8px;border-bottom:1px solid var(--grid);text-align:left}
.events td.heat{text-align:center;background:rgba(var(--heat),var(--a));min-width:44px}.events th.rot{font-weight:500;font-size:12px;vertical-align:bottom;max-width:64px}
.callout{border-left:4px solid var(--series-2);padding:8px 14px;margin:16px 0;color:var(--text-primary)}
ul{color:var(--text-secondary)}li{margin:4px 0}
</style></head><body><main class="viz-root">
<h1>The forecast, the weather's names and the crowd</h1>
<p>Task 4.05 PR C. Every site plays its own frozen weather rows. The forecast is measured over ${data.skillSeeds} seasons a site on every evening sheet from December to March; events and the crowd over ${data.winters} whole winters a site in the game's world.</p>
<div class="callout"><b>The forecast's lean at a week: 0.85</b> (owner, 2026-11-13). A week-out forecast is 85 % the truth and 15 % the normal for the date. At 0.85 ${fails(850).length === 0 ? "every site keeps the 5 °F rule (90 % of days)" : "these miss the 5 °F rule: " + esc(fails(850).map((s) => names[s]).join(", "))}; the first design's 0.65 missed it at ${fails(650).length} of 7 sites, shown beside it for comparison.</div>
<h2>How good the forecast is</h2>
<div class="legend">${legend}</div>
${siteBars("The high within 5 °F, a week out", "Share of 7-day cards; the rule is 90 %", (s) => s.within5[6], 1, 0.9, "90% rule")}
<div class="two">${byLead("The high's miss by days ahead", "Mean absolute error, °F, the mean of the seven sites (the whisker is the site range)", "meanErrF", 8, " °F")}
${byLead("Chance of precipitation on a true storm", "Shown chance by days ahead; the grey marks are the WX-54 targets", "stormChance", 100, "%", [[1, 92, 98], [3, 85, 95], [7, 70, 90]])}</div>
${siteBars("Busts: the storm outside its evening-before range", "Share of storms; the rule is 5 ± 3 %", (s) => s.busts, 0.1, 0.05, "5%")}
${table()}
<h2>What the weather is called</h2>
${eventTable()}
<h2>What the crowd does with it</h2>
<div class="callout"><b>Only rain in the ski day keeps the crowd home</b> (owner, 2026-11-13). A card's rain counts for the crowd when two rain hours in a row at the base fall inside the ski day (8:30 to 4:30), not overnight. Rain events and alerts still count every hour. The sheet now cuts the crowd on ${fmt(Math.min(...sites.map((st) => data.events.find((e) => e.site === st).crowd.forecastCutDays)), 0)}–${fmt(Math.max(...sites.map((st) => data.events.find((e) => e.site === st).crowd.forecastCutDays)), 0)} of ${data.events[0].crowd.days} days, and with the snow mornings counted the weather leaves a winter's crowd at × ${Math.min(...sites.map((st) => data.events.find((e) => e.site === st).crowd.meanBoth)).toFixed(2)}–${Math.max(...sites.map((st) => data.events.find((e) => e.site === st).crowd.meanBoth)).toFixed(2)} of a weatherless one (before: 9–27 days and × 0.72–0.98). The factors themselves are 4.12's to tune.</div>
${crowdBars()}
<h2 style="font-size:17px">What this run can't say</h2>
<ul>
<li>Inversions and freezing rain need the terrain's cold pool; the six sites without terrain in TestData (and Jackson Hole here, played without its package) show none. In the game, on a real area, they come.</li>
<li>The crowd chart counts the mornings each line moves and their mean multiplier, not visits: how many skiers that is depends on the resort and its reputation (4.12's tuning of <code>PowderPull</code>).</li>
<li>Rain on snow, glaze and corn mornings need the snowpack and come with 4.07.</li>
</ul>
</main></body></html>`;
const out = join(dirname(dataPath), "report.html");
writeFileSync(out, html);
console.log("wrote " + out);
