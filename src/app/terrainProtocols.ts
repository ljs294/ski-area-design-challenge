import maplibregl from 'maplibre-gl';

// Slope-angle + aspect custom tile protocols, computed live from Terrarium DEM
// tiles. Each output tile pulls its Terrarium tile plus 8 neighbors so central
// differences have real data at the edges (no seams). Decoded elevation tiles
// are cached and shared between the slope and aspect handlers.

const TERRARIUM = 'https://elevation-tiles-prod.s3.amazonaws.com/terrarium/{z}/{x}/{y}.png';
const TILE = 256;

export const SLOPE_PROTOCOL = 'slope';
export const ASPECT_PROTOCOL = 'aspect';

// ---- Shared decoded-elevation cache ----------------------------------------

const demCache = new Map<string, Promise<Float32Array>>();
const DEM_CACHE_MAX = 256;

function key(z: number, x: number, y: number): string {
  return `${z}/${x}/${y}`;
}

async function decodeTile(z: number, x: number, y: number, signal: AbortSignal): Promise<Float32Array> {
  const url = TERRARIUM.replace('{z}', String(z)).replace('{x}', String(x)).replace('{y}', String(y));
  const resp = await fetch(url, { signal }).catch(() => null);
  const out = new Float32Array(TILE * TILE);
  if (!resp || !resp.ok) return out; // missing tile -> zeros (ocean/void)

  const bmp = await createImageBitmap(await resp.blob());
  const canvas = document.createElement('canvas');
  canvas.width = TILE;
  canvas.height = TILE;
  const ctx = canvas.getContext('2d', { willReadFrequently: true })!;
  ctx.drawImage(bmp, 0, 0, TILE, TILE);
  bmp.close();
  const d = ctx.getImageData(0, 0, TILE, TILE).data;
  for (let i = 0, p = 0; i < out.length; i++, p += 4) {
    // Terrarium decode.
    out[i] = d[p] * 256 + d[p + 1] + d[p + 2] / 256 - 32768;
  }
  return out;
}

function getDem(z: number, x: number, y: number, signal: AbortSignal): Promise<Float32Array> {
  // Wrap negative/overflow tile coords (x wraps; y out of range -> zeros).
  const n = 1 << z;
  if (y < 0 || y >= n) return Promise.resolve(new Float32Array(TILE * TILE));
  const wx = ((x % n) + n) % n;
  const k = key(z, wx, y);
  let p = demCache.get(k);
  if (!p) {
    p = decodeTile(z, wx, y, signal);
    demCache.set(k, p);
    if (demCache.size > DEM_CACHE_MAX) {
      const oldest = demCache.keys().next().value;
      if (oldest !== undefined) demCache.delete(oldest);
    }
  }
  return p;
}

/** Sample elevation at padded-grid coord (gx,gy in -1..TILE) from center + neighbor tiles. */
function makeSampler(
  center: Float32Array,
  n: Float32Array,
  s: Float32Array,
  e: Float32Array,
  w: Float32Array,
  ne: Float32Array,
  nw: Float32Array,
  se: Float32Array,
  sw: Float32Array
) {
  return (gx: number, gy: number): number => {
    let tile = center;
    let lx = gx;
    let ly = gy;
    if (gy < 0) {
      ly = gy + TILE;
      if (gx < 0) { tile = nw; lx = gx + TILE; }
      else if (gx >= TILE) { tile = ne; lx = gx - TILE; }
      else tile = n;
    } else if (gy >= TILE) {
      ly = gy - TILE;
      if (gx < 0) { tile = sw; lx = gx + TILE; }
      else if (gx >= TILE) { tile = se; lx = gx - TILE; }
      else tile = s;
    } else if (gx < 0) { tile = w; lx = gx + TILE; }
    else if (gx >= TILE) { tile = e; lx = gx - TILE; }
    return tile[ly * TILE + lx];
  };
}

/** Web-Mercator ground resolution (m/px) at the given tile's center latitude. */
function metersPerPixel(z: number, y: number): number {
  const nTiles = 1 << z;
  const latRad = Math.atan(Math.sinh(Math.PI * (1 - (2 * (y + 0.5)) / nTiles)));
  return (156543.03392 * Math.cos(latRad)) / nTiles;
}

