import maplibregl from 'maplibre-gl';

// ESA WorldCover native WMTS (KVP GetTile form — verified working, see memory).
const WORLDCOVER_TILES =
  'https://wmts.terrascope.be/?service=WMTS&request=GetTile&version=1.0.0' +
  '&layer=esa-worldcover-map-10m-2021-v2_map&style=default&format=image/png' +
  '&tilematrixset=EPSG:3857&TileMatrix={z}&TileCol={x}&TileRow={y}&TIME=2021-01-01';

export const WORLDCOVER_PROTOCOL = 'worldcover';

// Our display buckets, sharp cartographic colors.
export const COVER_BUCKETS = {
  tree: [74, 122, 71],
  grass: [199, 196, 140], // cleared / grassland / developed
  alpine: [230, 236, 242], // snow, ice, moss/lichen
  rock: [168, 158, 146], // bare / sparse vegetation
  water: [94, 142, 173],
} as const;

export type CoverBucket = keyof typeof COVER_BUCKETS;

/** Human-readable label per bucket, for the cursor readout + legend. */
export const COVER_LABELS: Record<CoverBucket, string> = {
  tree: 'Tree cover',
  grass: 'Grass / cleared',
  alpine: 'Snow / alpine',
  rock: 'Rock / bare',
  water: 'Water',
};

// ESA WorldCover class code -> [nativeR,nativeG,nativeB] (official palette) -> our bucket.
const CLASS_TABLE: { rgb: [number, number, number]; bucket: CoverBucket }[] = [
  { rgb: [0, 100, 0], bucket: 'tree' }, // 10 tree cover
  { rgb: [255, 187, 34], bucket: 'grass' }, // 20 shrubland
  { rgb: [255, 255, 76], bucket: 'grass' }, // 30 grassland
  { rgb: [240, 150, 255], bucket: 'grass' }, // 40 cropland
  { rgb: [250, 0, 0], bucket: 'grass' }, // 50 built-up (cleared/developed)
  { rgb: [180, 180, 180], bucket: 'rock' }, // 60 bare / sparse vegetation
  { rgb: [240, 240, 240], bucket: 'alpine' }, // 70 snow and ice
  { rgb: [0, 100, 200], bucket: 'water' }, // 80 permanent water
  { rgb: [0, 150, 160], bucket: 'water' }, // 90 herbaceous wetland
  { rgb: [0, 207, 117], bucket: 'tree' }, // 95 mangroves
  { rgb: [250, 230, 160], bucket: 'alpine' }, // 100 moss and lichen
];

/** Nearest official-palette class match -> our bucket. */
export function nearestBucket(r: number, g: number, b: number): CoverBucket {
  let best = CLASS_TABLE[0];
  let bestD = Infinity;
  for (const c of CLASS_TABLE) {
    const dr = r - c.rgb[0];
    const dg = g - c.rgb[1];
    const db = b - c.rgb[2];
    const d = dr * dr + dg * dg + db * db;
    if (d < bestD) {
      bestD = d;
      best = c;
    }
  }
  return best.bucket;
}

/** Nearest-bucket color, memoized by packed RGB so repeat pixels are O(1). */
const cache = new Map<number, [number, number, number]>();
function bucketColorFor(r: number, g: number, b: number): [number, number, number] {
  const key = (r << 16) | (g << 8) | b;
  const hit = cache.get(key);
  if (hit) return hit;
  const color = COVER_BUCKETS[nearestBucket(r, g, b)] as unknown as [number, number, number];
  cache.set(key, color);
  return color;
}

function parseZXY(url: string): [number, number, number] {
  const m = url.match(/\/\/(\d+)\/(\d+)\/(\d+)/);
  if (!m) throw new Error(`bad worldcover url: ${url}`);
  return [Number(m[1]), Number(m[2]), Number(m[3])];
}

let transparentTile: ArrayBuffer | null = null;
async function makeTransparentTile(): Promise<ArrayBuffer> {
  if (transparentTile) return transparentTile;
  const c = document.createElement('canvas');
  c.width = c.height = 256;
  const blob = await new Promise<Blob | null>((res) => c.toBlob(res, 'image/png'));
  transparentTile = await blob!.arrayBuffer();
  return transparentTile;
}

// ---- Point sampling (cursor readout) ---------------------------------------
// WorldCover native tiles top out at z14; caller clamps. Native tiles are
// decoded once and cached (separate from the protocol's recolor path).

