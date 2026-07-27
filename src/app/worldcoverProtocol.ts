import maplibregl from 'maplibre-gl';
import type { SiteCoverGrid, WorldCoverClassCode } from '../types';
import { METERS_PER_DEGREE_LAT } from '../geo';

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

// Stable bucket ordering, so the grid sampler can pack each cell as a small
// integer index and the vectorizer can round-trip it back to a bucket name.
export const BUCKET_ORDER: CoverBucket[] = ['tree', 'grass', 'alpine', 'rock', 'water'];
/** Sentinel cell value for "no data / tile missing" in a sampled cover grid. */
export const COVER_NODATA = 255;

const BUCKET_INDEX: Record<CoverBucket, number> = {
  tree: 0,
  grass: 1,
  alpine: 2,
  rock: 3,
  water: 4,
};

// ESA WorldCover class code -> [nativeR,nativeG,nativeB] (official palette) -> our bucket.
const CLASS_TABLE: { code: WorldCoverClassCode; rgb: [number, number, number]; bucket: CoverBucket }[] = [
  { code: 10, rgb: [0, 100, 0], bucket: 'tree' },
  { code: 20, rgb: [255, 187, 34], bucket: 'grass' },
  { code: 30, rgb: [255, 255, 76], bucket: 'grass' },
  { code: 40, rgb: [240, 150, 255], bucket: 'grass' },
  { code: 50, rgb: [250, 0, 0], bucket: 'grass' },
  { code: 60, rgb: [180, 180, 180], bucket: 'rock' },
  { code: 70, rgb: [240, 240, 240], bucket: 'alpine' },
  { code: 80, rgb: [0, 100, 200], bucket: 'water' },
  { code: 90, rgb: [0, 150, 160], bucket: 'water' },
  { code: 95, rgb: [0, 207, 117], bucket: 'tree' },
  { code: 100, rgb: [250, 230, 160], bucket: 'alpine' },
];

const EXACT_CLASS = new Map(CLASS_TABLE.map((c) => [c.rgb.join(','), c.code]));

/** Exact official-palette lookup. Unknown pixels are nodata, never clear land. */
export function worldCoverCodeForRgb(r: number, g: number, b: number): WorldCoverClassCode {
  return EXACT_CLASS.get(`${r},${g},${b}`) ?? 255;
}

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

async function decodeNativeResponse(resp: Response | null): Promise<ImageData | null> {
  if (!resp || !resp.ok) return null;
  const bmp = await createImageBitmap(await resp.blob());
  const c = document.createElement('canvas');
  c.width = bmp.width;
  c.height = bmp.height;
  const ctx = c.getContext('2d', { willReadFrequently: true })!;
  ctx.drawImage(bmp, 0, 0);
  bmp.close();
  return ctx.getImageData(0, 0, c.width, c.height);
}

// The ESA WorldCover WMTS (terrascope.be) is a public EU service that
// intermittently drops connections ("Failed to fetch") or returns transient
// 5xx. During ingest a single unguarded tile failure would reject the whole
// Promise.all and abort the entire resort preparation — the root cause of the
// frequent "Preparation failed: Failed to fetch". Retry those transient
// failures so a blip on one of many parallel tiles no longer sinks the job.
const TILE_RETRIES = 3;
const TILE_RETRY_BASE_MS = 400;

function tileSleep(ms: number, signal?: AbortSignal): Promise<void> {
  if (signal?.aborted) return Promise.reject(new DOMException('WorldCover download cancelled', 'AbortError'));
  return new Promise((resolve, reject) => {
    const timer = setTimeout(resolve, ms);
    signal?.addEventListener('abort', () => {
      clearTimeout(timer);
      reject(new DOMException('WorldCover download cancelled', 'AbortError'));
    }, { once: true });
  });
}

/**
 * Fetch one WMTS tile, retrying transient network errors and 5xx with a short
 * backoff. A genuine 4xx (tile outside coverage) is a real "absent" and returns
 * null without retrying. Cancellation (abort) propagates as an AbortError so
 * package preparation can unwind cleanly; every other failure resolves to null
 * (→ transparent / nodata) rather than throwing.
 */
async function fetchTileResponse(url: string, signal?: AbortSignal): Promise<Response | null> {
  for (let attempt = 0; ; attempt++) {
    try {
      const resp = await fetch(url, signal ? { signal } : {});
      if (resp.ok) return resp;
      if (resp.status < 500) return null; // real "absent" — don't retry
    } catch (e) {
      if (signal?.aborted || (e instanceof DOMException && e.name === 'AbortError')) {
        throw new DOMException('WorldCover download cancelled', 'AbortError');
      }
      // network TypeError ("Failed to fetch") — retryable
    }
    if (attempt >= TILE_RETRIES) return null;
    await tileSleep(TILE_RETRY_BASE_MS * (attempt + 1), signal);
  }
}

