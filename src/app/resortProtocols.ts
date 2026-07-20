import maplibregl from 'maplibre-gl';
import type { CoverClassCode, LandCoverClass, TerrainRecord, WorldCoverClassCode } from '../types';
import { lngLatToUnit } from '../geo';

export const RESORT_DEM_PROTOCOL = 'resort-dem';
export const RESORT_COVER_PROTOCOL = 'resort-cover';
export const RESORT_SLOPE_PROTOCOL = 'resort-slope';
export const RESORT_ASPECT_PROTOCOL = 'resort-aspect';

let active: TerrainRecord | null = null;
let registered = false;
const tileCache = new Map<string, Promise<ArrayBuffer>>();
const CACHE_MAX = 192;
type TileKind = 'dem' | 'cover' | 'slope' | 'aspect';
const renderQueue: { kind: TileKind; url: string; resolve: (data: ArrayBuffer) => void; reject: (error: unknown) => void }[] = [];
let activeRenders = 0;
const MAX_CONCURRENT_RENDERS = 1;

export function setActiveResortTerrain(record: TerrainRecord | null): void {
  if (active?.key !== record?.key) tileCache.clear();
  active = record;
}

export function activeResortTerrain(): TerrainRecord | null {
  return active;
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

export function sampleLocalCoverAt(lng: number, lat: number): CoverClassCode | null {
  const grid = active?.coverGrid;
  if (!grid) return null;
  const b = grid.bounds;
  if (lng < b.west || lng > b.east || lat < b.south || lat > b.north) return null;
  const [u, v] = lngLatToUnit(lng, lat, b);
  const c = Math.min(grid.width - 1, Math.max(0, Math.floor(u * grid.width)));
  const r = Math.min(grid.height - 1, Math.max(0, Math.floor(v * grid.height)));
  const code = grid.data[r * grid.width + c] as CoverClassCode;
  return code === 255 ? null : code;
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

async function renderTile(kind: TileKind, url: string): Promise<ArrayBuffer> {
  const p = parse(url);
  const record = active;
  if (!record || record.key !== p.key) throw new Error(`Local resort package is not loaded: ${p.key}`);
  const axes = tileAxes(p.z, p.x, p.y);
  return canvasPng((out) => {
    for (let py = 0; py < 256; py++) for (let px = 0; px < 256; px++) {
      const lng = axes.lng[px + 1];
      const lat = axes.lat[py + 1];
      const i = (py * 256 + px) * 4;
      if (kind === 'dem') {
        const elevation = sampleGrid(record, lng, lat);
        const encoded = Math.max(0, Math.min(65535.996, (elevation ?? 0) + 32768));
        out[i] = Math.floor(encoded / 256);
        out[i + 1] = Math.floor(encoded) % 256;
        out[i + 2] = Math.floor((encoded - Math.floor(encoded)) * 256);
        out[i + 3] = elevation == null ? 0 : 255;
      } else if (kind === 'cover') {
        const code = sampleLocalCoverAt(lng, lat) ?? 255;
        const rgba = COVER_RGBA[code] ?? COVER_RGBA[255];
        out[i] = rgba[0]; out[i + 1] = rgba[1]; out[i + 2] = rgba[2]; out[i + 3] = rgba[3];
      } else {
        const lngW = axes.lng[px];
        const lngE = axes.lng[px + 2];
        const latN = axes.lat[py];
        const latS = axes.lat[py + 2];
        const ewM = 2 * 156543.03392 * Math.cos((lat * Math.PI) / 180) / (2 ** p.z);
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

function pumpRenderQueue(): void {
  while (activeRenders < MAX_CONCURRENT_RENDERS && renderQueue.length) {
    const task = renderQueue.shift()!;
    activeRenders++;
    // Yield before CPU-heavy rasterization so controls/camera updates paint
    // immediately even when MapLibre requests a burst of terrain tiles.
    window.setTimeout(() => {
      void renderTile(task.kind, task.url)
        .then(task.resolve, task.reject)
        .finally(() => {
          activeRenders--;
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
