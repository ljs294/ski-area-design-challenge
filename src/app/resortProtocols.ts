import maplibregl from 'maplibre-gl';
import type { CoverClassCode, LandCoverClass, SurroundElevation, TerrainRecord, WorldCoverClassCode } from '../types';
import { SURROUND_NODATA } from '../elevation';
import { lngLatToUnit } from '../geo';

// Any surround cell at or below this is treated as "no data" (see sampleSurround).
// Well below every real US land elevation, well above the -9999 nodata sentinel.
const NODATA_FLOOR = SURROUND_NODATA + 1000;

export const RESORT_DEM_PROTOCOL = 'resort-dem';
export const RESORT_COVER_PROTOCOL = 'resort-cover';
export const RESORT_SLOPE_PROTOCOL = 'resort-slope';
export const RESORT_ASPECT_PROTOCOL = 'resort-aspect';

let active: TerrainRecord | null = null;
let registered = false;
const tileCache = new Map<string, Promise<ArrayBuffer>>();
// Large enough to hold the whole warmed diorama tile set (see warmResortTiles)
// so preloaded tiles are never evicted before the camera reaches them.
const CACHE_MAX = 2048;
type TileKind = 'dem' | 'cover' | 'slope' | 'aspect';
const renderQueue: { kind: TileKind; url: string; resolve: (data: ArrayBuffer) => void; reject: (error: unknown) => void }[] = [];
let activeRenders = 0;
// Serial by default so on-demand renders never stutter interactive play; the
// warm-up preload temporarily raises this (setRenderConcurrency) for throughput,
// then restores it once the resort is revealed.
let maxConcurrentRenders = 1;
export function setRenderConcurrency(n: number): void {
  maxConcurrentRenders = Math.max(1, n);
  pumpRenderQueue();
}

// Cumulative counters for the preload progress/readiness gate. `completed` only
// ever grows; `pending` is the live queue depth. getResortRenderStats() reads
// them so the veil can wait until the on-demand queue has fully drained.
let tilesCompleted = 0;
export function getResortRenderStats(): { pending: number; completed: number } {
  return { pending: renderQueue.length + activeRenders, completed: tilesCompleted };
}

export function setActiveResortTerrain(record: TerrainRecord | null): void {
  if (active?.key !== record?.key) tileCache.clear();
  active = record;
}

export function activeResortTerrain(): TerrainRecord | null {
  return active;
}

/** Drop cached ground-cover tiles after an in-place edit to the same package
 *  (e.g. a lift clearing a corridor), so the raster fallback re-renders from the
 *  mutated grid. Leaves dem/slope/aspect tiles intact. */
export function clearResortCoverCache(): void {
  for (const key of [...tileCache.keys()]) {
    if (key.startsWith('cover:')) tileCache.delete(key);
  }
}

function parse(url: string): { key: string; z: number; x: number; y: number } {
  const m = url.match(/^[-a-z]+:\/\/([^/]+)\/(\d+)\/(\d+)\/(\d+)/);
  if (!m) throw new Error(`Invalid local resort tile URL: ${url}`);
  return { key: decodeURIComponent(m[1]), z: Number(m[2]), x: Number(m[3]), y: Number(m[4]) };
}

function pixelLngLat(z: number, x: number, y: number, px: number, py: number): [number, number] {
  const n = 2 ** z;
  const xf = (x + (px + 0.5) / 256) / n;
  const yf = (y + (py + 0.5) / 256) / n;
  const lng = xf * 360 - 180;
  const lat = (Math.atan(Math.sinh(Math.PI * (1 - 2 * yf))) * 180) / Math.PI;
  return [lng, lat];
}

function tileAxes(z: number, x: number, y: number): { lng: number[]; lat: number[] } {
  const lng = new Array<number>(258);
  const lat = new Array<number>(258);
  for (let px = -1; px <= 256; px++) lng[px + 1] = pixelLngLat(z, x, y, px, 0)[0];
  for (let py = -1; py <= 256; py++) lat[py + 1] = pixelLngLat(z, x, y, 0, py)[1];
  return { lng, lat };
}

