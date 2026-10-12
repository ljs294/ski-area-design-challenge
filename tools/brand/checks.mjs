// Mechanical checks for brand marks (docs/plans/brand/brief.md, tests T2 T3 T4 and the colour rules), measured in
// headless Edge on a canvas. Writes <out>/<name>.checks.json and <out>/<name>-proof.jpg for each SVG, and prints a
// one-line verdict per file. The judgement tests (T1, T5-T9) are for reviewers; these only catch what can be counted.
// Usage: node tools/brand/checks.mjs <svg...> [--out <folder>] [--brand studio|game] [--icon]
//   --icon   the file is a hand-fitted small icon: T4 is judged at its own size (16 or 24, from its viewBox). Without
//            it the file is a master, judged at 32 px (16 and 24 come from hand-fitted icons, so they are only reported).
// Part-covered pixels are reported as a warning: diagonals always antialias.
// Exit code: 0 when every file passes, 1 when any fails, 2 on a tool error.
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { evaluate, launch } from './edge.mjs';

const args = process.argv.slice(2);
const flag = (n) => args.includes(n);
const arg = (name, fallback) => { const i = args.indexOf(name); return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback; };
const files = args.filter((a, i) => a.toLowerCase().endsWith('.svg') && !['--out', '--brand'].includes(args[i - 1]));
const outDir = path.resolve(arg('--out', 'test-results/brand/checks'));
const brand = arg('--brand', null);
const iconMode = flag('--icon');
if (!files.length) { console.error('usage: checks.mjs <svg...> [--out <folder>] [--brand studio|game] [--icon]'); process.exit(2); }
mkdirSync(outDir, { recursive: true });

// Trailhead tokens (Theme-Dark.tss, Theme-Light.tss, Flow.uss): the studio's accent must stay clear of all of them.
const TOKENS = ['#1c1916', '#efe9df', '#ffffff', '#cfc7bb', '#3b3632', '#fbf8f2', '#1f1c19', '#1a6b3c', '#1f7d47', '#ff6a1f',
  '#f0b43c', '#8fcf8a', '#ff7a66', '#faf8f4', '#0f0d0b', '#4a443e', '#ddd7cd', '#e35b14', '#a5650c', '#2a7a3a', '#c23b27',
  '#22558f', '#93643e', '#5a3a22'];
const GAME_COLOURS = ['#1f1c19', '#fbf8f2', '#1a6b3c', '#22558f', '#ffffff', '#000000'];
const GROUNDS = { 'sign white': '#fbf8f2', graphite: '#1c1916', 'dark taskbar': '#202020', 'light taskbar': '#f3f3f3' };