const WC_MAXZOOM = 14;
const nativeCache = new Map<string, Promise<ImageData | null>>();
const NATIVE_CACHE_MAX = 64;

function fetchNativeTile(z: number, x: number, y: number): Promise<ImageData | null> {
  const k = `${z}/${x}/${y}`;
  let p = nativeCache.get(k);
  if (p) return p;
  p = (async () => {
    const url = WORLDCOVER_TILES.replace('{z}', String(z)).replace('{x}', String(x)).replace('{y}', String(y));
    const resp = await fetch(url).catch(() => null);
    if (!resp || !resp.ok) return null;
    const bmp = await createImageBitmap(await resp.blob());
    const c = document.createElement('canvas');
    c.width = bmp.width;
    c.height = bmp.height;
    const ctx = c.getContext('2d', { willReadFrequently: true })!;
    ctx.drawImage(bmp, 0, 0);
    bmp.close();
    return ctx.getImageData(0, 0, c.width, c.height);
  })();
  nativeCache.set(k, p);
  if (nativeCache.size > NATIVE_CACHE_MAX) {
    const oldest = nativeCache.keys().next().value;
    if (oldest !== undefined) nativeCache.delete(oldest);
  }
  return p;
}

/** Ground-cover bucket at a lng/lat, or null (nodata / tile missing). */
export async function sampleCoverAt(lng: number, lat: number, z: number): Promise<CoverBucket | null> {
  const zz = Math.min(WC_MAXZOOM, z);
  const nTiles = 1 << zz;
  const xf = ((lng + 180) / 360) * nTiles;
  const latRad = (lat * Math.PI) / 180;
  const yf = ((1 - Math.log(Math.tan(latRad) + 1 / Math.cos(latRad)) / Math.PI) / 2) * nTiles;
  const img = await fetchNativeTile(zz, Math.floor(xf), Math.floor(yf));
  if (!img) return null;
  const px = Math.min(img.width - 1, Math.floor((xf - Math.floor(xf)) * img.width));
  const py = Math.min(img.height - 1, Math.floor((yf - Math.floor(yf)) * img.height));
  const i = (py * img.width + px) * 4;
  if (img.data[i + 3] === 0) return null; // nodata
  return nearestBucket(img.data[i], img.data[i + 1], img.data[i + 2]);
}

let registered = false;

/**
 * Registers the `worldcover://{z}/{x}/{y}` protocol: fetches the native ESA
 * WorldCover tile and recolors each pixel into our display buckets (nearest
 * class match). Out-of-range tiles (server 400) become transparent so they
 * don't error. Smoothing is done at display time via `raster-resampling: linear`.
 */
export function registerWorldcoverProtocol(): void {
  if (registered) return;
  registered = true;

  maplibregl.addProtocol(WORLDCOVER_PROTOCOL, async (params, abortController) => {
    const [z, x, y] = parseZXY(params.url);
    const realUrl = WORLDCOVER_TILES.replace('{z}', String(z))
      .replace('{x}', String(x))
      .replace('{y}', String(y));

    // The WMTS occasionally returns a transient 5xx/400; one retry clears it.
    // Only a genuinely absent tile (still failing) falls back to transparent.
    let resp = await fetch(realUrl, { signal: abortController.signal }).catch(() => null);
    if (!resp || !resp.ok) {
      resp = await fetch(realUrl, { signal: abortController.signal }).catch(() => null);
    }
    if (!resp || !resp.ok) {
      console.warn(`[worldcover] tile ${z}/${x}/${y} failed (${resp?.status ?? 'network'})`);
      return { data: await makeTransparentTile() };
    }

    const bmp = await createImageBitmap(await resp.blob());
    const canvas = document.createElement('canvas');
    canvas.width = bmp.width;
    canvas.height = bmp.height;
    const ctx = canvas.getContext('2d', { willReadFrequently: true })!;
    ctx.drawImage(bmp, 0, 0);
    bmp.close();

    const img = ctx.getImageData(0, 0, canvas.width, canvas.height);
    const d = img.data;
    for (let i = 0; i < d.length; i += 4) {
      if (d[i + 3] === 0) continue; // keep nodata transparent
      const [r, g, b] = bucketColorFor(d[i], d[i + 1], d[i + 2]);
      d[i] = r;
      d[i + 1] = g;
      d[i + 2] = b;
    }
    ctx.putImageData(img, 0, 0);

    const outBlob = await new Promise<Blob | null>((res) => canvas.toBlob(res, 'image/png'));
    return { data: await outBlob!.arrayBuffer() };
  });
}