function sampleGrid(record: TerrainRecord, lng: number, lat: number): number | null {
  const b = record.bounds;
  if (!b || lng < b.west || lng > b.east || lat < b.south || lat > b.north) return null;
  const n = record.sampleGridSize;
  const [u, v] = lngLatToUnit(lng, lat, b);
  const x = u * (n - 1);
  const y = v * (n - 1);
  const x0 = Math.floor(x), y0 = Math.floor(y);
  const x1 = Math.min(n - 1, x0 + 1), y1 = Math.min(n - 1, y0 + 1);
  const tx = x - x0, ty = y - y0;
  const a = record.sampleHeights[y0 * n + x0];
  const c = record.sampleHeights[y0 * n + x1];
  const d = record.sampleHeights[y1 * n + x0];
  const e = record.sampleHeights[y1 * n + x1];
  return (a * (1 - tx) + c * tx) * (1 - ty) + (d * (1 - tx) + e * tx) * ty;
}

// Fraction of the core's width/height, in from each edge, over which the
// high-res core cross-fades into the coarse surround — hides the resolution
// change so the property line reads as a smooth handoff, not a seam.
const FEATHER_FRAC = 0.08;

/** Bilinear-sample the coarse offline surround ring. Null outside its extent
 *  or where the source had no data (both meaning "let the caller fall back"). */
function sampleSurround(surround: SurroundElevation, lng: number, lat: number): number | null {
  const b = surround.bounds;
  if (lng < b.west || lng > b.east || lat < b.south || lat > b.north) return null;
  const { width: w, height: h, heights } = surround;
  const [u, v] = lngLatToUnit(lng, lat, b);
  const x = u * (w - 1);
  const y = v * (h - 1);
  const x0 = Math.floor(x), y0 = Math.floor(y);
  const x1 = Math.min(w - 1, x0 + 1), y1 = Math.min(h - 1, y0 + 1);
  const tx = x - x0, ty = y - y0;
  const a = heights[y0 * w + x0];
  const c = heights[y0 * w + x1];
  const d = heights[y1 * w + x0];
  const e = heights[y1 * w + x1];
  // Any nodata corner poisons the interpolation — treat the whole sample as
  // absent rather than averaging a -9999 sentinel into a false cliff. Use a
  // generous floor, not exact -9999: the service's bilinear resampling can
  // smear values toward the sentinel near coverage edges, and no US land sits
  // anywhere near this low (Death Valley bottoms out at ~-86 m).
  if (a <= NODATA_FLOOR || c <= NODATA_FLOOR || d <= NODATA_FLOOR || e <= NODATA_FLOOR) return null;
  return (a * (1 - tx) + c * tx) * (1 - ty) + (d * (1 - tx) + e * tx) * ty;
}

/** 1 deep inside the core, easing to 0 at its edge over the feather band. */
function coreEdgeWeight(record: TerrainRecord, lng: number, lat: number): number {
  const b = record.bounds!;
  const dx = Math.min(lng - b.west, b.east - lng) / ((b.east - b.west) * FEATHER_FRAC);
  const dy = Math.min(lat - b.south, b.north - lat) / ((b.north - b.south) * FEATHER_FRAC);
  const t = Math.max(0, Math.min(1, Math.min(dx, dy)));
  return t * t * (3 - 2 * t); // smoothstep
}

/**
 * Elevation for the 3D mesh: the high-res core inside the property line, the
 * coarse offline surround outside it, cross-faded across the feather band so
 * the boundary isn't a visible seam. Falls back to core-only when a package
 * has no surround. Used only for the DEM tiles — the slope/aspect/cover
 * overlays stay clamped to the core, since they only describe the property.
 */
function sampleElevation(record: TerrainRecord, lng: number, lat: number): number | null {
  const core = sampleGrid(record, lng, lat);
  const surround = record.surround;
  if (!surround) return core;
  const sur = sampleSurround(surround, lng, lat);
  if (core == null) return sur;
  if (sur == null) return core;
  const w = coreEdgeWeight(record, lng, lat);
  return core * w + sur * (1 - w);
}

