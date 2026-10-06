// HUD parity (task P2-02): the accepted mockup, docs/plans/prototypes/ui-layout.html, against the game's HUD.
// Each state renders in headless Edge as `#solo&demo=p2,<flags>` (p2 = the game as built in Phase 2) and every part
// with a data-ui name is measured: its box on the 1280×720 stage, colours, font and text. The game writes the same for
// its elements with the same names (-uicapture writes <shot>.layout.json next to each 1920×1080 picture at 100%).
// Differences beyond the tolerances go to report.md, with side-by-side sheets that outline each differing part.
// No dependencies: Edge is driven over the DevTools protocol with Node's own WebSocket (Node 22).
// Usage: node tools/ui-parity/parity.mjs [--unity <capture folder>] [--out <folder>] [--only state,state] [--mockup-only]
//   --unity        the -uicapture folder (default test-results/ui-captures)
//   --out          where the mockup renders, sheets and report go (default test-results/ui-parity)
//   --mockup-only  render and measure the mockup without comparing (to see it, or to check the names)
// Exit code: 0 when every compared part matches, 1 when anything differs or is missing, 2 on a tool error.
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { STATES, NO_TEXT, TOLERANCE } from './states.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '../..');
const mockup = path.join(root, 'docs/plans/prototypes/ui-layout.html');
const args = process.argv.slice(2);
const arg = (name, fallback) => { const i = args.indexOf(name); return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback; };
const unityDir = path.resolve(arg('--unity', path.join(root, 'test-results/ui-captures')));
const outDir = path.resolve(arg('--out', path.join(root, 'test-results/ui-parity')));
const only = arg('--only', null)?.split(',');
const mockupOnly = args.includes('--mockup-only');
const THEMES = ['dark', 'light'];

const EDGE = [
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
].find((p) => existsSync(p));

// ---------- a minimal DevTools client ----------
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const within = (p, ms, what) => Promise.race([p, sleep(ms).then(() => { throw new Error(`timed out: ${what}`); })]);
async function launch() {
  if (!EDGE) throw new Error('Microsoft Edge was not found');
  const profile = mkdtempSync(path.join(os.tmpdir(), 'ui-parity-'));   // a fresh profile, so nothing is cached
  const port = 9300 + Math.floor(Math.random() * 500);
  const proc = spawn(EDGE, ['--headless', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, '--no-first-run',
    '--no-default-browser-check', '--hide-scrollbars', '--force-color-profile=srgb', '--disable-gpu', 'about:blank'], { stdio: 'ignore' });
  let page = null;
  for (let i = 0; i < 100 && !page; i++) {
    await sleep(150);
    try { page = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find((t) => t.type === 'page'); } catch { /* not up yet */ }
  }
  if (!page) { proc.kill(); throw new Error('Edge did not open its DevTools port'); }
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject; });
  let id = 0;
  const pending = new Map(), waiters = [];
  ws.onmessage = (m) => {
    const msg = JSON.parse(m.data);
    if (msg.id && pending.has(msg.id)) {
      const { resolve, reject } = pending.get(msg.id);
      pending.delete(msg.id);
      if (msg.error) reject(new Error(msg.error.message)); else resolve(msg.result);
    } else if (msg.method) for (const w of waiters.splice(0)) if (w.method === msg.method) w.resolve(msg.params); else waiters.push(w);
  };
  const send = (method, params = {}) => new Promise((resolve, reject) => { const n = ++id; pending.set(n, { resolve, reject }); ws.send(JSON.stringify({ id: n, method, params })); });
  const once = (method) => new Promise((resolve) => waiters.push({ method, resolve }));
  const close = async () => {
    try { ws.close(); } catch { /* closed */ }
    const exited = new Promise((r) => proc.once('exit', r));
    proc.kill();
    await Promise.race([exited, sleep(3000)]);
    for (let i = 0; i < 5; i++) {   // Edge's helpers let go of the profile a moment after it exits
      try { rmSync(profile, { recursive: true, force: true }); break; } catch { await sleep(500); }
    }
  };
  await send('Page.enable');
  await send('Runtime.enable');
  return { send, once, close };
}
const evaluate = async (b, expression) => {
  const r = await b.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text);
  return r.result.value;
};
async function shot(b, file, width = 1920, height = 1080) {
  await b.send('Emulation.setDeviceMetricsOverride', { width: 1280, height: 720, deviceScaleFactor: width / 1280, mobile: false });
  const { data } = await b.send('Page.captureScreenshot', { format: 'png', clip: { x: 0, y: 0, width: 1280, height: 720, scale: 1 } });
  writeFileSync(file, Buffer.from(data, 'base64'));
}