async function gatherTiles(z: number, x: number, y: number, signal: AbortSignal) {
  const [center, n, s, e, w, ne, nw, se, sw] = await Promise.all([
    getDem(z, x, y, signal),
    getDem(z, x, y - 1, signal),
    getDem(z, x, y + 1, signal),
    getDem(z, x + 1, y, signal),
    getDem(z, x - 1, y, signal),
    getDem(z, x + 1, y - 1, signal),
    getDem(z, x - 1, y - 1, signal),
    getDem(z, x + 1, y + 1, signal),
    getDem(z, x - 1, y + 1, signal),
  ]);
  return makeSampler(center, n, s, e, w, ne, nw, se, sw);
}

function parseZXY(url: string): [number, number, number] {
  const m = url.match(/\/\/(\d+)\/(\d+)\/(\d+)/);
  if (!m) throw new Error(`bad tile url: ${url}`);
  return [Number(m[1]), Number(m[2]), Number(m[3])];
}

function toPngArrayBuffer(img: ImageData): Promise<ArrayBuffer> {
  const canvas = document.createElement('canvas');
  canvas.width = TILE;
  canvas.height = TILE;
  canvas.getContext('2d')!.putImageData(img, 0, 0);
  return new Promise<ArrayBuffer>((resolve, reject) => {
    canvas.toBlob((b) => {
      if (!b) return reject(new Error('toBlob returned null'));
      b.arrayBuffer().then(resolve, reject);
    }, 'image/png');
  });
}

// ---- Color ramps ------------------------------------------------------------
// Bands/sectors are the single source of truth for BOTH the rendered overlays
// and the on-screen legends (exported below), so they can never drift apart.

const OVERLAY_ALPHA = 150; // ~0.59

/** Ski-run difficulty bands by slope angle (upper bound exclusive). <6° is too
 * flat to ski and stays transparent. */
const SLOPE_BANDS: { max: number; rgb: [number, number, number]; label: string }[] = [
  { max: 16, rgb: [67, 160, 71], label: 'Green · 6–15°' }, // easiest
  { max: 24, rgb: [30, 136, 229], label: 'Blue · 16–23°' }, // intermediate
  { max: 37, rgb: [33, 33, 33], label: 'Black · 24–36°' }, // advanced
  { max: Infinity, rgb: [211, 47, 47], label: 'Red · ≥37°' }, // expert
];

/** 8-point compass sectors, index 0 = N, clockwise. */
const ASPECT_LABELS = ['N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW'] as const;
const ASPECT_SECTORS: [number, number, number][] = [
  [66, 133, 244], // N  blue
  [45, 165, 190], // NE teal
  [46, 170, 100], // E  green
  [140, 190, 60], // SE yellow-green
  [245, 214, 60], // S  yellow
  [240, 150, 50], // SW orange
  [220, 70, 70], // W  red
  [150, 90, 200], // NW purple
];

/** Legend rows for the UI — derived from the same ramp data the tiles use. */
export const SLOPE_LEGEND = SLOPE_BANDS.map((b) => ({
  label: b.label,
  color: `rgb(${b.rgb[0]}, ${b.rgb[1]}, ${b.rgb[2]})`,
}));
export const ASPECT_LEGEND = ASPECT_SECTORS.map((c, i) => ({
  label: ASPECT_LABELS[i],
  color: `rgb(${c[0]}, ${c[1]}, ${c[2]})`,
}));

/** Ski grading: transparent <6 (too flat), then green, blue, black, red. */
function slopeColor(deg: number, out: [number, number, number, number]): void {
  if (deg < 6) { out[3] = 0; return; }
  for (const b of SLOPE_BANDS) {
    if (deg < b.max) {
      out[0] = b.rgb[0]; out[1] = b.rgb[1]; out[2] = b.rgb[2]; out[3] = OVERLAY_ALPHA;
      return;
    }
  }
}

/** 8-sector compass wheel (0=N clockwise). Transparent on near-flat ground. */
function aspectColor(deg: number, slopeDeg: number, out: [number, number, number, number]): void {
  if (slopeDeg < 4) { out[3] = 0; return; }
  const c = ASPECT_SECTORS[Math.round(deg / 45) % 8];
  out[0] = c[0]; out[1] = c[1]; out[2] = c[2]; out[3] = OVERLAY_ALPHA;
}

/**
 * Slope exposure = compass bearing the slope FACES (downhill / steepest
 * descent), 0=N clockwise. Gradient (dzdx east, dzdy south) points uphill, so
 * downhill north-component is +dzdy and east-component is -dzdx.
 */