// ---------- colour maths ----------
const hex = (h) => { h = h.replace('#', ''); if (h.length === 3) h = [...h].map((c) => c + c).join(''); return [0, 2, 4].map((i) => parseInt(h.slice(i, i + 2), 16)); };
const lin = (c) => { c /= 255; return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4; };
const lum = (rgb) => 0.2126 * lin(rgb[0]) + 0.7152 * lin(rgb[1]) + 0.0722 * lin(rgb[2]);
const contrast = (a, b) => { const [x, y] = [lum(hex(a)), lum(hex(b))].sort((p, q) => q - p); return (x + 0.05) / (y + 0.05); };
function hsl(h) {
  const [r, g, b] = hex(h).map((v) => v / 255), mx = Math.max(r, g, b), mn = Math.min(r, g, b), l = (mx + mn) / 2, d = mx - mn;
  if (!d) return [0, 0, l];
  const s = d / (1 - Math.abs(2 * l - 1));
  let hue = mx === r ? ((g - b) / d) % 6 : mx === g ? (b - r) / d + 2 : (r - g) / d + 4;
  return [(hue * 60 + 360) % 360, s, l];
}
function lab(h) {
  const [r, g, b] = hex(h).map(lin);
  const x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047, y = 0.2126 * r + 0.7152 * g + 0.0722 * b, z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883;
  const f = (t) => (t > 216 / 24389 ? Math.cbrt(t) : (24389 / 27 * t + 16) / 116);
  return [116 * f(y) - 16, 500 * (f(x) - f(y)), 200 * (f(y) - f(z))];
}
function de2000(h1, h2) {   // CIEDE2000
  const [L1, a1, b1] = lab(h1), [L2, a2, b2] = lab(h2), rad = Math.PI / 180;
  const C1 = Math.hypot(a1, b1), C2 = Math.hypot(a2, b2), Cm = (C1 + C2) / 2, G = 0.5 * (1 - Math.sqrt(Cm ** 7 / (Cm ** 7 + 25 ** 7)));
  const a1p = (1 + G) * a1, a2p = (1 + G) * a2, C1p = Math.hypot(a1p, b1), C2p = Math.hypot(a2p, b2);
  const h1p = (Math.atan2(b1, a1p) / rad + 360) % 360, h2p = (Math.atan2(b2, a2p) / rad + 360) % 360;
  const dL = L2 - L1, dC = C2p - C1p;
  let dh = h2p - h1p; if (C1p * C2p === 0) dh = 0; else if (dh > 180) dh -= 360; else if (dh < -180) dh += 360;
  const dH = 2 * Math.sqrt(C1p * C2p) * Math.sin(dh * rad / 2), Lm = (L1 + L2) / 2, Cmp = (C1p + C2p) / 2;
  let hm = h1p + h2p; if (C1p * C2p !== 0) hm = Math.abs(h1p - h2p) > 180 ? (hm + (hm < 360 ? 360 : -360)) / 2 : hm / 2;
  const T = 1 - 0.17 * Math.cos((hm - 30) * rad) + 0.24 * Math.cos(2 * hm * rad) + 0.32 * Math.cos((3 * hm + 6) * rad) - 0.2 * Math.cos((4 * hm - 63) * rad);
  const dTh = 30 * Math.exp(-(((hm - 275) / 25) ** 2)), RC = 2 * Math.sqrt(Cmp ** 7 / (Cmp ** 7 + 25 ** 7));
  const SL = 1 + 0.015 * (Lm - 50) ** 2 / Math.sqrt(20 + (Lm - 50) ** 2), SC = 1 + 0.045 * Cmp, SH = 1 + 0.015 * Cmp * T, RT = -Math.sin(2 * dTh * rad) * RC;
  return Math.sqrt((dL / SL) ** 2 + (dC / SC) ** 2 + (dH / SH) ** 2 + RT * (dC / SC) * (dH / SH));
}

