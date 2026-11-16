// Task 4.07 PR A2: the live snow report (demo 111). Reads test-results/snow-a2/report.json, which SnowA2ReportTests writes,
// and writes report.html beside it: the season's depth at the base, mid-mountain and the summit at each site, the deepest
// cell against the mean (no cell piles up), the season's water ledger, the depth and conditions maps on five dates, how the
// mean sampler moved the weather's terrain, the budgets, and (when the folder has them) the time-lapse's stills as a flipbook.
// Usage: node tools/snow-a2/report.mjs [test-results/snow-a2/report.json] [time-lapse folder with snow-*.png and run.log]
import { existsSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";

const dataPath = resolve(process.argv[2] ?? "test-results/snow-a2/report.json");
const data = JSON.parse(readFileSync(dataPath, "utf8"));
const outDir = dirname(dataPath);
const lapseDir = resolve(process.argv[3] ?? join(outDir, "lapse"));
const esc = (s) => String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/"/g, "&quot;");
const fmt = (v, d = 0) => (v == null || Number.isNaN(v) ? "–" : v.toLocaleString("en-US", { minimumFractionDigits: d, maximumFractionDigits: d }));
const niceStep = (raw) => { const p = Math.pow(10, Math.floor(Math.log10(raw))), f = raw / p; return (f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10) * p; };

// The conditions palette: Snow.hlsl's SnowTypeColours and InfoLegend's, indexed by SnowType (validated for CVD in A2).
const TYPES = [
  ["Bare", "#8B7458"], ["Powder", "#5AA2EC"], ["Packed powder", "#2BB585"], ["Machine groomed", "#168A5E"], ["Hard packed", "#B77AC9"],
  ["Icy", "#6E2F88"], ["Corn", "#F5C04A"], ["Frozen granular", "#9A4FB0"], ["Loose granular", "#EDA100"], ["Wet granular", "#C47F00"],
  ["Wet powder", "#2C74C6"],
];

// ---- a line chart over the season's face days, one axis, a crosshair and tooltip ----
function lineChart(site, lines, { title, unit, d = 0, w = 520, h = 230, gaps = false } = {}) {
  const days = site.days, n = days.length, left = 44, right = 64, top = 28, bottom = 26;
  const max = Math.max(1, ...lines.flatMap((l) => days.map((x) => x[l.key])));
  const step = niceStep(max / 4), yMax = Math.ceil(max / step) * step;
  const x = (i) => left + (i / Math.max(1, n - 1)) * (w - left - right), y = (v) => top + (1 - v / yMax) * (h - top - bottom);
  let svg = `<svg viewBox="0 0 ${w} ${h}" role="img" aria-label="${esc(site.name + " " + title)}">`;
  svg += `<text x="${left}" y="16" class="label">${esc(site.name)}</text>`;
  for (let t = 0; t <= yMax + 1e-9; t += step)
    svg += `<line x1="${left}" x2="${w - right}" y1="${y(t)}" y2="${y(t)}" class="${t === 0 ? "axis" : "grid"}"/><text x="${left - 6}" y="${y(t) + 4}" class="tick" text-anchor="end">${fmt(t)}</text>`;
  let lastMonth = "";
  days.forEach((dd, i) => {
    const m = dd.label.split(" ")[0];
    if (m !== lastMonth) { lastMonth = m; svg += `<text x="${x(i)}" y="${h - 8}" class="tick">${m}</text>`; }
  });
  // gaps: a 0 is "none" (density with no snow), so the line breaks there instead of dropping to the axis
  for (const l of lines) svg += `<path d="${days.map((dd, i) => gaps && !(dd[l.key] > 0) ? "" : `${i && !(gaps && !(days[i - 1][l.key] > 0)) ? "L" : "M"}${x(i).toFixed(1)},${y(dd[l.key]).toFixed(1)}`).join("")}" class="line ${l.cls}"/>`;
  // direct labels at the end of each line (≤ 4 series), nudged apart so they never overlap
  const last = (l) => { for (let i = n - 1; i >= 0; i--) if (!gaps || days[i][l.key] > 0) return days[i][l.key]; return 0; };
  const ends = lines.map((l) => ({ l, at: y(last(l)) + 4 })).sort((a, b) => a.at - b.at);
  for (let k = 1; k < ends.length; k++) if (ends[k].at - ends[k - 1].at < 13) ends[k].at = ends[k - 1].at + 13;
  ends.forEach((e) => { svg += `<text x="${w - right + 6}" y="${e.at}" class="value">${esc(e.l.short ?? "")}</text>`; });
  const series = lines.map((l) => days.map((dd) => +dd[l.key].toFixed(d)));
  svg += `<line class="cross" x1="0" x2="0" y1="${top}" y2="${h - bottom}" visibility="hidden"/>`;
  svg += `<rect class="hover" x="${left}" y="${top}" width="${w - left - right}" height="${h - top - bottom}" fill="transparent" data-left="${left}" data-right="${right}" data-w="${w}"`
    + ` data-series='${JSON.stringify(series)}' data-labels='${JSON.stringify(days.map((dd) => dd.label))}' data-words='${JSON.stringify(lines.map((l) => l.word))}'`
    + ` data-cls='${JSON.stringify(lines.map((l) => l.cls))}' data-unit="${esc(unit)}" data-d="${d}"/>`;
  return `<div class="cell">${svg + "</svg>"}</div>`;
}

const BANDS = (s) => [
  { key: "summit", word: `Summit (top tenth, ${fmt(s.bands.summit)} ft)`, short: "Summit", cls: "z3" },
  { key: "mid", word: `Mid-mountain (${fmt(s.bands.mid)} ft)`, short: "Mid", cls: "z2" },
  { key: "base", word: `Base (lowest tenth, ${fmt(s.bands.base)} ft)`, short: "Base", cls: "z1" },
];
const PILE = [
  { key: "deepest", word: "The deepest cell", short: "Deepest", cls: "l2" },
  { key: "mean", word: "The mean over the 2 km", short: "Mean", cls: "l1" },
];

// ---- the season's ledger: where the water went, one bar per term (mm of water a cell) ----
const TERMS = [
  ["fell", "Fell (rain and snow)"], ["runoff", "Ran off (melt and rain)"], ["sublimated", "Sublimated"], ["canopy", "Held by the trees"],
  ["water", "Onto lakes"], ["offMap", "Slid or blew off the map"],
];
function ledgerChart(site) {
  const tot = Object.fromEntries(TERMS.map(([k]) => [k, site.days.reduce((a, dd) => a + (dd[k] ?? 0), 0)]));
  const end = site.days[site.days.length - 1].ground, unbalanced = site.days.filter((dd) => dd.balance !== 0).length;
  const rows = [...TERMS.map(([k, word]) => [word, tot[k]]), ["On the ground, April 30", end]];
  const w = 520, rowH = 26, left = 190, right = 70, h = rows.length * rowH + 10, max = Math.max(...rows.map((r) => r[1]), 1);
  let svg = `<svg viewBox="0 0 ${w} ${h}" role="img" aria-label="${esc(site.name)} season ledger">`;
  rows.forEach(([word, v], i) => {
    const y = 4 + i * rowH, bw = Math.max(v > 0 ? 2 : 0, (v / max) * (w - left - right));
    svg += `<g class="hit"><title>${esc(`${word}: ${fmt(v, 1)} mm of water a cell`)}</title><rect x="0" y="${y}" width="${w}" height="${rowH}" fill="transparent"/>`;
    svg += `<text x="${left - 8}" y="${y + 17}" class="label" text-anchor="end">${esc(word)}</text>`;
    svg += `<rect x="${left}" y="${y + 5}" width="${bw}" height="${rowH - 10}" rx="4" class="${i === 0 ? "s1" : i === rows.length - 1 ? "s3" : "s2"}"/>`;
    svg += `<text x="${left + bw + 6}" y="${y + 17}" class="value">${fmt(v, 0)} mm</text></g>`;
  });
  svg += "</svg>";
  return `<div class="cell"><b>${esc(site.name)}</b> <span class="muted">· ${unbalanced === 0 ? "every day balances to the µm" : `${unbalanced} days don't balance`}</span>${svg}</div>`;
}

// ---- the maps: drawn client-side on canvases from the data, depth and type, north up ----
function mapsSection(site, si) {
  return `<h3>${esc(site.name)}</h3><div class="maps">${site.maps.map((m, mi) => `<figure class="map"><figcaption>${esc(m.label)}</figcaption>
<canvas width="${m.size}" height="${m.size}" data-site="${si}" data-map="${mi}" data-kind="depth" aria-label="${esc(`${site.name} depth ${m.label}`)}"></canvas>
<canvas width="${m.size}" height="${m.size}" data-site="${si}" data-map="${mi}" data-kind="type" aria-label="${esc(`${site.name} conditions ${m.label}`)}"></canvas></figure>`).join("")}</div>`;
}

// ---- the time-lapse: the stills and their log lines ----
function lapseSection() {
  if (!existsSync(lapseDir)) return `<p class="muted">No time-lapse folder at ${esc(lapseDir)}: run demo 111's part 3 to fill it.</p>`;
  const stills = readdirSync(lapseDir).filter((f) => /^snow-\d+\.png$/.test(f)).sort();
  if (!stills.length) return `<p class="muted">The time-lapse folder has no stills yet.</p>`;
  const log = existsSync(join(lapseDir, "run.log")) ? readFileSync(join(lapseDir, "run.log"), "utf8") : "";
  const lines = [...log.matchAll(/\[SnowLapse\] still (\d+): (\S+) [^)]*\) core: mean ([\d.]+) in, deepest (\d+) in(?: \([^)]*\))?, (\d+)% covered/g)]
    .map((m) => ({ at: m[2], mean: +m[3], deepest: +m[4], covered: +m[5] }));
  const rel = stills.map((f) => relative(outDir, join(lapseDir, f)).replace(/\\/g, "/"));
  const date = (at) => { const [y, doy] = at.split("-").map(Number), d = new Date(Date.UTC(y, 0, doy)); return d.toLocaleDateString("en-US", { month: "short", day: "numeric", timeZone: "UTC" }); };
  const words = rel.map((_, i) => lines[i] ? `${date(lines[i].at)} · mean ${lines[i].mean} in, deepest ${lines[i].deepest} in, ${lines[i].covered}% of the core under 6 in or more` : `Still ${i}`);
  const movie = existsSync(join(lapseDir, "lapse.mp4")) ? `<p><video controls preload="metadata" style="max-width:100%" src="${esc(relative(outDir, join(lapseDir, "lapse.mp4")).replace(/\\/g, "/"))}"></video></p>` : "";
  return `${movie}<div class="lapse"><img id="lapse" src="${esc(rel[0])}" alt="${esc(words[0])}"><div class="controls"><button id="play">▶ Play</button>
<input id="frame" type="range" min="0" max="${rel.length - 1}" value="0" aria-label="Still"><span id="frameWord">${esc(words[0])}</span></div></div>
<script>const LAPSE = ${JSON.stringify(rel)}, LAPSE_WORDS = ${JSON.stringify(words)};</script>`;
}