function sampleCoverForRecord(record: TerrainRecord, lng: number, lat: number): CoverClassCode | null {
  const grid = record.coverGrid;
  if (!grid) return null;
  const b = grid.bounds;
  if (lng < b.west || lng > b.east || lat < b.south || lat > b.north) return null;
  const [u, v] = lngLatToUnit(lng, lat, b);
  const c = Math.min(grid.width - 1, Math.max(0, Math.floor(u * grid.width)));
  const r = Math.min(grid.height - 1, Math.max(0, Math.floor(v * grid.height)));
  const code = grid.data[r * grid.width + c] as CoverClassCode;
  return code === 255 ? null : code;
}

export function sampleLocalCoverAt(lng: number, lat: number): CoverClassCode | null {
  return active ? sampleCoverForRecord(active, lng, lat) : null;
}

export function sampleLocalTerrainAt(lng: number, lat: number): { elevation: number; slopeDeg: number; aspectDeg: number } | null {
  const record = active;
  const b = record?.bounds;
  if (!record || !b) return null;
  const elevation = sampleGrid(record, lng, lat);
  if (elevation == null) return null;
  const dx = (b.east - b.west) / Math.max(1, record.sampleGridSize - 1);
  const dy = (b.north - b.south) / Math.max(1, record.sampleGridSize - 1);
  const metersX = dx * 111320 * Math.cos((lat * Math.PI) / 180);
  const metersY = dy * 111320;
  const dzdx = ((sampleGrid(record, lng + dx, lat) ?? elevation) - (sampleGrid(record, lng - dx, lat) ?? elevation)) / Math.max(1, 2 * metersX);
  const dzdy = ((sampleGrid(record, lng, lat - dy) ?? elevation) - (sampleGrid(record, lng, lat + dy) ?? elevation)) / Math.max(1, 2 * metersY);
  return {
    elevation,
    slopeDeg: Math.atan(Math.hypot(dzdx, dzdy)) * 180 / Math.PI,
    aspectDeg: (Math.atan2(-dzdx, dzdy) * 180 / Math.PI + 360) % 360,
  };
}

export const WORLD_COVER_LABELS: Record<number, string> = {
  1: 'Forest', 2: 'Alpine', 3: 'Grassland', 4: 'Water',
  10: 'Tree cover', 20: 'Shrubland', 30: 'Grassland', 40: 'Cropland', 50: 'Built-up',
  60: 'Bare / sparse', 70: 'Snow and ice', 80: 'Permanent water', 90: 'Herbaceous wetland',
  95: 'Mangroves', 100: 'Moss and lichen', 255: 'No data',
};

export const WORLD_COVER_CLASSES: Record<WorldCoverClassCode, LandCoverClass> = {
  10: 'tree-cover', 20: 'shrubland', 30: 'grassland', 40: 'cropland', 50: 'built-up',
  60: 'bare-sparse', 70: 'snow-ice', 80: 'permanent-water', 90: 'herbaceous-wetland',
  95: 'mangroves', 100: 'moss-lichen', 255: 'nodata',
};

const COVER_RGBA: Record<number, [number, number, number, number]> = {
  1: [82, 105, 82, 205], 2: [215, 216, 207, 150], 3: [177, 183, 145, 150], 4: [83, 142, 174, 185],
  10: [47, 81, 53, 205], 20: [113, 128, 90, 190], 30: [197, 200, 153, 120],
  40: [202, 184, 139, 120], 50: [154, 135, 125, 160], 60: [157, 151, 140, 140],
  70: [237, 240, 238, 150], 80: [83, 142, 174, 185], 90: [79, 145, 137, 170],
  95: [39, 105, 69, 205], 100: [192, 193, 153, 120], 255: [0, 0, 0, 0],
};

