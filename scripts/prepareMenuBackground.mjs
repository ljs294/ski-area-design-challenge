import { chromium } from 'playwright';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { smoothMenuBackground } from './smoothMenuBackground.mjs';

const root = path.resolve('public/menu-background');
const center = [-121.474, 46.928];
const hash = (data) => createHash('sha256').update(data).digest('hex');
const files = {};
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage();
const palette = [
  [[0,100,0],[82,114,89]], [[255,187,34],[145,165,121]], [[255,255,76],[145,165,121]],
  [[240,150,255],[168,173,128]], [[250,0,0],[168,173,128]], [[180,180,180],[170,166,155]],
  [[240,240,240],[230,238,233]], [[0,100,200],[82,140,164]], [[0,150,160],[82,140,164]],
  [[0,207,117],[82,114,89]], [[250,230,160],[230,238,233]],
];
await page.evaluate((palette) => {
  window.recolor = async (encoded) => {
    const bytes = Uint8Array.from(atob(encoded), (c) => c.charCodeAt(0));
    const bitmap = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
    const canvas = document.createElement('canvas'); canvas.width = bitmap.width; canvas.height = bitmap.height;
    const ctx = canvas.getContext('2d'); ctx.drawImage(bitmap, 0, 0); bitmap.close();
    const image = ctx.getImageData(0, 0, canvas.width, canvas.height);
    const data = image.data;
    for (let i = 0; i < data.length; i += 4) {
      let best = Infinity, color = [172,169,154];
      for (const [source, target] of palette) {
        const distance = source.reduce((sum, c, j) => sum + (c - data[i+j]) ** 2, 0);
        if (distance < best) { best = distance; color = target; }
      }
      // Coarse world tiles include areas outside the product footprint.
      if (data[i+3] !== 255) color = [172,169,154];
      data.set(color, i); data[i+3] = 255;
    }
    ctx.putImageData(image, 0, 0);
    return canvas.toDataURL('image/png').split(',')[1];
  };
}, palette);

async function download(url) {
  for (let attempt = 0; attempt < 4; attempt++) {
    try {
      const response = await fetch(url, { signal: AbortSignal.timeout(45000) });
      if (!response.ok) throw new Error(`${response.status}: ${url}`);
      const bytes = Buffer.from(await response.arrayBuffer());
      if (bytes.subarray(1,4).toString() !== 'PNG') throw new Error(`Not a PNG: ${url}`);
      return bytes;
    } catch (error) {
      if (attempt === 3) throw error;
      await new Promise((resolve) => setTimeout(resolve, 500 * (attempt+1)));
    }
  }
}

try {
  await mkdir(root, { recursive: true });
  const notice = await fetch('https://raw.githubusercontent.com/tilezen/joerd/master/docs/attribution.md');
  if (!notice.ok) throw new Error('Could not retrieve terrain source attribution');
  await writeFile(path.join(root, 'TERRAIN-SOURCES.md'), await notice.text());
  const jobs = [];
  // A nested pyramid: detailed summit tiles, progressively wider horizon coverage.
  // The runtime falls back to a bundled ancestor outside each detailed ring.
  for (let z = 0; z <= 14; z++) {
    const n = 2 ** z;
    const cx = Math.floor((center[0]+180)/360*n);
    const cy = Math.floor((1-Math.asinh(Math.tan(center[1]*Math.PI/180))/Math.PI)/2*n);
    const radius = z < 8 ? 0 : 2;
    for (let x = Math.max(0,cx-radius); x <= Math.min(n-1,cx+radius); x++) {
      for (let y = Math.max(0,cy-radius); y <= Math.min(n-1,cy+radius); y++) {
        for (const kind of ['terrain','cover']) jobs.push({kind,z,x,y});
      }
    }
  }
  let completed = 0;
  const worker = async () => {
    while (jobs.length) {
      const {kind,z,x,y} = jobs.shift();
      const relative = `${kind}/${z}/${x}/${y}.png`, target = path.join(root, relative);
      let data;
      try { data = await readFile(target); } catch {
        const url = kind === 'terrain'
          ? `https://elevation-tiles-prod.s3.amazonaws.com/terrarium/${z}/${x}/${y}.png`
          : `https://wmts.terrascope.be/?service=WMTS&request=GetTile&version=1.0.0&layer=esa-worldcover-map-10m-2021-v2_map&style=default&format=image/png&tilematrixset=EPSG:3857&TileMatrix=${z}&TileCol=${x}&TileRow=${y}&TIME=2021-01-01`;
        data = await download(url);
        if (kind === 'cover') data = Buffer.from(await page.evaluate((encoded) => window.recolor(encoded), data.toString('base64')), 'base64');
        await mkdir(path.dirname(target), {recursive:true}); await writeFile(target,data);
      }
      files[relative] = { bytes:data.length, sha256:hash(data) };
      if (++completed % 20 === 0) console.log(`Prepared ${completed} tiles (${jobs.length} remaining)`);
    }
  };
  await Promise.all(Array.from({length:6},worker));
  const manifest = { version:1, center, maxzoom:14, tileSize:256,
    bounds:[-180,-85.051129,180,85.051129],
    coverage:'Nested 5x5 rings at zooms 8–14; center ancestors at zooms 0–7; ancestor fallback outside detailed rings.',
    attribution:'© ESA WorldCover project 2021 / Contains modified Copernicus Sentinel data (2021), processed by ESA WorldCover consortium; recolored by Mountain Planner. CC BY 4.0. Elevation: Mapzen Terrain Tiles, USGS and other sources.',
    sources:['https://esa-worldcover.org/en/data-access','https://registry.opendata.aws/terrain-tiles/','https://github.com/tilezen/joerd/blob/master/docs/attribution.md'],
    files:Object.fromEntries(Object.entries(files).sort(([a],[b])=>a.localeCompare(b))) };
  await writeFile(path.join(root,'manifest.json'),JSON.stringify(manifest,null,2)+'\n');
  await smoothMenuBackground(root, page);
  console.log(`Prepared ${Object.keys(files).length} tiles, ${Object.values(files).reduce((s,v)=>s+v.bytes,0)} bytes`);
} finally { await browser.close(); }