// ---------- the mockup's parts ----------
// Every visible element with a data-ui name: its box in stage pixels, colours as [r,g,b,a] (0-255), font and the
// text of a leaf element.
const MEASURE = `(async () => {
  await document.fonts.ready;
  await new Promise((r) => setTimeout(r, 120));   // headless Edge can hold back animation frames; a timer always runs
  const stage = document.getElementById('stage').getBoundingClientRect(), K = stage.width / 1280;
  const rgba = (c) => { const m = c.match(/rgba?\\(([^)]+)\\)/); if (!m) return null; const v = m[1].split(',').map((x) => parseFloat(x)); return [v[0], v[1], v[2], Math.round((v.length > 3 ? v[3] : 1) * 255)]; };
  const parts = {};
  for (const el of document.querySelectorAll('[data-ui]')) {
    const r = el.getBoundingClientRect(), cs = getComputedStyle(el);
    if (r.width < 0.5 || r.height < 0.5 || cs.visibility === 'hidden' || cs.display === 'none') continue;
    const border = parseFloat(cs.borderTopWidth) > 0 ? rgba(cs.borderTopColor) : null;
    parts[el.dataset.ui] = {
      x: (r.left - stage.left) / K, y: (r.top - stage.top) / K, w: r.width / K, h: r.height / K,
      bg: rgba(cs.backgroundColor), color: rgba(cs.color), border,
      fontSize: parseFloat(cs.fontSize), fontWeight: parseInt(cs.fontWeight, 10),
      text: el.children.length ? null : el.textContent.replace(/\\s+/g, ' ').trim(),
    };
  }
  return parts;
})()`;

async function renderMockup(b, state, theme, index) {
  const flags = ['p2', ...(state.mockup ? state.mockup.split(',') : []), ...(theme === 'light' ? ['light'] : [])].join(',');
  const url = `${pathToFileURL(mockup).href}?n=${index}#solo&demo=${flags}`;
  await b.send('Emulation.setDeviceMetricsOverride', { width: 1280, height: 720, deviceScaleFactor: 1.5, mobile: false });
  let parts = null;
  for (let attempt = 1; !parts; attempt++) {
    try {
      const loaded = b.once('Page.loadEventFired');
      await b.send('Page.navigate', { url });
      await within(loaded, 15000, `loading ${state.name}`);
      parts = await within(evaluate(b, MEASURE), 15000, `measuring ${state.name}`);
    } catch (e) {
      if (attempt >= 3) throw e;
      console.log(`retrying ${state.name} ${theme}: ${e.message}`);
    }
  }
  await sleep(250);   // the theme's colour transitions, if any
  const png = path.join(outDir, 'mockup', `${state.name}_${theme}.png`);
  await shot(b, png);
  return { parts, png };
}

// ---------- comparing ----------
const near = (a, b, tol) => Math.abs(a - b) <= tol;
const opaque = (c) => c && c[3] >= 230;   // the mockup's 96-97% panels are solid in the game (0.3 §5, P2-01)
function colourDiff(m, u, tol) {
  if (!m || m[3] === 0) return null;                      // nothing painted in the mockup
  if (!u) return 'none in the game';
  if (opaque(m) !== (u[3] >= 230) && Math.abs(m[3] - u[3]) > 40) return `alpha ${m[3]} vs ${u[3]}`;
  const d = Math.max(Math.abs(m[0] - u[0]), Math.abs(m[1] - u[1]), Math.abs(m[2] - u[2]));
  return d > tol ? `${hex(m)} vs ${hex(u)}` : null;
}
const hex = (c) => '#' + c.slice(0, 3).map((v) => Math.round(v).toString(16).padStart(2, '0')).join('');

