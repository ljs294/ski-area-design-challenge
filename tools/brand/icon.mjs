// The game's app icon (task P2-10, decision BR5): the ink game mark on a sign-white tile, rendered from the masters in
// docs/brand/masters at every size Windows asks for, each with the cut the brand rules give that size:
//   16 and 24: the plain pair (icon-16, icon-24), full size on the tile, where the trail and lift would be scratches;
//   32 to 64: the game mark's small cut (1.25 lines); 128 and up: the game mark.
// The tile is the title's sign white (#fbf8f2) with rounded corners and no outline, so the ink mark reads on a dark
// or a light taskbar. Writes Assets/MountainPlanner/Art/UI/Brand/Icon/icon-<size>.png (Git LFS) and a proof strip.
// Usage: node tools/brand/icon.mjs [--proof test-results/brand/icon-proof.png]
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { evaluate, launch } from './edge.mjs';

const root = path.resolve(path.dirname(new URL(import.meta.url).pathname.replace(/^\/(\w:)/, '$1')), '..', '..');
const masters = path.join(root, 'docs', 'brand', 'masters');
const out = path.join(root, 'Assets', 'MountainPlanner', 'Art', 'UI', 'Brand', 'Icon');
const args = process.argv.slice(2);
const proofAt = args.indexOf('--proof');
const proof = path.resolve(root, proofAt >= 0 ? args[proofAt + 1] : 'test-results/brand/icon-proof.png');

export const SIZES = [16, 24, 32, 48, 64, 128, 256, 512, 1024];
const TILE = '#fbf8f2';

// Which master, and how much of the tile it fills (the masters' own margins sit inside that).
function cut(size) {
  if (size <= 16) return { file: 'icon-16.svg', fill: 1 };
  if (size <= 24) return { file: 'icon-24.svg', fill: 1 };
  if (size <= 64) return { file: 'game-mark-small.svg', fill: 0.92 };
  return { file: 'game-mark.svg', fill: 0.84 };
}

function page(size) {
  const { file, fill } = cut(size);
  const svg = readFileSync(path.join(masters, file), 'utf8').replace(/ width="\d+" height="\d+"/, '');
  const mark = Math.round(size * fill);
  const radius = Math.max(2, Math.round(size * 0.18));
  return `<!doctype html><html><head><style>
    html, body { margin: 0; background: transparent; }
    .tile { width: ${size}px; height: ${size}px; border-radius: ${radius}px; background: ${TILE};
            display: flex; align-items: center; justify-content: center; }
    .tile svg { width: ${mark}px; height: ${mark}px; display: block; }
  </style></head><body><div class="tile">${svg}</div></body></html>`;
}

async function shot(b, html, width, height) {
  await b.send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
  await b.send('Emulation.setDefaultBackgroundColorOverride', { color: { r: 0, g: 0, b: 0, a: 0 } });
  const loaded = b.once('Page.loadEventFired');
  await b.send('Page.navigate', { url: 'data:text/html;base64,' + Buffer.from(html).toString('base64') });
  await loaded;
  await evaluate(b, 'new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)))');
  const s = await b.send('Page.captureScreenshot', { format: 'png', clip: { x: 0, y: 0, width, height, scale: 1 } });
  return Buffer.from(s.data, 'base64');
}

const b = await launch();
try {
  mkdirSync(out, { recursive: true });
  for (const size of SIZES) {
    writeFileSync(path.join(out, `icon-${size}.png`), await shot(b, page(size), size, size));
    console.log(`icon-${size}.png (${cut(size).file})`);
  }
  // The proof: every size at 1:1 on a dark and a light taskbar, plus the 16 and 32 enlarged 8x to show the pixels.
  const row = (bg) => `<div style="display:flex;gap:24px;align-items:center;padding:20px;background:${bg}">${
    SIZES.filter((s) => s <= 256).map((s) => `<img src="data:image/png;base64,${readFileSync(path.join(out, `icon-${s}.png`)).toString('base64')}">`).join('')}</div>`;
  const zoom = [16, 32].map((s) => `<img style="width:${s * 8}px;image-rendering:pixelated" src="data:image/png;base64,${
    readFileSync(path.join(out, `icon-${s}.png`)).toString('base64')}">`).join('');
  const board = `<!doctype html><html><body style="margin:0;font:13px sans-serif">${row('#202020')}${row('#f3f3f3')}
    <div style="display:flex;gap:24px;padding:20px;background:#202020">${zoom}</div></body></html>`;
  mkdirSync(path.dirname(proof), { recursive: true });
  writeFileSync(proof, await shot(b, board, 760, 900));
  console.log(`proof → ${proof}`);
} finally {
  await b.close();
}