function exposureBearing(dzdx: number, dzdy: number): number {
  const deg = (Math.atan2(-dzdx, dzdy) * 180) / Math.PI;
  return (deg + 360) % 360;
}

/** 8-point compass label for a bearing (0=N clockwise). */
export function compass8(deg: number): string {
  return ASPECT_LABELS[Math.round(((deg % 360) + 360) % 360 / 45) % 8];
}

// ---- Handlers ---------------------------------------------------------------

type Mode = 'slope' | 'aspect';

async function renderTile(url: string, signal: AbortSignal, mode: Mode): Promise<ArrayBuffer> {
  const [z, x, y] = parseZXY(url);
  const sample = await gatherTiles(z, x, y, signal);
  const mpp = metersPerPixel(z, y);
  const img = new ImageData(TILE, TILE);
  const d = img.data;
  const rgba: [number, number, number, number] = [0, 0, 0, 0];

  for (let py = 0; py < TILE; py++) {
    for (let px = 0; px < TILE; px++) {
      const dzdx = (sample(px + 1, py) - sample(px - 1, py)) / (2 * mpp);
      const dzdy = (sample(px, py + 1) - sample(px, py - 1)) / (2 * mpp);
      const slopeDeg = (Math.atan(Math.hypot(dzdx, dzdy)) * 180) / Math.PI;

      if (mode === 'slope') {
        slopeColor(slopeDeg, rgba);
      } else {
        aspectColor(exposureBearing(dzdx, dzdy), slopeDeg, rgba);
      }

      const p = (py * TILE + px) * 4;
      d[p] = rgba[0]; d[p + 1] = rgba[1]; d[p + 2] = rgba[2]; d[p + 3] = rgba[3];
    }
  }
  return toPngArrayBuffer(img);
}

// ---- Point sampling (cursor readout) ---------------------------------------
// Reuses the same tile cache + math as the overlays, so the readout matches
// the shaded tiles exactly.

/** Bilinear elevation at a fractional pixel (px,py) via the padded sampler. */
function bilinear(sample: (gx: number, gy: number) => number, px: number, py: number): number {
  const x0 = Math.floor(px);
  const y0 = Math.floor(py);
  const fx = px - x0;
  const fy = py - y0;
  const a = sample(x0, y0);
  const b = sample(x0 + 1, y0);
  const c = sample(x0, y0 + 1);
  const d = sample(x0 + 1, y0 + 1);
  return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy;
}

export interface TerrainSample {
  elevation: number; // meters
  slopeDeg: number;
  aspectDeg: number; // exposure, 0=N clockwise
}

/**
 * Sample elevation (bilinear), slope, and exposure at a lng/lat. `z` is the
 * DEM zoom to sample at (caller clamps to a sensible range). Never aborts its
 * tile fetches — that would poison the shared DEM cache with zero tiles.
 */
export async function sampleTerrainAt(lng: number, lat: number, z: number): Promise<TerrainSample> {
  const nTiles = 1 << z;
  const xf = ((lng + 180) / 360) * nTiles;
  const latRad = (lat * Math.PI) / 180;
  const yf = ((1 - Math.log(Math.tan(latRad) + 1 / Math.cos(latRad)) / Math.PI) / 2) * nTiles;
  const tx = Math.floor(xf);
  const ty = Math.floor(yf);
  const sample = await gatherTiles(z, tx, ty, new AbortController().signal);

  const px = (xf - tx) * TILE;
  const py = (yf - ty) * TILE;
  const elevation = bilinear(sample, px, py);

  const pxi = Math.round(px);
  const pyi = Math.round(py);
  const mpp = metersPerPixel(z, ty);
  const dzdx = (sample(pxi + 1, pyi) - sample(pxi - 1, pyi)) / (2 * mpp);
  const dzdy = (sample(pxi, pyi + 1) - sample(pxi, pyi - 1)) / (2 * mpp);
  const slopeDeg = (Math.atan(Math.hypot(dzdx, dzdy)) * 180) / Math.PI;
  return { elevation, slopeDeg, aspectDeg: exposureBearing(dzdx, dzdy) };
}

let registered = false;

export function registerTerrainProtocols(): void {
  if (registered) return;
  registered = true;
  maplibregl.addProtocol(SLOPE_PROTOCOL, (params, ac) => renderTile(params.url, ac.signal, 'slope').then((data) => ({ data })));
  maplibregl.addProtocol(ASPECT_PROTOCOL, (params, ac) => renderTile(params.url, ac.signal, 'aspect').then((data) => ({ data })));
}