function compare(state, theme, mock, unity, allNames) {
  const diffs = [];
  for (const [name, m] of Object.entries(mock)) {
    const u = unity[name];
    if (!u) { diffs.push({ name, kind: 'missing', what: 'not in the game' }); continue; }
    const box = [['x', m.x, u.x], ['y', m.y, u.y], ['w', m.w, u.w], ['h', m.h, u.h]].filter(([, a, b]) => !near(a, b, TOLERANCE.px));
    if (box.length) diffs.push({ name, kind: 'box', what: box.map(([k, a, b]) => `${k} ${a.toFixed(1)} vs ${b.toFixed(1)}`).join(', '), m, u });
    for (const k of ['bg', 'color', 'border']) {
      const d = k === 'color' && m.text == null ? null : colourDiff(m[k], u[k], TOLERANCE.colour);
      if (d) diffs.push({ name, kind: k, what: d, m, u });
    }
    if (m.text != null && u.fontSize && !near(m.fontSize, u.fontSize, TOLERANCE.fontSize)) diffs.push({ name, kind: 'font', what: `${m.fontSize}px vs ${u.fontSize}px`, m, u });
    if (m.text != null && u.text != null && !NO_TEXT.has(name) && m.text !== u.text) diffs.push({ name, kind: 'text', what: `"${m.text}" vs "${u.text}"`, m, u });
  }
  // A part the game shows here that the mockup hides in this state (it shows somewhere else).
  for (const [name, u] of Object.entries(unity)) if (!mock[name] && allNames.has(name)) diffs.push({ name, kind: 'extra', what: 'shown in the game, not in the mockup', u });
  return diffs;
}

// ---------- the side-by-side sheet ----------
function sheetHtml(state, theme, mockPng, unityPng, diffs) {
  const boxes = (side) => diffs.filter((d) => d[side]).map((d) => {
    const r = d[side];
    return `<div class="o" style="left:${r.x / 1280 * 100}%;top:${r.y / 720 * 100}%;width:${r.w / 1280 * 100}%;height:${r.h / 720 * 100}%"><span>${d.name}</span></div>`;
  }).join('');
  const img = (src, side, label) => `<figure><figcaption>${label}</figcaption><div class="w">${src ? `<img src="${pathToFileURL(src).href}">` : '<div class="none">no capture</div>'}${boxes(side)}</div></figure>`;
  return `<!doctype html><meta charset="utf-8"><style>
    body { margin: 0; padding: 16px; background: #111; color: #eee; font: 14px 'Segoe UI', sans-serif; }
    h1 { font-size: 18px; margin: 0 0 10px; } .row { display: flex; gap: 16px; } figure { margin: 0; flex: 1; }
    figcaption { margin-bottom: 6px; color: #aaa; } .w { position: relative; aspect-ratio: 16 / 9; background: #222; }
    img { width: 100%; display: block; } .none { padding: 40px; color: #888; }
    .o { position: absolute; outline: 2px solid #ff3d7f; } .o span { position: absolute; left: 0; top: -15px; font: 11px Consolas, monospace; background: #ff3d7f; color: #fff; padding: 0 3px; white-space: nowrap; }
    /* headless Edge only draws a new frame when something changes; a still page would never give a screenshot */
    @keyframes tick { to { opacity: 0.99; } } .tick { position: fixed; width: 1px; height: 1px; animation: tick 0.5s infinite alternate; }
    ul { margin: 12px 0 0; padding-left: 18px; font: 12px Consolas, monospace; color: #ccc; columns: 2; }
  </style><h1>${state.name} · ${theme} · ${diffs.length ? `${diffs.length} differences` : 'matches'}</h1>
  <div class="tick"></div><div class="row">${img(mockPng, 'm', 'Mockup (ui-layout.html, demo=p2)')}${img(unityPng, 'u', 'Game (-uicapture, 1920×1080, 100%)')}</div>
  <ul>${diffs.map((d) => `<li>${d.name}: ${d.kind}, ${d.what.replace(/</g, '&lt;')}</li>`).join('')}</ul>`;
}

