import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { chromium } from 'playwright';

/** Bake soft class boundaries with neighboring tiles as a gutter to avoid seams. */
export async function smoothMenuBackground(root, page) {
  const manifest = JSON.parse(await readFile(path.join(root, 'manifest.json'), 'utf8'));
  const names = Object.keys(manifest.files).filter((name) => name.startsWith('cover/'));
  for (const name of names) {
    const [, zs, xs, ys] = name.split('/');
    const z = Number(zs), x = Number(xs), y = Number(ys.replace('.png', ''));
    const neighbors = [];
    for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
      const neighbor = `cover/${z}/${x + dx}/${y + dy}.png`;
      neighbors.push(manifest.files[neighbor] ? (await readFile(path.join(root, neighbor))).toString('base64') : null);
    }
    const output = await page.evaluate(async (neighbors) => {
      const bitmaps = await Promise.all(neighbors.map(async (value) => value
        ? createImageBitmap(new Blob([Uint8Array.from(atob(value), (c) => c.charCodeAt(0))], { type: 'image/png' })) : null));
      const center = bitmaps[4], size = center.width;
      const canvas = document.createElement('canvas'); canvas.width = canvas.height = size * 3;
      const ctx = canvas.getContext('2d');
      // Missing neighbors at the package edge extend the last pixel, not a repeated tile.
      for (let i = 0; i < 9; i++) {
        const col = i % 3, row = Math.floor(i / 3), bitmap = bitmaps[i];
        if (bitmap) ctx.drawImage(bitmap, col * size, row * size, size, size);
        else ctx.drawImage(center, col === 0 ? 0 : col === 2 ? size - 1 : 0,
          row === 0 ? 0 : row === 2 ? size - 1 : 0, col === 1 ? size : 1, row === 1 ? size : 1,
          col * size, row * size, size, size);
      }
      const result = document.createElement('canvas'); result.width = result.height = size;
      const out = result.getContext('2d'); out.filter = 'blur(2px)';
      out.drawImage(canvas, -size, -size);
      bitmaps.forEach((bitmap) => bitmap?.close());
      return result.toDataURL('image/png').split(',')[1];
    }, neighbors);
    const data = Buffer.from(output, 'base64'), target = name.replace('cover/', 'smooth-cover/');
    await mkdir(path.dirname(path.join(root, target)), { recursive: true });
    await writeFile(path.join(root, target), data);
    manifest.files[target] = { bytes: data.length, sha256: createHash('sha256').update(data).digest('hex') };
  }
  manifest.coverSmoothing = { radiusPx: 2, gutter: 'adjacent source tiles', source: 'cover', output: 'smooth-cover' };
  await writeFile(path.join(root, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n');
  console.log(`Prepared ${names.length} seamless smoothed cover tiles.`);
}

if (path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const browser = await chromium.launch();
  try { await smoothMenuBackground(path.resolve('public/menu-background'), await browser.newPage()); }
  finally { await browser.close(); }
}
