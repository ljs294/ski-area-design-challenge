import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { chromium } from 'playwright';
import { bakeAlpineTile } from './alpineMenuTexture.mjs';

/** Bake alpine materials from preserved cover + DEM, with neighboring gutters. */
export async function smoothMenuBackground(root, page) {
  await page.addScriptTag({ content: `window.bakeAlpineTile = ${bakeAlpineTile.toString()};` });
  const manifest = JSON.parse(await readFile(path.join(root, 'manifest.json'), 'utf8'));
  const names = Object.keys(manifest.files).filter((name) => name.startsWith('cover/'));
  for (const name of names) {
    const [, zs, xs, ys] = name.split('/');
    const z = Number(zs), x = Number(xs), y = Number(ys.replace('.png', ''));
    const mosaics = [];
    for (const kind of ['cover', 'terrain']) {
      const neighbors = [];
      for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
        const tx = (x + dx + 2 ** z) % 2 ** z;
        const neighbor = `${kind}/${z}/${tx}/${y + dy}.png`;
        neighbors.push(manifest.files[neighbor] ? (await readFile(path.join(root, neighbor))).toString('base64') : null);
      }
      if (!neighbors[4]) throw new Error(`Missing ${kind} input for ${name}`);
      mosaics.push(neighbors);
    }
    const output = await page.evaluate(async ({ mosaics, z, x, y }) => {
      const decoded = [];
      let size;
      for (const [kind, neighbors] of mosaics.entries()) {
        const bitmaps = await Promise.all(neighbors.map(async (value) => value
          ? createImageBitmap(new Blob([Uint8Array.from(atob(value), (c) => c.charCodeAt(0))], { type: 'image/png' })) : null));
        try {
          const center = bitmaps[4];
          if (size !== undefined && center.width !== size) throw new Error('Cover and DEM dimensions differ');
          size = center.width;
          if (bitmaps.some(bitmap => bitmap && (bitmap.width !== size || bitmap.height !== size))) {
            throw new Error('Menu input tiles must have matching square dimensions');
          }
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
          const softened = document.createElement('canvas'); softened.width = softened.height = size * 3;
          const out = softened.getContext('2d');
          // Only soften cover boundaries; never blur elevation or the baked texture.
          if (kind === 0) out.filter = 'blur(2px)';
          out.drawImage(canvas, 0, 0);
          decoded.push(out.getImageData(0, 0, size * 3, size * 3).data);
        } finally { bitmaps.forEach((bitmap) => bitmap?.close()); }
      }
      const result = document.createElement('canvas'); result.width = result.height = size;
      const pixels = window.bakeAlpineTile({ cover: decoded[0], terrain: decoded[1], size, z, x, y });
      result.getContext('2d').putImageData(new ImageData(pixels, size, size), 0, 0);
      return result.toDataURL('image/png').split(',')[1];
    }, { mosaics, z, x, y });
    const data = Buffer.from(output, 'base64'), target = name.replace('cover/', 'smooth-cover/');
    await mkdir(path.dirname(path.join(root, target)), { recursive: true });
    await writeFile(path.join(root, target), data);
    manifest.files[target] = { bytes: data.length, sha256: createHash('sha256').update(data).digest('hex') };
  }
  manifest.coverSmoothing = { radiusPx: 2, gutter: 'adjacent source tiles', source: 'cover', output: 'smooth-cover' };
  manifest.materialBake = { version: 1, scene: 'alpine-morning', inputs: ['cover', 'terrain'],
    textureCoordinates: 'global Web Mercator metres', snow: 'decorative elevation and slope mask; not observed snow cover' };
  await writeFile(path.join(root, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n');
  console.log(`Prepared ${names.length} alpine-morning cover tiles.`);
}

if (path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const browser = await chromium.launch();
  try { await smoothMenuBackground(path.resolve('public/menu-background'), await browser.newPage()); }
  finally { await browser.close(); }
}