// A sheet's picture: its own headless Edge with a fresh profile and --screenshot, which never waits on a frame
// (a still page over DevTools can wait for one forever).
async function sheetPicture(html, height) {
  const profile = mkdtempSync(path.join(os.tmpdir(), 'ui-parity-sheet-'));
  const proc = spawn(EDGE, ['--headless', '--disable-gpu', '--hide-scrollbars', '--no-first-run', `--user-data-dir=${profile}`,
    `--screenshot=${html.replace(/\.html$/, '.png')}`, `--window-size=1600,${height}`, pathToFileURL(html).href], { stdio: 'ignore' });
  await Promise.race([new Promise((r) => proc.once('exit', r)), sleep(30000)]);
  proc.kill();
  for (let i = 0; i < 5; i++) { try { rmSync(profile, { recursive: true, force: true }); break; } catch { await sleep(500); } }
}

// ---------- main ----------
async function main() {
  for (const d of ['mockup', 'sheets']) mkdirSync(path.join(outDir, d), { recursive: true });
  const states = STATES.filter((s) => !only || only.includes(s.name));
  const b = await launch();
  const results = [];
  try {
    let n = 0;
    const mockups = [];
    for (const state of states) for (const theme of THEMES) mockups.push({ state, theme, ...(await renderMockup(b, state, theme, n++)) });
    const allNames = new Set(mockups.flatMap((x) => Object.keys(x.parts)));
    writeFileSync(path.join(outDir, 'mockup', 'parts.json'), JSON.stringify(Object.fromEntries(mockups.map((x) => [`${x.state.name}_${x.theme}`, x.parts])), null, 1));
    for (const { state, theme, parts, png } of mockups) {
      if (mockupOnly) { results.push({ state, theme, diffs: [], skipped: false }); continue; }
      const shotName = `s6-${state.name}_${theme}_1920x1080`;
      const layout = path.join(unityDir, `${shotName}.layout.json`), unityPng = path.join(unityDir, `${shotName}.png`);
      if (!existsSync(layout)) { results.push({ state, theme, diffs: [{ name: '(state)', kind: 'missing', what: `no ${path.basename(layout)}` }] }); continue; }
      const unity = JSON.parse(readFileSync(layout, 'utf8')).parts;
      const diffs = compare(state, theme, parts, unity, allNames);
      results.push({ state, theme, diffs });
      const html = path.join(outDir, 'sheets', `${state.name}_${theme}.html`);
      writeFileSync(html, sheetHtml(state, theme, png, existsSync(unityPng) ? unityPng : null, diffs));
      await sheetPicture(html, 560 + Math.ceil(diffs.length / 2) * 17);
    }
  } finally {
    await b.close();
  }
  const total = results.reduce((a, r) => a + r.diffs.length, 0);
  let md = `# HUD parity: mockup vs game\n\n${mockupOnly ? 'Mockup only (nothing compared).' : `${total} differences in ${results.length} shots`} · tolerances ±${TOLERANCE.px} px, ±${TOLERANCE.colour}/255 per colour channel, ±${TOLERANCE.fontSize} px type.\n\n`;
  for (const r of results) {
    md += `## ${r.state.name} · ${r.theme}: ${r.diffs.length ? `${r.diffs.length} differences` : 'matches'}\n`;
    for (const d of r.diffs) md += `- \`${d.name}\` ${d.kind}: ${d.what}\n`;
    md += '\n';
  }
  writeFileSync(path.join(outDir, 'report.md'), md);
  console.log(md.split('\n').slice(0, 3).join('\n'));
  for (const r of results) console.log(`${r.diffs.length ? 'DIFF ' : 'ok   '} ${r.state.name} ${r.theme}${r.diffs.length ? ` (${r.diffs.length})` : ''}`);
  console.log(`Report: ${path.join(outDir, 'report.md')}`);
  process.exitCode = total ? 1 : 0;
}

main().catch((e) => { console.error(e); process.exitCode = 2; });