async function canvasPng(write: (data: Uint8ClampedArray) => void): Promise<ArrayBuffer> {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = 256;
  const ctx = canvas.getContext('2d')!;
  const image = ctx.createImageData(256, 256);
  write(image.data);
  ctx.putImageData(image, 0, 0);
  const blob = await new Promise<Blob>((resolve, reject) =>
    canvas.toBlob((b) => b ? resolve(b) : reject(new Error('Unable to encode local terrain tile')), 'image/png')
  );
  return blob.arrayBuffer();
}

function analysisColor(kind: 'slope' | 'aspect', slope: number, aspect: number): [number, number, number, number] {
  if (kind === 'slope') {
    if (slope < 6) return [0, 0, 0, 0];
    if (slope < 16) return [67, 160, 71, 150];
    if (slope < 24) return [30, 136, 229, 150];
    if (slope < 37) return [33, 33, 33, 150];
    return [211, 47, 47, 150];
  }
  if (slope < 4) return [0, 0, 0, 0];
  const colors: [number, number, number][] = [[66,133,244],[45,165,190],[46,170,100],[140,190,60],[245,214,60],[240,150,50],[220,70,70],[150,90,200]];
  const c = colors[Math.round(aspect / 45) % 8];
  return [c[0], c[1], c[2], 150];
}

/** Rasterize one resort tile straight from an explicit record — no dependency
 *  on the module-global `active`, so ingest/warm-up can call it for any package. */
async function renderResortTile(record: TerrainRecord, kind: TileKind, z: number, x: number, y: number): Promise<ArrayBuffer> {
  const axes = tileAxes(z, x, y);
  return canvasPng((out) => {
    for (let py = 0; py < 256; py++) for (let px = 0; px < 256; px++) {
      const lng = axes.lng[px + 1];
      const lat = axes.lat[py + 1];
      const i = (py * 256 + px) * 4;
      if (kind === 'dem') {
        const elevation = sampleElevation(record, lng, lat);
        const encoded = Math.max(0, Math.min(65535.996, (elevation ?? 0) + 32768));
        out[i] = Math.floor(encoded / 256);
        out[i + 1] = Math.floor(encoded) % 256;
        out[i + 2] = Math.floor((encoded - Math.floor(encoded)) * 256);
        out[i + 3] = elevation == null ? 0 : 255;
      } else if (kind === 'cover') {
        const code = sampleCoverForRecord(record, lng, lat) ?? 255;
        const rgba = COVER_RGBA[code] ?? COVER_RGBA[255];
        out[i] = rgba[0]; out[i + 1] = rgba[1]; out[i + 2] = rgba[2]; out[i + 3] = rgba[3];
      } else {
        const lngW = axes.lng[px];
        const lngE = axes.lng[px + 2];
        const latN = axes.lat[py];
        const latS = axes.lat[py + 2];
        const ewM = 2 * 156543.03392 * Math.cos((lat * Math.PI) / 180) / (2 ** z);
        const nsM = ewM;
        const dzdx = ((sampleGrid(record, lngE, lat) ?? 0) - (sampleGrid(record, lngW, lat) ?? 0)) / ewM;
        const dzdy = ((sampleGrid(record, lng, latS) ?? 0) - (sampleGrid(record, lng, latN) ?? 0)) / nsM;
        const slope = Math.atan(Math.hypot(dzdx, dzdy)) * 180 / Math.PI;
        const aspect = (Math.atan2(-dzdx, dzdy) * 180 / Math.PI + 360) % 360;
        const rgba = analysisColor(kind, slope, aspect);
        out[i] = rgba[0]; out[i + 1] = rgba[1]; out[i + 2] = rgba[2]; out[i + 3] = rgba[3];
      }
    }
  });
}

async function renderTile(kind: TileKind, url: string): Promise<ArrayBuffer> {
  const p = parse(url);
  const record = active;
  if (!record || record.key !== p.key) throw new Error(`Local resort package is not loaded: ${p.key}`);
  return renderResortTile(record, kind, p.z, p.x, p.y);
}