// ---------- T3 and the colour rules, from the SVG text ----------
function textChecks(svg) {
  const strokes = [...svg.matchAll(/stroke\s*[:=]\s*["']?\s*([^"';\s>]+)/gi)].map((m) => m[1]).filter((v) => !/^none$/i.test(v));
  const opacity = [...svg.matchAll(/(?:fill-|stroke-)?opacity\s*[:=]\s*["']?\s*([\d.]+)/gi)].map((m) => +m[1]).filter((v) => v < 1);
  const gradients = /<(linear|radial)Gradient|<filter|<mask|<clipPath|<image|<text/i.test(svg);
  const fills = [...new Set([...svg.matchAll(/fill\s*[:=]\s*["']?\s*(#[0-9a-f]{3,6})/gi)].map((m) => m[1].toLowerCase().replace(/^#(.)(.)(.)$/, '#$1$1$2$2$3$3')))];
  const colours = fills.map((c) => {
    const [h, s, l] = hsl(c);
    const warm = s > 0.25 && l > 0.12 && l < 0.92 && (h >= 345 || h <= 50);
    const nearest = Math.min(...TOKENS.map((t) => de2000(c, t)));
    const offPalette = brand === 'game' && !GAME_COLOURS.includes(c);
    return { colour: c, hue: Math.round(h), banned: warm, nearestTokenDE: +nearest.toFixed(1), offPalette,
      contrast: Object.fromEntries(Object.entries(GROUNDS).map(([k, g]) => [k, +contrast(c, g).toFixed(2)])) };
  });
  return { strokes, opacity, forbiddenElements: gradients, fills, colours };
}

// ---------- T2 and T4, on a canvas in the page ----------
const PAGE_FN = `async (svg, iconMode) => {
  const img = new Image();
  img.src = URL.createObjectURL(new Blob([svg], { type: 'image/svg+xml' }));
  await img.decode();
  const raster = (size, blur = 0) => {
    const c = document.createElement('canvas'); c.width = c.height = size;
    const g = c.getContext('2d'); if (blur) g.filter = 'blur(' + blur + 'px)';
    g.drawImage(img, 0, 0, size, size);
    const d = g.getImageData(0, 0, size, size).data, a = new Uint8Array(size * size);
    for (let i = 0; i < a.length; i++) a[i] = d[i * 4 + 3];
    return a;
  };
  const mask = (a, t = 127) => a.map((v) => (v > t ? 1 : 0));
  const morph = (m, n, r, dilate) => {   // square kernel, separable
    const tmp = new Uint8Array(m.length), out = new Uint8Array(m.length);
    for (let y = 0; y < n; y++) for (let x = 0; x < n; x++) {
      let v = dilate ? 0 : 1;
      for (let k = -r; k <= r; k++) { const xx = x + k; const s = xx < 0 || xx >= n ? 0 : m[y * n + xx]; v = dilate ? v | s : v & s; }
      tmp[y * n + x] = v;
    }
    for (let y = 0; y < n; y++) for (let x = 0; x < n; x++) {
      let v = dilate ? 0 : 1;
      for (let k = -r; k <= r; k++) { const yy = y + k; const s = yy < 0 || yy >= n ? 0 : tmp[yy * n + x]; v = dilate ? v | s : v & s; }
      out[y * n + x] = v;
    }
    return out;
  };
  const components = (m, n, val) => {
    const seen = new Uint8Array(m.length); let count = 0;
    for (let i = 0; i < m.length; i++) {
      if (seen[i] || m[i] !== val) continue;
      count++; const st = [i]; seen[i] = 1;
      while (st.length) { const j = st.pop(), x = j % n, y = (j / n) | 0;
        for (const [dx, dy] of [[1,0],[-1,0],[0,1],[0,-1]]) { const xx = x + dx, yy = y + dy; if (xx < 0 || yy < 0 || xx >= n || yy >= n) continue;
          const k = yy * n + xx; if (!seen[k] && m[k] === val) { seen[k] = 1; st.push(k); } } }
    }
    return count;
  };
  const N = 256, base = mask(raster(N)), squint = mask(raster(N, N * 0.04));
  let inter = 0, uni = 0; for (let i = 0; i < base.length; i++) { inter += base[i] & squint[i]; uni += base[i] | squint[i]; }
  const parts = (m) => components(m, N, 1), holes = (m) => components(m, N, 0) - 1;
  const t2 = { iou: +(inter / uni).toFixed(3), parts: parts(base), squintParts: parts(squint), holes: holes(base), squintHoles: holes(squint) };
  const vb = (svg.match(/viewBox\\s*=\\s*"([^"]+)"/) || [])[1];
  const own = vb ? Math.round(+vb.split(/[ ,]+/)[3]) : 24;
  const sizes = iconMode ? [own] : [16, 24, 32];
  const t4 = {};
  for (const s of sizes) {
    const a = raster(s); let ink = 0, partial = 0;
    for (const v of a) { if (v > 26) ink++; if (v > 26 && v < 229) partial++; }
    const n = s * 4, m = mask(raster(n)), opened = morph(morph(m, n, 2, false), n, 2, true), closed = morph(morph(m, n, 2, true), n, 2, false);
    let inkHi = 0, thin = 0, gap = 0; for (let i = 0; i < m.length; i++) { inkHi += m[i]; thin += m[i] & (1 - opened[i]); gap += closed[i] & (1 - m[i]); }
    t4[s] = { partial: +(partial / Math.max(1, ink)).toFixed(3), thin: +(thin / Math.max(1, inkHi)).toFixed(3), gap: +(gap / Math.max(1, inkHi)).toFixed(3) };
  }
  return { t2, t4 };
}`;

const PROOF = (svg, name, res) => `<!doctype html><meta charset="utf-8"><body style="margin:0;font:12px monospace;background:#ddd">
<div style="display:flex;gap:10px;padding:10px;align-items:flex-end;flex-wrap:wrap">
  <div style="background:#fbf8f2;padding:8px">${svg.replace(/<svg/, '<svg width="160" height="160"')}</div>
  <div style="background:#1c1916;padding:8px;color:#fbf8f2">${svg.replace(/<svg/, '<svg width="160" height="160" style="filter:invert(1)"')}</div>
  <div style="background:#fbf8f2;padding:8px">${svg.replace(/<svg/, '<svg width="160" height="160" style="filter:blur(6.4px) contrast(30)"')}</div>
  ${[32, 24, 16].map((s) => ['#202020', '#f3f3f3'].map((bg) => `<div style="background:${bg};padding:6px"><canvas data-s="${s}" width="${s}" height="${s}" style="width:${s * 4}px;height:${s * 4}px;image-rendering:pixelated;${bg === '#202020' ? 'filter:invert(1)' : ''}"></canvas></div>`).join('')).join('')}
</div><div style="padding:0 10px 10px">${name} · T2 IoU ${res.t2.iou} (parts ${res.t2.parts}→${res.t2.squintParts}, holes ${res.t2.holes}→${res.t2.squintHoles}) · T4 ${Object.entries(res.t4).map(([s, v]) => `${s}px part ${v.partial} thin ${v.thin} gap ${v.gap}`).join(' · ')}</div>
<script>
const img = new Image(); img.src = URL.createObjectURL(new Blob([${JSON.stringify(svg)}], { type: 'image/svg+xml' }));
window.boardReady = img.decode().then(() => { for (const c of document.querySelectorAll('canvas')) c.getContext('2d').drawImage(img, 0, 0, +c.dataset.s, +c.dataset.s); });
</script>`;

const b = await launch();
let failed = 0;
try {
  await b.send('Emulation.setDeviceMetricsOverride', { width: 1500, height: 300, deviceScaleFactor: 1, mobile: false });
  for (const file of files) {
    const svg = readFileSync(file, 'utf8'), name = path.basename(file, '.svg');
    const text = textChecks(svg);
    const page = await evaluate(b, `(${PAGE_FN})(${JSON.stringify(svg)}, ${iconMode})`);
    const res = { file, ...text, ...page };
    const problems = [];
    if (res.t2.iou < 0.8) problems.push(`T2 squint IoU ${res.t2.iou} < 0.80`);
    if (res.t2.squintParts !== res.t2.parts || res.t2.squintHoles !== res.t2.holes) problems.push(`T2 parts/holes change when squinting (${res.t2.parts}/${res.t2.holes} → ${res.t2.squintParts}/${res.t2.squintHoles})`);
    if (text.strokes.length || text.opacity.length || text.forbiddenElements) problems.push('T3 strokes, opacity or gradients/filters/masks/text present');
    const warnings = [];
    for (const [s, v] of Object.entries(res.t4)) {
      const judged = iconMode || +s === 32;
      if (v.partial > 0.15) warnings.push(`${s}px: ${Math.round(v.partial * 100)}% part-covered`);
      if (!judged) continue;
      if (v.thin > 0.05) problems.push(`T4 ${s}px: ${Math.round(v.thin * 100)}% of the ink is thinner than 1 px`);
      if (v.gap > 0.10) problems.push(`T4 ${s}px: gaps under 1 px (${Math.round(v.gap * 100)}%)`);
    }
    res.warnings = warnings;
    for (const c of text.colours) {
      if (c.banned) problems.push(`colour ${c.colour}: red/orange/amber hue ${c.hue}°`);
      if (c.offPalette) problems.push(`colour ${c.colour}: not a game colour`);
      if (brand === 'studio' && !['#1f1c19', '#fbf8f2', '#ffffff', '#000000'].includes(c.colour) && c.nearestTokenDE < 15) problems.push(`colour ${c.colour}: only ${c.nearestTokenDE} ΔE from a Trailhead token`);
    }
    res.problems = problems;
    writeFileSync(path.join(outDir, `${name}.checks.json`), JSON.stringify(res, null, 1));
    const proofLoaded = b.once('Page.loadEventFired');
    await b.send('Page.navigate', { url: 'data:text/html;base64,' + Buffer.from(PROOF(svg, name, res)).toString('base64') });
    await proofLoaded;
    await evaluate(b, 'window.boardReady || true');
    const h = await evaluate(b, 'document.documentElement.scrollHeight');
    const shot = await b.send('Page.captureScreenshot', { format: 'jpeg', quality: 85, clip: { x: 0, y: 0, width: 1500, height: h, scale: 1 } });
    writeFileSync(path.join(outDir, `${name}-proof.jpg`), Buffer.from(shot.data, 'base64'));
    if (problems.length) failed++;
    console.log(`${problems.length ? 'FAIL' : 'pass'} ${name}${problems.length ? ': ' + problems.join('; ') : ''}${warnings.length ? ' (warn: ' + warnings.join(', ') + ')' : ''}`);
  }
} catch (e) {
  console.error(e.message); process.exitCode = 2;
} finally {
  await b.close();
}
if (!process.exitCode) process.exitCode = failed ? 1 : 0;
