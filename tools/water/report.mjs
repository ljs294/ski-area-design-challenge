// Task 4.14 PR B (demo 118): the water report. Reads the CSVs WaterReportTests.WS_WriteTheReportData wrote under
// test-results/water and writes test-results/water/report.html: inline SVG charts, no packages needed.
//
//   node tools/water/report.mjs [test-results/water] [--png]
//
// --png also renders each section to a picture (water-levels.png, water-flows.png, water-ledger.png) through headless
// Edge (tools/brand/edge.mjs), for review on a phone.
import { readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import process from 'node:process';

const args = process.argv.slice(2);
const root = path.resolve(args.find((a) => !a.startsWith('--')) ?? 'test-results/water');
const wantPng = args.includes('--png');

const MONTHS = ['Oct', 'Nov', 'Dec', 'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep'];
// The lake report's palette (tools/lakes/report.py): the stored water is the first hue, out the second.
const WATER = '#2E6FD8', OUT = '#E07B39', FLOOR = '#6B7280', GRID = '#D9DEE5', INK = '#1F2937', MUTED = '#6B7280', BAND = '#EEF2F7';
const SOURCES = ['Lake (source)', 'Natural pond', 'Pumped-only pond', 'Reservoir'];
const NOTES = {
  'Lake (source)': 'Switched on as a source; 300 gpm test draw Dec 1 – Mar 31; may draw 3 ft at most (WS-05).',
  'Natural pond': 'Starts empty; fills from its catchment\'s rain and snowmelt (WS-04).',
  'Pumped-only pond': 'Starts empty; takes no runoff (WS-09); filled from the lake at 100 gpm in November (WS-11).',
  'Reservoir': 'A dam on a 2 cfs stream × the month\'s factor (WS-03); 1,000 gpm test draw Dec 1 – Mar 31.',
};

function readCsv(file) {
  const [head, ...lines] = readFileSync(path.join(root, file), 'utf8').trim().split(/\r?\n/);
  const cols = head.split(',');
  return lines.map((l) => Object.fromEntries(l.split(',').map((v, i) => [cols[i], v])));
}

const rows = readCsv('daily.csv');
const sources = Object.fromEntries(readCsv('sources.csv').map((r) => [r.source, r]));
const days = rows.length;
const num = (r, k) => Number(r[k]);
const fmt = (v) => Math.round(v).toLocaleString('en-US');
const gal = (v) => (Math.abs(v) >= 1e6 ? (v / 1e6).toFixed(v >= 1e7 ? 0 : 1) + 'M' : fmt(v)) + ' gal';
const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;');

const ticks = [];
for (let d = 0, last = null; d < days; d++) {
  const m = num(rows[d], 'month');
  if (m !== last) { ticks.push([d, MONTHS[(m - 10 + 12) % 12]]); last = m; }
}
// The season (Dec 1 – Mar 31), shaded: snowmaking draws only then.
const season = [rows.findIndex((r) => r.month === '12'), rows.findIndex((r) => r.month === '4')];

function frame(svg, x0, y0, w, h, ymax, label) {
  svg.push(`<rect x="${x0 + w * season[0] / days}" y="${y0}" width="${w * (season[1] - season[0]) / days}" height="${h}" fill="${BAND}"/>`);
  for (const [d, m] of ticks) {
    const x = x0 + w * d / days;
    svg.push(`<line x1="${x}" y1="${y0 + h}" x2="${x}" y2="${y0 + h + 4}" stroke="${MUTED}"/>`);
    svg.push(`<text x="${x + 3}" y="${y0 + h + 15}" font-size="11" fill="${MUTED}">${m}</text>`);
  }
  for (let k = 0; k <= 4; k++) {
    const y = y0 + h - h * k / 4;
    svg.push(`<line x1="${x0}" y1="${y}" x2="${x0 + w}" y2="${y}" stroke="${GRID}" stroke-width="${k ? 1 : 1.5}"/>`);
    svg.push(`<text x="${x0 - 6}" y="${y + 4}" font-size="11" fill="${MUTED}" text-anchor="end">${label(ymax * k / 4)}</text>`);
  }
}

function line(values, x0, y0, w, h, ymax, colour, dash = '') {
  const pts = values.map((v, d) => `${(x0 + w * (d + 0.5) / days).toFixed(1)},${(y0 + h - h * Math.min(v, ymax) / ymax).toFixed(1)}`);
  return `<polyline points="${pts.join(' ')}" fill="none" stroke="${colour}" stroke-width="2" stroke-linejoin="round" ${dash ? `stroke-dasharray="${dash}"` : ''}/>`;
}

function legend(items) {
  return `<div class="legend">${items.map(([c, t, dash]) => `<span><svg width="22" height="10"><line x1="1" y1="5" x2="21" y2="5" stroke="${c}" stroke-width="2" ${dash ? 'stroke-dasharray="4 3"' : ''}/></svg>${esc(t)}</span>`).join('')}
    <span><svg width="14" height="10"><rect width="14" height="10" fill="${BAND}"/></svg>Season, Dec 1 – Mar 31</span></div>`;
}

const W = 520, H = 170, X0 = 54, Y0 = 10;

function levelChart(name) {
  const cap = num(rows[0], `${name} capacity gal`);
  const held = rows.map((r) => 100 * num(r, `${name} held gal`) / cap);
  const floor = rows.map((r) => 100 * num(r, `${name} floor gal`) / cap);
  const svg = [];
  frame(svg, X0, Y0, W, H, 100, (v) => `${Math.round(v)}%`);
  svg.push(line(floor, X0, Y0, W, H, 100, FLOOR, '4 3'));
  svg.push(line(held, X0, Y0, W, H, 100, WATER));
  const s = sources[name];
  const feeds = Number(s.catchment_cells) > 0 ? `catchment ${s.catchment_acres} acres` : name === 'Reservoir' ? 'fed by its stream' : 'takes no runoff';
  return `<figure><figcaption><b>${esc(name)}</b> · holds ${gal(Number(s.capacity_gal))} full · ${feeds}</figcaption>
    <svg viewBox="0 0 ${X0 + W + 10} ${Y0 + H + 22}" width="${X0 + W + 10}">${svg.join('')}</svg><p class="note">${esc(NOTES[name])}</p></figure>`;
}

function flowChart(name) {
  const inn = rows.map((r) => ['inflow', 'runoff', 'spring', 'pumped in'].reduce((a, k) => a + num(r, `${name} ${k}`), 0));
  const out = rows.map((r) => ['draw', 'pumped out', 'spill', 'evaporation'].reduce((a, k) => a + num(r, `${name} ${k}`), 0));
  let ymax = Math.max(1, ...inn, ...out);
  const step = 10 ** Math.floor(Math.log10(ymax / 4));
  ymax = Math.ceil(ymax / 4 / step) * step * 4;
  const svg = [];
  frame(svg, X0, Y0, W, H, ymax, (v) => (v >= 1e6 ? `${+(v / 1e6).toFixed(1)}M` : v >= 1e3 ? `${+(v / 1e3).toFixed(0)}k` : `${v}`));
  svg.push(line(out, X0, Y0, W, H, ymax, OUT));
  svg.push(line(inn, X0, Y0, W, H, ymax, WATER));
  return `<figure><figcaption><b>${esc(name)}</b> · gallons a face day</figcaption>
    <svg viewBox="0 0 ${X0 + W + 10} ${Y0 + H + 22}" width="${X0 + W + 10}">${svg.join('')}</svg></figure>`;
}

const TERMS = ['inflow', 'runoff', 'spring', 'pumped in', 'draw', 'pumped out', 'spill', 'evaporation'];
function ledgerTable() {
  const head = `<tr><th>Source</th><th>Start</th>${TERMS.map((t) => `<th>${t[0].toUpperCase() + t.slice(1)}</th>`).join('')}<th>End</th><th>Balances</th></tr>`;
  const body = SOURCES.map((name) => {
    const sum = (k) => rows.reduce((a, r) => a + num(r, `${name} ${k}`), 0);
    const t = Object.fromEntries(TERMS.map((k) => [k, sum(k)]));
    const end = num(rows[days - 1], `${name} held gal`);
    const start = Number(sources[name].start_gal);
    // Each day's terms are rounded down to the gallon, so the sum may miss the end by up to two gallons a term a day.
    const miss = start + t.inflow + t.runoff + t.spring + t['pumped in'] - t.draw - t['pumped out'] - t.spill - t.evaporation - end;
    const ok = Math.abs(miss) <= 2 * TERMS.length * days;
    return `<tr><td>${esc(name)}</td><td>${start ? gal(start) : 'empty'}</td>${TERMS.map((k) => `<td>${t[k] ? gal(t[k]) : '–'}</td>`).join('')}<td>${gal(end)}</td><td>${ok ? '✓' : '✗ ' + fmt(miss)}</td></tr>`;
  }).join('');
  const last = rows[days - 1];
  const worst = num(last, 'worst hourly imbalance gal');
  return `<table>${head}${body}</table>
    <p class="note">The resort's ledger (SI-10): inflow + runoff + spring + pumped in = change in storage + draw + pumped out + spill +
    evaporation, checked every face hour of the year. Largest difference over the year: <b>${fmt(worst)} gal</b>. Held at the
    end: ${gal(num(last, 'resort held gal'))}; the ledger says ${gal(num(last, 'resort ledger gal'))}. Start is the water each source
    began with.</p>`;
}

const html = `<!DOCTYPE html><html><head><meta charset="utf-8"><title>Water report · demo 118</title><style>
body{font:14px/1.45 "Segoe UI",system-ui,sans-serif;color:${INK};margin:24px;max-width:1210px;background:#fff}
h1{font-size:22px;margin:0 0 4px}h2{font-size:17px;margin:28px 0 6px}section{padding:8px 0 16px}
.grid{display:grid;grid-template-columns:repeat(2,${X0 + W + 10}px);gap:14px 40px}figure{margin:0}figcaption{font-size:13px;margin-bottom:2px}
.note{color:${MUTED};font-size:12.5px;margin:2px 0 0;max-width:${X0 + W}px}.legend{display:flex;gap:18px;font-size:12.5px;color:${MUTED};margin:0 0 10px}
.legend span{display:flex;gap:6px;align-items:center}table{border-collapse:collapse;font-size:12.5px;font-variant-numeric:tabular-nums}
th,td{padding:5px 9px;border-bottom:1px solid ${GRID};text-align:right}th:first-child,td:first-child{text-align:left}th{color:${MUTED};font-weight:600}
.lede{color:${MUTED};max-width:1100px}
</style></head><body>
<h1>Water over a year · Task 4.14 PR B (demo 118)</h1>
<p class="lede">4.07's synthetic 2 km mountain under Jackson Hole's recorded weather, October to September (125 face days; a face
day is about 2.9 calendar days, WX-22). The snow builds and melts on every 8 m cell, and each cell's runoff goes to the source
it drains to. Test draws stand in for 4.16's snow guns.</p>
<section id="levels"><h2>How full each source is</h2>${legend([[WATER, 'Water held, % of full'], [FLOOR, 'Floor: minimum pool (10%), or a lake\'s 3 ft limit', true]])}
<div class="grid">${SOURCES.map(levelChart).join('')}</div></section>
<section id="flows"><h2>Water in and out, each face day</h2>${legend([[WATER, 'In: stream, runoff, spring, pumped in'], [OUT, 'Out: draw, pumped out, spill, evaporation']])}
<div class="grid">${SOURCES.map(flowChart).join('')}</div></section>
<section id="ledger"><h2>The year's ledger</h2>${ledgerTable()}</section>
</body></html>`;

const out = path.join(root, 'report.html');
writeFileSync(out, html);
console.log('Wrote ' + out);

if (wantPng) {
  const { launch, open, evaluate } = await import('../brand/edge.mjs');
  const b = await launch();
  try {
    await open(b, out, 1260, 900);
    for (const id of ['levels', 'flows', 'ledger']) {
      const r = await evaluate(b, `(() => { const r = document.getElementById('${id}').getBoundingClientRect(); return { x: r.x, y: r.y + scrollY, w: r.width, h: r.height }; })()`);
      const shot = await b.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true,
        clip: { x: 0, y: Math.max(0, r.y - 8), width: 1260, height: Math.ceil(r.h + 16), scale: 1.5 } });
      const file = path.join(root, `water-${id}.png`);
      writeFileSync(file, Buffer.from(shot.data, 'base64'));
      console.log('Wrote ' + file);
    }
  } finally { await b.close(); }
}