function pumpRenderQueue(): void {
  while (activeRenders < maxConcurrentRenders && renderQueue.length) {
    const task = renderQueue.shift()!;
    activeRenders++;
    // Yield before CPU-heavy rasterization so controls/camera updates paint
    // immediately even when MapLibre requests a burst of terrain tiles.
    window.setTimeout(() => {
      void renderTile(task.kind, task.url)
        .then(task.resolve, task.reject)
        .finally(() => {
          activeRenders--;
          tilesCompleted++;
          pumpRenderQueue();
        });
    }, 8);
  }
}

function cached(kind: TileKind, url: string): Promise<ArrayBuffer> {
  const key = `${kind}:${url}`;
  let promise = tileCache.get(key);
  if (!promise) {
    promise = new Promise<ArrayBuffer>((resolve, reject) => {
      renderQueue.push({ kind, url, resolve, reject });
      pumpRenderQueue();
    });
    tileCache.set(key, promise);
    if (tileCache.size > CACHE_MAX) tileCache.delete(tileCache.keys().next().value!);
  }
  return promise;
}

export function registerResortProtocols(): void {
  if (registered) return;
  registered = true;
  // MapLibre transfers protocol buffers to workers; clone cached bytes so a
  // later request never receives an already-detached ArrayBuffer.
  maplibregl.addProtocol(RESORT_DEM_PROTOCOL, (params) => cached('dem', params.url).then((data) => ({ data: data.slice(0) })));
  maplibregl.addProtocol(RESORT_COVER_PROTOCOL, (params) => cached('cover', params.url).then((data) => ({ data: data.slice(0) })));
  maplibregl.addProtocol(RESORT_SLOPE_PROTOCOL, (params) => cached('slope', params.url).then((data) => ({ data: data.slice(0) })));
  maplibregl.addProtocol(RESORT_ASPECT_PROTOCOL, (params) => cached('aspect', params.url).then((data) => ({ data: data.slice(0) })));
}

export function localTileBounds(record: TerrainRecord): [number, number, number, number] | undefined {
  const b = record.bounds;
  return b ? [b.west, b.south, b.east, b.north] : undefined;
}

/**
 * Tile bounds for the 3D terrain source: the surround extent when the package
 * carries an offline buffer, otherwise the core. This is what lets MapLibre
 * request DEM tiles out past the property line so neighbouring terrain renders
 * instead of a void. Falls back to core bounds for buffer-less packages.
 */
export function resortDemBounds(record: TerrainRecord): [number, number, number, number] | undefined {
  const b = record.surround?.bounds;
  return b ? [b.west, b.south, b.east, b.north] : localTileBounds(record);
}

/**
 * Bounds to clamp the *diorama camera* to — the play box grown by `marginM`
 * (~1 km), clamped so it never exceeds the rendered surround/DEM extent. This
 * keeps the camera inside the clean near-ring and off the coarse 3 km far edge
 * (which looks janky) while still letting the player orbit all four sides of the
 * box. Distinct from `resortDemBounds`, which stays the wider *source* extent so
 * neighbouring relief still renders out to 3 km beyond where the camera can go.
 */
export function resortCameraBounds(
  record: TerrainRecord,
  marginM = 1000
): [number, number, number, number] | undefined {
  const b = record.bounds;
  if (!b) return resortDemBounds(record);
  const midLat = (b.north + b.south) / 2;
  const dLat = marginM / 111320;
  const dLng = marginM / (111320 * Math.max(0.05, Math.cos((midLat * Math.PI) / 180)));
  let west = b.west - dLng, south = b.south - dLat;
  let east = b.east + dLng, north = b.north + dLat;
  const ring = resortDemBounds(record); // [w, s, e, n]
  if (ring) {
    west = Math.max(west, ring[0]);
    south = Math.max(south, ring[1]);
    east = Math.min(east, ring[2]);
    north = Math.min(north, ring[3]);
  }
  return [west, south, east, north];
}

// ---------------------------------------------------------------------------
// Preload / warm-up: rasterize the whole reachable diorama tile set into the
// in-memory cache before the player joins, so gameplay opens fully drawn and
// panning within the (box + ~1 km) camera bounds never triggers a fresh render.
// ---------------------------------------------------------------------------