function lapseNumbers() {
  const log = existsSync(join(lapseDir, "run.log")) ? readFileSync(join(lapseDir, "run.log"), "utf8") : "";
  const save = log.match(/a save's simulation state: ([\d.]+) MB in (\d+) ms/);
  const up = log.match(/tile uploads: the first frame ([\d.]+) ms; then (\d+) frames, mean ([\d.]+) ms, (\d+) over 0\.2 ms, (\d+) over 1 ms, the most ([\d.]+) ms/);
  const deep = [...log.matchAll(/deepest (\d+) in \(cell \d+, \d+; (-?\d+) m E, (-?\d+) m N; grade (\d+)%\)/g)].pop();
  const done = log.match(/\[SnowLapse\] (\d+) stills saved .* in (\d+) s/);
  return { saveMb: save ? +save[1] : null, saveMs: save ? +save[2] : null, uploadMs: up ? +up[6] : null, upload: up ? { first: +up[1], frames: +up[2], mean: +up[3], over: +up[4], overMs: +up[5], most: +up[6] } : null,
    deep: deep ? { inches: +deep[1], east: +deep[2], north: +deep[3], grade: +deep[4] } : null, days: done ? +done[1] : null, seconds: done ? +done[2] : null };
}

const L = lapseNumbers();
// The Core timing runs (SnowCostReportTests.ReportTheFillAndAWeekJump): new, then WX-02 v3's fill reverted (old), then new again.
const COST = Object.fromEntries(["new-1", "old", "new-2", "shed-before", "zero-after"].map((tag) => [tag, existsSync(join(outDir, `cost-${tag}.json`)) ? JSON.parse(readFileSync(join(outDir, `cost-${tag}.json`), "utf8")) : null]));
const NEW = [COST["new-1"], COST["new-2"]].filter(Boolean);
const mean = (xs) => xs.reduce((a, b) => a + b, 0) / Math.max(1, xs.length);
const newFill = NEW.length ? mean(NEW.map((c) => c.fillMeanMs)) : null, newWeek = NEW.length ? mean(NEW.map((c) => c.weekS)) : null;
const newDay = NEW.length ? Math.max(...NEW.map((c) => c.dayWorstS)) : null;
const NOW = COST["zero-after"], BEFORE = COST["shed-before"] ?? NEW[0];
const nowWeek = NOW ? NOW.weekS : newWeek, nowDayWorst = NOW ? NOW.dayWorstS : newDay, nowDayMedian = NOW ? NOW.dayMedianS : null;
// One bar chart of named rows (one measure, one unit).
function barRows(rows, unit, d, title) {
  rows = rows.filter((r) => r[1] != null);
  if (!rows.length) return "";
  const w = 520, rowH = 30, left = 300, right = 80, h = rows.length * rowH + 30, max = Math.max(...rows.map((r) => r[1]));
  let svg = `<svg viewBox="0 0 ${w} ${h}" role="img" aria-label="${esc(title)}"><text x="0" y="16" class="label">${esc(title)}</text>`;
  rows.forEach(([word, v, cls], i) => {
    const y = 24 + i * rowH, bw = Math.max(2, (v / max) * (w - left - right));
    svg += `<g class="hit"><title>${esc(`${word}: ${fmt(v, d)}${unit}`)}</title><rect x="0" y="${y}" width="${w}" height="${rowH}" fill="transparent"/>`;
    svg += `<text x="${left - 8}" y="${y + 19}" class="label" text-anchor="end">${esc(word)}</text>`;
    svg += `<rect x="${left}" y="${y + 6}" width="${bw}" height="${rowH - 12}" rx="4" class="${cls}"/>`;
    svg += `<text x="${left + bw + 6}" y="${y + 19}" class="value">${fmt(v, d)}${esc(unit)}</text></g>`;
  });
  return `<div class="cell">${svg}</svg></div>`;
}
function costChart(key, unit, d, title) {
  const rows = [["Before WX-02 v3 (fills only the pairs the area has)", COST.old?.[key], "s2"], ["WX-02 v3, first run", COST["new-1"]?.[key], "s1"], ["WX-02 v3, second run", COST["new-2"]?.[key], "s1"]].filter((r) => r[1] != null);
  if (!rows.length) return `<p class="muted">No ${esc(title)} run yet.</p>`;
  const w = 520, rowH = 30, left = 300, right = 80, h = rows.length * rowH + 30, max = Math.max(...rows.map((r) => r[1]));
  let svg = `<svg viewBox="0 0 ${w} ${h}" role="img" aria-label="${esc(title)}"><text x="0" y="16" class="label">${esc(title)}</text>`;
  rows.forEach(([word, v, cls], i) => {
    const y = 24 + i * rowH, bw = Math.max(2, (v / max) * (w - left - right));
    svg += `<g class="hit"><title>${esc(`${word}: ${fmt(v, d)}${unit}`)}</title><rect x="0" y="${y}" width="${w}" height="${rowH}" fill="transparent"/>`;
    svg += `<text x="${left - 8}" y="${y + 19}" class="label" text-anchor="end">${esc(word)}</text>`;
    svg += `<rect x="${left}" y="${y + 6}" width="${bw}" height="${rowH - 12}" rx="4" class="${cls}"/>`;
    svg += `<text x="${left + bw + 6}" y="${y + 19}" class="value">${fmt(v, d)}${esc(unit)}</text></g>`;
  });
  return `<div class="cell">${svg}</svg></div>`;
}
const skipsLog = existsSync(join(lapseDir, "run.log")) ? readFileSync(join(lapseDir, "run.log"), "utf8").match(/the skips to morning \(BG-02 v4: ≤ 5 s\): (\d+), median ([\d.]+) s, p90 ([\d.]+) s, the most ([\d.]+) s/) : null;
const SKIP = skipsLog ? { n: +skipsLog[1], median: +skipsLog[2], p90: +skipsLog[3], most: +skipsLog[4] } : null;
const DENS = (s) => [
  { key: "dSummit", word: `Summit (${fmt(s.bands.summit)} ft)`, short: "Summit", cls: "z3" },
  { key: "dMid", word: `Mid-mountain (${fmt(s.bands.mid)} ft)`, short: "Mid", cls: "z2" },
  { key: "dBase", word: `Base (${fmt(s.bands.base)} ft)`, short: "Base", cls: "z1" },
];
const hasDensity = data.sites.every((s) => s.days.length && s.days[0].dBase != null);
const img = (f) => existsSync(join(outDir, f)) ? f : null;
// The snow hour's phases over the timed week (a temporary profile build, never shipped): the last PROFILE line in either log.
const PROFS = (() => {
  const found = {};
  for (const f of ["profile-and-report.log", "profile2.log"]) {
    if (!existsSync(join(outDir, f))) continue;
    for (const m of readFileSync(join(outDir, f), "utf8").matchAll(/\[cost:([\w-]+)\] PROFILE (\d+) hours \((\d+) wet, (\d+) drifting\): pass A ([\d.]+) s, shed scatter ([\d.]+) s, drift ([\d.]+) s, pass C ([\d.]+) s/g))
      found[m[1]] = { hours: +m[2], wet: +m[3], drifting: +m[4], a: +m[5], shed: +m[6], drift: +m[7], c: +m[8] };
  }
  return found;
})();
const PROF = PROFS["profile-zero"] ?? PROFS["profile"] ?? PROFS["profile2"] ?? null, PROF_BEFORE = PROFS["profile"] ?? PROFS["profile2"] ?? null;
// The game's skips before the fix: the time between the quiet lapse's stills (a whole face day's skip plus about 1 s of capture).
function gapsOf(f) {
  if (!existsSync(f)) return null;
  const text = readFileSync(f, "utf8");
  const at = [...text.matchAll(/still (\d+): \S+ \S+ \((\d+) s\)/g)].map((m) => +m[2]);
  const gaps = at.slice(1).map((v, i) => v - at[i]).sort((a, b) => a - b);
  const done = text.match(/\[SnowLapse\] (\d+) stills saved .* in (\d+) s/);
  return gaps.length ? { n: gaps.length, median: gaps[gaps.length >> 1], most: gaps[gaps.length - 1], total: done ? +done[2] : null } : null;
}
const GAME = gapsOf(join(outDir, "lapse-quiet", "run.log"));
const GAME_AFTER = resolve(lapseDir) === resolve(join(outDir, "lapse-quiet")) ? null : gapsOf(join(lapseDir, "run.log"));
const cropSave = data.sites[0].saveBytes / data.sites[0].cells;   // bytes a cell, mid-winter, every cell snowy
const BUDGETS = [
  ["A face hour at 11 km (1.89 M cells, all snowy)", "the snow threads, never the frame", PROF ? `over a timed week (.NET, 4 threads): the read, sun and energy ${fmt(PROF.c * 1000 / PROF.hours, 0)} ms an hour; a wet hour's shed walk (one thread) ${fmt(PROF.shed * 1000 / Math.max(1, PROF.wet), 0)} ms more` : "407 ms on 1 thread, 155 ms on 4 (the read, sun and energy only)", "—", "BG-02 v4"],
  ["The weather's hourly fill at 11 km (WX-02 v3)", "inside the face hour", newFill != null ? `${fmt(newFill, 2)} ms mean${COST.old ? `, against ${fmt(COST.old.fillMeanMs, 2)} ms before` : ""}` : "not run", newFill != null ? "✓" : "—", "the worker's hour"],
  ["Skip to morning at 11 km (16 face hours)", "≤ 5 s", [nowDayWorst != null ? `Core, every cell under a metre: ${nowDayMedian != null ? `${fmt(nowDayMedian * 16 / 24, 1)} s median, ` : ""}${fmt(nowDayWorst * 16 / 24, 1)} s on the worst day` : null, SKIP ? `in the game with the fix (Mono; each skip a whole face day, 07:30 to 07:30): median ${fmt(SKIP.median, 1)} s, p90 ${fmt(SKIP.p90, 1)} s, the most ${fmt(SKIP.most, 1)} s over ${SKIP.n} skips, so about ${fmt(SKIP.median * 16 / 24, 1)} s and ${fmt(SKIP.most * 16 / 24, 1)} s for 16 face hours${GAME ? `; before the fix ${fmt(GAME.median)} s and ${fmt(GAME.most)} s a whole day` : ""}` : GAME ? `in the game before the shed fix (a whole face day, Mono): ${fmt(GAME.median)} s median, ${fmt(GAME.most)} s the worst; not re-measured yet (no player build: Unity's licence)` : null].filter(Boolean).join("; ") || "2.5 s (projected)", SKIP ? (SKIP.most * 16 / 24 <= 5 ? "✓" : SKIP.median * 16 / 24 <= 5 ? "over on the worst days" : "over") : nowDayWorst != null ? (nowDayWorst * 16 / 24 <= 5 ? "Core ✓, game to measure" : "Core over on the worst day; game to measure") : "—", "BG-02 v4"],
  ["Skip a month at 11 km", "≤ 60 s", nowWeek != null ? `a face week ${fmt(nowWeek, 1)} s (Core) → a month (about 10.8 face days) about ${fmt(nowWeek / 7 * 10.8, 0)} s` : "39 s (projected)", nowWeek != null ? (nowWeek / 7 * 10.8 <= 60 ? "Core ✓, game to measure" : "over") : "—", "BG-02 v4"],
  ["The live field's memory at 11 km", "≤ 64 MB", "59 MB (31 B a cell)", "✓", "BG-08 v3"],
  ["A mid-winter save's simulation state",  "10–20 MB expected (plan)", `${fmt(cropSave, 1)} B a cell on the crops → about ${fmt(cropSave * 1.89, 0)} MB at 11 km${L.saveMb ? `; the time-lapse's save: ${fmt(L.saveMb, 1)} MB in ${fmt(L.saveMs)} ms` : ""}`, "over the guess", "the plan's estimate, not a rule"],
  ["Screen tile uploads a frame", "≤ 0.2 ms", L.upload ? `mean ${fmt(L.upload.mean, 3)} ms over ${fmt(L.upload.frames)} frames with tiles; ${fmt(L.upload.over)} over 0.2 ms (${fmt(100 * L.upload.over / Math.max(1, L.upload.frames), 1)}%), the most ${fmt(L.upload.most, 2)} ms; the first frame ${fmt(L.upload.first, 2)} ms` : "run part 3", L.upload ? (L.upload.most <= 0.2 ? "✓" : L.upload.mean <= 0.2 ? "mean ✓, a few frames over" : "over") : "—", "BG-11; measured while the snow skips to morning, every core busy"],
];

const html = `<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>4.07 A2 · the live snow</title>
<style>
.viz-root{color-scheme:light;--surface-1:#fcfcfb;--text-primary:#0b0b0b;--text-secondary:#52514e;--muted:#898781;--grid:#e1e0d9;--axis:#c3c2b7;
--series-1:#2a78d6;--series-2:#eb6834;--series-3:#1baf7a;--z1:#6da7ec;--z2:#2a78d6;--z3:#104281}
@media (prefers-color-scheme:dark){.viz-root{color-scheme:dark;--surface-1:#1a1a19;--text-primary:#f0efec;--text-secondary:#c3c2b7;--grid:#2c2c2a;--axis:#383835;
--series-1:#3987e5;--series-2:#d95926;--series-3:#199e70;--z1:#184f95;--z2:#3987e5;--z3:#9ec5f4}}
body{margin:0;background:var(--surface-1)}
.viz-root{background:var(--surface-1);color:var(--text-primary);font:15px/1.5 system-ui,sans-serif;max-width:1180px;margin:0 auto;padding:28px}
h1{font-size:24px;margin:0 0 4px}h2{font-size:20px;margin:36px 0 6px}h3{font-size:15px;margin:18px 0 4px}p{color:var(--text-secondary);margin:6px 0 14px}
svg{width:100%;height:auto;display:block;overflow:visible}.grid{stroke:var(--grid)}.axis{stroke:var(--axis)}
.tick{fill:var(--muted);font-size:11px}.label{fill:var(--text-primary);font-size:13px}.value{fill:var(--text-secondary);font-size:12px}.muted{color:var(--muted)}
.s1{fill:var(--series-1)}.s2{fill:var(--series-2)}.s3{fill:var(--series-3)}
.line{fill:none;stroke-width:2;stroke-linejoin:round}.l1{stroke:var(--series-1)}.l2{stroke:var(--series-2)}.z1{stroke:var(--z1)}.z2{stroke:var(--z2)}.z3{stroke:var(--z3)}
.cross{stroke:var(--muted);stroke-width:1}
.legend{display:flex;flex-wrap:wrap;gap:8px 18px;margin:8px 0}.key{display:flex;align-items:center;gap:6px;color:var(--text-secondary);font-size:14px}
.key i{width:16px;height:4px;border-radius:2px;display:inline-block}.k-l1{background:var(--series-1)}.k-l2{background:var(--series-2)}
.k-z1{background:var(--z1)}.k-z2{background:var(--z2)}.k-z3{background:var(--z3)}.key i.sw{width:14px;height:14px;border-radius:3px}
.two{display:grid;grid-template-columns:1fr 1fr;gap:8px 28px}@media (max-width:900px){.two{grid-template-columns:1fr}}
.maps{display:grid;grid-template-columns:repeat(5,1fr);gap:10px}.map{margin:0}.map figcaption{font-size:13px;color:var(--text-secondary)}
.map canvas{width:100%;image-rendering:auto;display:block;border-radius:4px;margin-bottom:4px;cursor:crosshair}
.key i.ramp{display:inline-block;width:160px;height:12px;border-radius:3px;vertical-align:middle}
table{border-collapse:collapse;font-size:13px;margin-top:8px}th,td{padding:4px 8px;border-bottom:1px solid var(--grid);text-align:left;vertical-align:top}
.tiles{display:flex;flex-wrap:wrap;gap:12px}.tile{border:1px solid var(--grid);border-radius:8px;padding:10px 14px;min-width:200px}
.tile b{display:block;font-size:22px}.tile span{color:var(--text-secondary);font-size:13px}
.lapse img{width:100%;border-radius:6px;display:block}.shot{margin:10px 0 18px}.shot img{width:100%;border-radius:6px;display:block}.shot figcaption{font-size:13px;color:var(--text-secondary);margin-top:4px}.controls{display:flex;gap:12px;align-items:center;margin:8px 0}.controls input{flex:1}
.controls button{font:inherit;padding:4px 12px;border-radius:6px;border:1px solid var(--axis);background:var(--surface-1);color:var(--text-primary);cursor:pointer}
#tip{position:fixed;pointer-events:none;background:var(--surface-1);color:var(--text-primary);border:1px solid var(--axis);border-radius:6px;padding:6px 9px;font-size:12px;display:none;box-shadow:0 2px 8px #0002;z-index:9}
#tip b{display:block;margin-bottom:2px}#tip span{display:flex;gap:6px;align-items:center}#tip i{width:10px;height:3px;border-radius:2px;display:inline-block}
</style></head><body><main class="viz-root">
<h1>The live snow: a season on real ground</h1>
<p>Task 4.07 PR A2, demo 111 (made ${esc(data.made)}). A new game starts on bare ground on October 1. From there the snow builds storm by storm, slides off steep rock into run-out fans, blows off ridges into hollows, settles, and melts by sun, air and rain. This is the game's own simulation, run without Unity on the 2 km round the centre of ${data.sites.map((s) => esc(s.name)).join(" and ")}, at 8 m a cell, under each area's frozen weather rows.</p>
<h2>Depth through the season</h2>
<p>The snow's depth (inches) each face day, as the mean over the lowest tenth of the cells, the middle tenth and the highest tenth by height. Hover a chart to read a day.</p>
<div class="legend"><span class="key"><i class="k-z3"></i>Summit</span><span class="key"><i class="k-z2"></i>Mid-mountain</span><span class="key"><i class="k-z1"></i>Base</span></div>
<div class="two">${data.sites.map((s) => lineChart(s, BANDS(s), { title: "depth by height", unit: " in", d: 1 })).join("")}</div>
<h2>No cell piles up</h2>
<p>The deepest single cell against the mean (inches). Before this round, snow shed off a cliff landed whole in one cell: 2,580 in (the 65 m cap) by late October in the game at 11 km, and 1,881 in on Olympic Valley's crop. Now it spreads over a run-out fan (SN-06 v2), and snow that slides or blows past the map's edge leaves it. A fan under a big cliff is still several times the mean, as avalanche debris is.</p>
<div class="legend"><span class="key"><i class="k-l2"></i>The deepest cell</span><span class="key"><i class="k-l1"></i>The mean</span></div>
<div class="two">${data.sites.map((s) => lineChart(s, PILE, { title: "deepest against mean", unit: " in", d: 0 })).join("")}</div>
<h2>Where the water went</h2>
<p>The season's ledger, October 1 to April 30, in mm of water a cell. What fell is what ran off, sublimated, stayed in the trees, went to lakes or off the map, plus what is still on the ground. Every day must balance to the µm, and does.</p>
<div class="two">${data.sites.map(ledgerChart).join("")}</div>
<h2>The maps on five dates</h2>
<p>Each pair is the depth (top) and the Snow conditions layer's colours (bottom), north up, 2 km across. Hover a map to read a cell. The game draws both through the same curved filter, so they look as smooth as here.</p>
<div class="legend"><span class="key"><i class="ramp" style="background:linear-gradient(90deg,#8B7458 0 6%,#eef4fb 6%,#9cc3ee 35%,#2a78d6 65%,#0b2d5c)"></i>Depth: bare, then 0 → 120 in and more</span>
${[1, 10, 2, 7, 5, 6, 9, 0].map((t) => `<span class="key"><i class="sw" style="background:${TYPES[t][1]}"></i>${TYPES[t][0]}</span>`).join("")}</div>
${data.sites.map(mapsSection).join("")}
<h2>The snow line follows the ground</h2>
<p>Early-season snow survives only where a little more fell, so the bare edge traces the precipitation pattern. That pattern was one windward or leeward factor per 512 m block, blended in straight lines, so the edge ran straight across the foot of Jackson Hole. WX-35 v3 and WX-38 v3 judge each 8 m cell by its own height against the upwind crest: gullies sit deeper in the shadow than ridges, and the edge bends with the contours. WX-02 v3 then blends the snow's read across every height bin and pool band, so an even slope shows no step.</p>
${[["snowline-cause.png", "The cause, November 15 on the 2 km crop (depth, 100 ft contours, black = bare). Left: as built, the bare edge cuts straight across about ten contours. Right: the 512 m precipitation pattern made flat, and the edge follows the contours."], ["snowline-crop-before-after.png", "The fix on the same crop (brown = bare), left to right: October 16 old, October 16 new, November 15 old, November 15 new."], ["snowline-ingame-before-after-013.png", "In the game, Jackson Hole on November 7 from the same camera: before (left) and after (right). The edge breaks up along the gullies and ridges."]].filter(([f]) => img(f)).map(([f, cap]) => `<figure class="shot"><img src="${esc(f)}" alt="${esc(cap)}"><figcaption>${esc(cap)}</figcaption></figure>`).join("")}
${hasDensity ? `<h2>How dense the snow is</h2>
<p>SN-08 v2: new snow lands light when it's cold (about 60 kg/m³ at 12 °F) and heavy when it's wet (about 125 at 33 °F), then settles toward 300 kg/m³ dry, 380 damp and 450 wet, keeping its water exactly. This is the bulk density (the band's water over its depth) each face day; a drop is a fresh fall on top. The cursor's readout shows the same as water: "Powder 14 in · 1.4 in water".</p>
<div class="legend"><span class="key"><i class="k-z3"></i>Summit</span><span class="key"><i class="k-z2"></i>Mid-mountain</span><span class="key"><i class="k-z1"></i>Base</span></div>
<div class="two">${data.sites.map((s) => lineChart(s, DENS(s), { title: "bulk density by height", unit: " kg/m³", d: 0, gaps: true })).join("")}</div>` : ""}
<h2>The skip's speed: the shed walk (BG-02 v4)</h2>
<p>The budgets were projections from the read, sun and energy alone. Timed on the whole hour (11 km, every cell under a metre in January, Core on .NET with four snow threads), a face week took about a minute: two thirds of it the shed walk (SN-06), which runs on one thread. Most of a busy hour's fans carried a walk's last few µm to the end of its run-on and read the pile cap nine times for nothing. A fan whose shares all round to nothing now returns at once, and a cell that has taken the hour's pile max skips the pile cap. The snow is the same to the byte: the whole field folds the same after the week${NOW && BEFORE?.fold ? ` (${esc(NOW.fold)}${NOW.fold === BEFORE.fold ? ", before and after" : ", which differs from before: " + esc(BEFORE.fold)})` : ""}.</p>
<div class="two">${barRows([["Before the fix", BEFORE?.weekS, "s2"], ["With the fix", NOW?.weekS, "s1"]], " s", 1, "A face week's jump at 11 km")}${barRows([["Before the fix, the worst day", BEFORE?.dayWorstS, "s2"], ["With the fix, the worst day", NOW?.dayWorstS, "s1"], ["Before the fix, the median day", BEFORE?.dayMedianS, "s2"], ["With the fix, the median day", NOW?.dayMedianS, "s1"]], " s", 2, "A face day's jump at 11 km")}</div>
${PROF_BEFORE && PROFS["profile-zero"] ? `<div class="two">${barRows([["Shed walk, before", PROF_BEFORE.shed, "s2"], ["Shed walk, with the fix", PROFS["profile-zero"].shed, "s1"], ["Read, sun and energy (pass C), before", PROF_BEFORE.c, "s2"], ["Read, sun and energy (pass C), with the fix", PROFS["profile-zero"].c, "s1"], ["What leaves each cell (pass A), with the fix", PROFS["profile-zero"].a, "s1"]], " s", 1, "Where the week's time goes (a profile build)")}</div>` : ""}
${GAME && GAME_AFTER ? `<p>In the game (Mono, the real Jackson Hole at 11 km, the same 45-day lapse from bare October 1): the time between stills (a whole face day's skip, plus the pause and the still) before and after the fix. The snow on the last day is the same (mean, deepest and the water on the ground).${SKIP ? ` Timed on its own, a skip with the fix takes ${fmt(SKIP.median, 1)} s at the median and ${fmt(SKIP.most, 1)} s at the worst, about ${fmt(SKIP.median * 16 / 24, 1)} s and ${fmt(SKIP.most * 16 / 24, 1)} s for the budget's 16 face hours: still over 5 s. The game runs the hour about 2.7 times slower than Core on .NET.` : ""}</p>
<div class="two">${barRows([["Before the fix, the median day", GAME.median, "s2"], ["With the fix, the median day", GAME_AFTER.median, "s1"], ["Before the fix, the worst day", GAME.most, "s2"], ["With the fix, the worst day", GAME_AFTER.most, "s1"]], " s", 0, "In the game: a face day between stills")}${barRows([["Before the fix", GAME.total, "s2"], ["With the fix", GAME_AFTER.total, "s1"]], " s", 0, "In the game: the whole 45-day lapse")}</div>` : GAME ? `<p>In the game (Mono, real Jackson Hole at 11 km) before the fix, a whole face day's skip took ${fmt(GAME.median)} s at the median and ${fmt(GAME.most)} s at the worst over ${GAME.n} days.</p>` : ""}
<h2>The weather's read, WX-02 v3: what it costs</h2>
<p>Each face hour now also fills every height-bin and pool-band pair beside one the area has, so the snow's blend always has its neighbour. Timed at 11 km (1,375 × 1,375 cells) without Unity, on the quiet machine, through the GPU queue's measure lane. The fill runs once per face hour on the worker; the week is a whole world with the live snow (a metre seeded everywhere in January) jumping seven face days as a skip does.</p>
<div class="two">${costChart("fillMeanMs", " ms", 2, "The hourly fill, mean over 48 January hours")}${costChart("weekS", " s", 1, "A face week's jump with the live snow")}</div>
${COST.old && newFill != null ? `<p>The fill: ${fmt(COST.old.fillMeanMs, 2)} ms before, ${fmt(newFill, 2)} ms now (worst hour ${fmt(Math.max(...NEW.map((c) => c.fillWorstMs)), 2)} ms). The week: ${fmt(COST.old.weekS, 1)} s before, ${fmt(newWeek, 1)} s now; the worst face day ${fmt(newDay, 2)} s, all before the shed walk's fix above. WX-02 v3 itself costs nothing you could measure.</p>` : ""}
<h2>A finding, not changed: a thaw on thin snow</h2>
<p>A thaw on thin snow can split neighbouring cells by about 1 mm of water. The weather's wet bulb is whole tenths of a degree, so new snow's temperature differs by 0.05 °C between some rows; melt and refreeze carry that on, and SN-09 v3's faster melt of thin snow triples it (973 µm in one December thaw on the test slope, 346 µm without that rule). That's under half an inch of snow on a 32 mm pack, too little to see on the map. It's recorded in the rules (p4-02) for a later look.</p>
<h2>The weather's terrain under the new sampler</h2>
<p>SN-01 v2 takes each 8 m cell as the mean of its ground, not one post at its centre. The weather reads the same cells, so its inputs shift slightly. This is how much, on each crop. No format or golden moved for it.</p>
<div class="tiles">${data.sites.map((s) => `<div class="tile"><span>${esc(s.name)}: cells in a different 33 ft height bin</span><b>${fmt(100 * s.weatherShift.bins / s.weatherShift.cells, 1)}%</b>
<span>exposure class changed in ${fmt(100 * s.weatherShift.exposure / s.weatherShift.cells, 1)}% (by at most ${s.weatherShift.exposureMost} class), mean height ${fmt(s.weatherShift.meanFeetShift, 2)} ft</span></div>`).join("")}</div>
<h2>Budgets</h2>
<table><thead><tr><th>What</th><th>Target</th><th>Measured</th><th></th><th>Rule</th></tr></thead><tbody>
${BUDGETS.map((b) => `<tr>${b.map((c) => `<td>${esc(c)}</td>`).join("")}</tr>`).join("")}</tbody></table>
<p>The save is bigger than the plan guessed: the snow block keeps every snowy cell's whole pack (the precision the pillows passed), coded as differences along rows. Today's static snow block is 3.8 MB.</p>
<h2>The time-lapse (Jackson Hole, 11 km, in the game)</h2>
<p>One still each face day from a fixed view at 12:30, from bare October ground. Press play, or drag.</p>
${lapseSection()}
${L.deep ? `<p>The deepest cell at the end of the time-lapse: ${fmt(L.deep.inches)} in, ${fmt(Math.abs(L.deep.east))} m ${L.deep.east < 0 ? "west" : "east"} and ${fmt(Math.abs(L.deep.north))} m ${L.deep.north < 0 ? "south" : "north"} of the centre, on ground at ${fmt(L.deep.grade)}% grade: the foot of a cliff band, where the shed's run-out fan lands every storm.</p>` : ""}
<details><summary>The season as a table</summary>${data.sites.map((s) => `<h3>${esc(s.name)}</h3><table><thead><tr><th>Day</th><th>Base</th><th>Mid</th><th>Summit</th><th>Mean</th><th>Deepest</th><th>Covered</th><th>Fell</th><th>Ran off</th></tr></thead><tbody>${s.days.map((dd) => `<tr><td>${esc(dd.label)}</td><td>${fmt(dd.base, 1)}</td><td>${fmt(dd.mid, 1)}</td><td>${fmt(dd.summit, 1)}</td><td>${fmt(dd.mean, 1)}</td><td>${fmt(dd.deepest)}</td><td>${fmt(dd.covered)}%</td><td>${fmt(dd.fell, 1)}</td><td>${fmt(dd.runoff, 1)}</td></tr>`).join("")}</tbody></table>`).join("")}</details>
<h2>What this run can't say</h2>
<ul>
<li>The crops are 2 km with no canopy and no lakes; the game adds the trees (they hold back up to 30 % of a fall) and 4.09's lakes.</li>
<li>The weather is each area's typical frozen rows, one generated winter, not a record winter.</li>
<li>Trails, grooming and skier wear come with PR B, so every type here is natural.</li>
</ul>
<div id="tip"></div>
</main>
<script>
const DATA = ${JSON.stringify(data.sites.map((s) => ({ name: s.name, maps: s.maps })))};
const TYPES = ${JSON.stringify(TYPES)};
const tip = document.getElementById("tip");
const show = (e, html) => { tip.innerHTML = html; tip.style.display = "block"; tip.style.left = (e.clientX + 14) + "px"; tip.style.top = (e.clientY + 14) + "px"; };
const hide = () => { tip.style.display = "none"; };
const colour = (c) => getComputedStyle(document.querySelector(".k-" + c)).backgroundColor;
for (const r of document.querySelectorAll("rect.hover")) {
  const svg = r.ownerSVGElement, cross = svg.querySelector(".cross"), series = JSON.parse(r.dataset.series), labels = JSON.parse(r.dataset.labels);
  const words = JSON.parse(r.dataset.words), cls = JSON.parse(r.dataset.cls), left = +r.dataset.left, w = +r.dataset.w, right = +r.dataset.right, n = labels.length;
  r.addEventListener("mousemove", (e) => {
    const pt = svg.createSVGPoint(); pt.x = e.clientX; pt.y = e.clientY;
    const p = pt.matrixTransform(svg.getScreenCTM().inverse());
    const i = Math.max(0, Math.min(n - 1, Math.round((p.x - left) / (w - left - right) * (n - 1))));
    const cx = left + i / Math.max(1, n - 1) * (w - left - right);
    cross.setAttribute("x1", cx); cross.setAttribute("x2", cx); cross.setAttribute("visibility", "visible");
    show(e, "<b>" + labels[i] + "</b>" + series.map((s, k) => "<span><i style='background:" + colour(cls[k]) + "'></i>" + words[k] + ": " + s[i] + r.dataset.unit + "</span>").join(""));
  });
  r.addEventListener("mouseleave", () => { hide(); cross.setAttribute("visibility", "hidden"); });
}
// Depth: bare brown, then a light-to-dark blue ramp over 0–120 in (one hue, light to dark).
const RAMP = [[0, [238, 244, 251]], [30, [156, 195, 238]], [60, [42, 120, 214]], [120, [11, 45, 92]]];
const hex = (h) => [1, 3, 5].map((i) => parseInt(h.slice(i, i + 2), 16));
function depthColour(v) {
  if (v < 0.1) return hex("#8B7458");
  for (let k = 1; k < RAMP.length; k++) if (v <= RAMP[k][0]) { const [a, ca] = RAMP[k - 1], [b, cb] = RAMP[k], t = (v - a) / (b - a); return ca.map((c, j) => Math.round(c + (cb[j] - c) * t)); }
  return RAMP[RAMP.length - 1][1];
}
for (const c of document.querySelectorAll("canvas[data-kind]")) {
  const m = DATA[+c.dataset.site].maps[+c.dataset.map], kind = c.dataset.kind, size = m.size, ctx = c.getContext("2d"), img = ctx.createImageData(size, size);
  for (let i = 0; i < size * size; i++) {
    const rgb = kind === "depth" ? depthColour(m.depth[i]) : hex(TYPES[m.type[i]][1]);
    img.data.set([...rgb, 255], i * 4);
  }
  ctx.putImageData(img, 0, 0);
  c.addEventListener("mousemove", (e) => {
    const b = c.getBoundingClientRect(), x = Math.floor((e.clientX - b.left) / b.width * size), y = Math.floor((e.clientY - b.top) / b.height * size);
    const i = Math.max(0, Math.min(size - 1, y)) * size + Math.max(0, Math.min(size - 1, x));
    show(e, "<b>" + DATA[+c.dataset.site].name + " · " + m.label + "</b><span>" + TYPES[m.type[i]][0] + ", " + m.depth[i].toFixed(1) + " in deep</span>");
  });
  c.addEventListener("mouseleave", hide);
}
if (typeof LAPSE !== "undefined") {
  const img = document.getElementById("lapse"), range = document.getElementById("frame"), word = document.getElementById("frameWord"), play = document.getElementById("play");
  let timer = null;
  const go = (i) => { range.value = i; img.src = LAPSE[i]; img.alt = word.textContent = LAPSE_WORDS[i]; };
  range.addEventListener("input", () => go(+range.value));
  play.addEventListener("click", () => {
    if (timer) { clearInterval(timer); timer = null; play.textContent = "▶ Play"; return; }
    play.textContent = "❚❚ Pause";
    timer = setInterval(() => go((+range.value + 1) % LAPSE.length), 600);
  });
  LAPSE.forEach((s) => { const p = new Image(); p.src = s; });
}
</script></body></html>`;
const out = join(outDir, "report.html");
writeFileSync(out, html);
console.log("wrote " + out);
