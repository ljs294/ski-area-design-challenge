// Render a brand board (or any page) to JPEG stills: one of the whole page and one per section, 1080 px wide, for
// review on a phone. Waits for the page's web fonts and exits non-zero if any failed to load.
// Usage: node tools/brand/render.mjs <page.html> <out prefix> [--width 1300] [--select "section"] [--quality 82]
import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { evaluate, launch, open } from './edge.mjs';

const args = process.argv.slice(2);
const arg = (name, fallback) => { const i = args.indexOf(name); return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback; };
const [page, prefix] = args;
if (!page || !prefix) { console.error('usage: render.mjs <page.html> <out prefix> [--width 1300] [--select "section"]'); process.exit(2); }
const width = +arg('--width', 1300), select = arg('--select', 'section'), quality = +arg('--quality', 82);

const b = await launch();
try {
  const fonts = await open(b, page, width);
  if (fonts.missing.length) { console.error('fonts failed to load:', fonts.missing.join(', ')); process.exitCode = 1; }
  mkdirSync(path.dirname(path.resolve(prefix)), { recursive: true });
  const height = await evaluate(b, 'Math.ceil(document.documentElement.scrollHeight)');
  const full = await b.send('Page.captureScreenshot', { format: 'jpeg', quality, captureBeyondViewport: true,
    clip: { x: 0, y: 0, width, height, scale: 1 } });
  writeFileSync(`${prefix}-full.jpg`, Buffer.from(full.data, 'base64'));
  const boxes = await evaluate(b, `[...document.querySelectorAll(${JSON.stringify(select)})].map((e) => {
    const r = e.getBoundingClientRect(); return { x: r.left + scrollX, y: r.top + scrollY, w: r.width, h: r.height, id: e.id || '' };
  })`);
  let n = 0;
  for (const r of boxes) {
    if (r.w < 10 || r.h < 10) continue;
    const s = await b.send('Page.captureScreenshot', { format: 'jpeg', quality, captureBeyondViewport: true,
      clip: { x: r.x, y: r.y, width: r.w, height: r.h, scale: 1080 / r.w } });
    writeFileSync(`${prefix}-${String(n).padStart(2, '0')}${r.id ? '-' + r.id : ''}.jpg`, Buffer.from(s.data, 'base64'));
    n++;
  }
  console.log(`rendered ${prefix}-full.jpg (${width}x${height}) and ${n} section stills; fonts: ${fonts.families.join(', ') || 'none'}`);
} finally {
  await b.close();
}