function fetchNativeTile(z: number, x: number, y: number, signal?: AbortSignal): Promise<ImageData | null> {
  const url = WORLDCOVER_TILES.replace('{z}', String(z)).replace('{x}', String(x)).replace('{y}', String(y));
  // Package preparation must be cancellable. Its fetch bypasses the shared
  // preview cache so the AbortSignal reaches the underlying network request.
  if (signal) return fetchTileResponse(url, signal).then(decodeNativeResponse);
  const k = `${z}/${x}/${y}`;
  let p = nativeCache.get(k);
  if (p) return p;
  p = (async () => {
    const resp = await fetchTileResponse(url).catch(() => null);
    return decodeNativeResponse(resp);
  })();
  nativeCache.set(k, p);
  if (nativeCache.size > NATIVE_CACHE_MAX) {
    const oldest = nativeCache.keys().next().value;
    if (oldest !== undefined) nativeCache.delete(oldest);
  }
  return p;
}

// Global (fractional) tile coordinates in the Web-Mercator pyramid.
function lngToXf(lng: number, nTiles: number): number {
  return ((lng + 180) / 360) * nTiles;
}
function latToYf(lat: number, nTiles: number): number {
  const latRad = (lat * Math.PI) / 180;
  return ((1 - Math.log(Math.tan(latRad) + 1 / Math.cos(latRad)) / Math.PI) / 2) * nTiles;
}

/** Ground-cover bucket at a lng/lat, or null (nodata / tile missing). */
export async function sampleCoverAt(lng: number, lat: number, z: number): Promise<CoverBucket | null> {
  const zz = Math.min(WC_MAXZOOM, z);
  const nTiles = 1 << zz;
  const xf = lngToXf(lng, nTiles);
  const yf = latToYf(lat, nTiles);
  const img = await fetchNativeTile(zz, Math.floor(xf), Math.floor(yf));
  if (!img) return null;
  const px = Math.min(img.width - 1, Math.floor((xf - Math.floor(xf)) * img.width));
  const py = Math.min(img.height - 1, Math.floor((yf - Math.floor(yf)) * img.height));
  const i = (py * img.width + px) * 4;
  if (img.data[i + 3] === 0) return null; // nodata
  return nearestBucket(img.data[i], img.data[i + 1], img.data[i + 2]);
}

/** Bounds of a rectangular area to sample, in degrees. */
export interface CoverBounds {
  west: number;
  south: number;
  east: number;
  north: number;
}

/**
 * Sample an n×n grid of cover-bucket indices across `bounds` (row 0 = north
 * edge, matching the renderer's grid convention). Fetches only the WorldCover
 * tiles the area overlaps — a handful even for the largest site — then reads
 * every cell from that decoded, cached set. Cells with no data (missing tile /
 * transparent pixel) are `COVER_NODATA`. Run once when a site is locked.
 */
export async function sampleCoverGrid(bounds: CoverBounds, n: number): Promise<Uint8Array> {
  const zz = WC_MAXZOOM;
  const nTiles = 1 << zz;

  const xfW = lngToXf(bounds.west, nTiles);
  const xfE = lngToXf(bounds.east, nTiles);
  const yfN = latToYf(bounds.north, nTiles);
  const yfS = latToYf(bounds.south, nTiles);
  const xTileMin = Math.floor(Math.min(xfW, xfE));
  const xTileMax = Math.floor(Math.max(xfW, xfE));
  const yTileMin = Math.floor(Math.min(yfN, yfS));
  const yTileMax = Math.floor(Math.max(yfN, yfS));

  const tiles = new Map<string, ImageData | null>();
  const jobs: Promise<void>[] = [];
  for (let tx = xTileMin; tx <= xTileMax; tx++) {
    for (let ty = yTileMin; ty <= yTileMax; ty++) {
      jobs.push(
        fetchNativeTile(zz, tx, ty)
          .then((img) => void tiles.set(`${tx}/${ty}`, img))
          .catch(() => void tiles.set(`${tx}/${ty}`, null))
      );
    }
  }
  await Promise.all(jobs);

  const out = new Uint8Array(n * n).fill(COVER_NODATA);
  for (let r = 0; r < n; r++) {
    const v = n === 1 ? 0 : r / (n - 1);
    const lat = bounds.north - v * (bounds.north - bounds.south);
    const yf = latToYf(lat, nTiles);
    const ty = Math.floor(yf);
    for (let c = 0; c < n; c++) {
      const u = n === 1 ? 0 : c / (n - 1);
      const lng = bounds.west + u * (bounds.east - bounds.west);
      const xf = lngToXf(lng, nTiles);
      const tx = Math.floor(xf);
      const img = tiles.get(`${tx}/${ty}`);
      if (!img) continue;
      const px = Math.min(img.width - 1, Math.floor((xf - tx) * img.width));
      const py = Math.min(img.height - 1, Math.floor((yf - ty) * img.height));
      const i = (py * img.width + px) * 4;
      if (img.data[i + 3] === 0) continue; // nodata
      out[r * n + c] = BUCKET_INDEX[nearestBucket(img.data[i], img.data[i + 1], img.data[i + 2])];
    }
  }
  return out;
}