const PROTOCOL_FOR_KIND: Record<'dem' | 'cover', string> = {
  dem: RESORT_DEM_PROTOCOL,
  cover: RESORT_COVER_PROTOCOL,
};

function lngLatToTileXY(lng: number, lat: number, z: number): [number, number] {
  const n = 2 ** z;
  const x = Math.floor(((lng + 180) / 360) * n);
  const latRad = (lat * Math.PI) / 180;
  const y = Math.floor(((1 - Math.log(Math.tan(latRad) + 1 / Math.cos(latRad)) / Math.PI) / 2) * n);
  return [Math.max(0, Math.min(n - 1, x)), Math.max(0, Math.min(n - 1, y))];
}

/** Slippy tiles covering `[w,s,e,n]` across the inclusive zoom band. */
function tilesForBounds(bounds: [number, number, number, number], zMin: number, zMax: number): { z: number; x: number; y: number }[] {
  const [w, s, e, n] = bounds;
  const out: { z: number; x: number; y: number }[] = [];
  for (let z = zMin; z <= zMax; z++) {
    const [x0, y0] = lngLatToTileXY(w, n, z); // NW → min x, min y
    const [x1, y1] = lngLatToTileXY(e, s, z); // SE → max x, max y
    for (let x = Math.min(x0, x1); x <= Math.max(x0, x1); x++)
      for (let y = Math.min(y0, y1); y <= Math.max(y0, y1); y++) out.push({ z, x, y });
  }
  return out;
}

/** The tiles worth preloading: DEM across the rendered surround/ring (drives the
 *  mesh + hillshade) and cover across the play box, over the zoom band the
 *  camera actually uses (a coarse fit level up to the source maxzoom 15). The
 *  slope/aspect analysis overlays are off by default, so they stay on-demand. */
export function resortWarmTileKeys(record: TerrainRecord): { kind: 'dem' | 'cover'; z: number; x: number; y: number }[] {
  const ring = resortDemBounds(record);
  const core = localTileBounds(record);
  const keys: { kind: 'dem' | 'cover'; z: number; x: number; y: number }[] = [];
  const zMax = 15;
  const bandFor = (b: [number, number, number, number]) => {
    const widthDeg = Math.max(1e-6, b[2] - b[0]);
    // Coarsest level where the extent still spans ~1 tile, floored so we also
    // warm the low-zoom tiles the terrain mesh pulls for distant relief.
    const zFit = Math.floor(Math.log2(360 / widthDeg));
    return Math.max(11, Math.min(zMax, zFit));
  };
  if (ring) for (const t of tilesForBounds(ring, bandFor(ring), zMax)) keys.push({ kind: 'dem', ...t });
  if (core) for (const t of tilesForBounds(core, bandFor(core), zMax)) keys.push({ kind: 'cover', ...t });
  return keys;
}

/**
 * Pre-rasterize the warm tile set into `tileCache`, reporting progress. Raises
 * render concurrency for throughput; the caller restores it (setRenderConcurrency(1))
 * once the resort is revealed. Best-effort — a failed tile is skipped and simply
 * renders on demand later. Honors an AbortSignal so leaving the resort cancels it.
 */
export async function warmResortTiles(
  record: TerrainRecord,
  onProgress?: (completed: number, total: number) => void,
  signal?: AbortSignal
): Promise<void> {
  const keys = resortWarmTileKeys(record);
  const total = keys.length;
  let done = 0;
  onProgress?.(0, total);
  if (!total) return;
  setRenderConcurrency(4);
  const key = encodeURIComponent(record.key);
  let idx = 0;
  const worker = async () => {
    while (idx < keys.length) {
      if (signal?.aborted) return;
      const k = keys[idx++];
      const url = `${PROTOCOL_FOR_KIND[k.kind]}://${key}/${k.z}/${k.x}/${k.y}`;
      try {
        await cached(k.kind, url);
      } catch {
        // Skip — the on-demand protocol path can retry this tile if the camera
        // ever requests it.
      }
      done++;
      onProgress?.(done, total);
    }
  };
  await Promise.all([worker(), worker(), worker(), worker()]);
}