/**
 * Downloads a source-faithful, rectangular WorldCover grid for a locked site.
 * The grid is the persisted planning truth; no blur, simplification, or class
 * collapsing is applied here.
 */
export async function sampleSiteCoverGrid(
  bounds: CoverBounds,
  targetCellM = 10,
  signal?: AbortSignal
): Promise<SiteCoverGrid> {
  const midLat = (bounds.north + bounds.south) / 2;
  const widthM = Math.abs(bounds.east - bounds.west) * METERS_PER_DEGREE_LAT * Math.cos((midLat * Math.PI) / 180);
  const heightM = Math.abs(bounds.north - bounds.south) * METERS_PER_DEGREE_LAT;
  const { width, height } = siteCoverDimensions(bounds, targetCellM);
  const zz = WC_MAXZOOM;
  const nTiles = 1 << zz;
  const xfW = lngToXf(bounds.west, nTiles);
  const xfE = lngToXf(bounds.east, nTiles);
  const yfN = latToYf(bounds.north, nTiles);
  const yfS = latToYf(bounds.south, nTiles);
  const tiles = new Map<string, ImageData | null>();
  const jobs: Promise<void>[] = [];
  for (let tx = Math.floor(Math.min(xfW, xfE)); tx <= Math.floor(Math.max(xfW, xfE)); tx++) {
    for (let ty = Math.floor(Math.min(yfN, yfS)); ty <= Math.floor(Math.max(yfN, yfS)); ty++) {
      jobs.push(fetchNativeTile(zz, tx, ty, signal).then((img) => void tiles.set(`${tx}/${ty}`, img)));
    }
  }
  await Promise.all(jobs);
  if (signal?.aborted) throw new DOMException('Resort preparation cancelled', 'AbortError');

  const data = new Uint8Array(width * height).fill(COVER_NODATA);
  let nodataCount = 0;
  for (let r = 0; r < height; r++) {
    if (signal?.aborted) throw new DOMException('Resort preparation cancelled', 'AbortError');
    const lat = bounds.north - ((r + 0.5) / height) * (bounds.north - bounds.south);
    const yf = latToYf(lat, nTiles);
    const ty = Math.floor(yf);
    for (let c = 0; c < width; c++) {
      const lng = bounds.west + ((c + 0.5) / width) * (bounds.east - bounds.west);
      const xf = lngToXf(lng, nTiles);
      const tx = Math.floor(xf);
      const img = tiles.get(`${tx}/${ty}`);
      if (!img) { nodataCount++; continue; }
      const px = Math.min(img.width - 1, Math.floor((xf - tx) * img.width));
      const py = Math.min(img.height - 1, Math.floor((yf - ty) * img.height));
      const i = (py * img.width + px) * 4;
      const code = img.data[i + 3] === 0 ? 255 : worldCoverCodeForRgb(img.data[i], img.data[i + 1], img.data[i + 2]);
      data[r * width + c] = code;
      if (code === 255) nodataCount++;
    }
  }
  return {
    bounds,
    width,
    height,
    cellSizeM: Math.max(widthM / width, heightM / height),
    data: Array.from(data),
    complete: nodataCount === 0,
    nodataCount,
    source: 'esa-worldcover-2021-v200',
    vintage: '2021',
  };
}

export function siteCoverDimensions(bounds: CoverBounds, targetCellM = 10): { width: number; height: number } {
  const midLat = (bounds.north + bounds.south) / 2;
  const widthM = Math.abs(bounds.east - bounds.west) * METERS_PER_DEGREE_LAT * Math.cos((midLat * Math.PI) / 180);
  const heightM = Math.abs(bounds.north - bounds.south) * METERS_PER_DEGREE_LAT;
  return {
    width: Math.max(2, Math.min(1200, Math.round(widthM / targetCellM))),
    height: Math.max(2, Math.min(1200, Math.round(heightM / targetCellM))),
  };
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

    // The WMTS occasionally returns a transient 5xx or drops the connection;
    // fetchTileResponse retries those with backoff. Only a genuinely absent
    // tile (4xx / still failing after retries) falls back to transparent.
    const resp = await fetchTileResponse(realUrl, abortController.signal).catch(() => null);
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
