import maplibregl from 'maplibre-gl';
import { sampleSnowGrid } from '../snow';
import type { SnowGrid } from '../types/snow';
import { snowRgba, type SnowDisplayMode } from './snowStyle';

export const RESORT_SNOW_PROTOCOL = 'resort-snow';

let active: SnowGrid | null = null;
let revision = 0;
let registered = false;
const cache = new Map<string, Promise<ArrayBuffer>>();
const CACHE_MAX = 256;
const renderQueue: { grid: SnowGrid; url: string; resolve(data: ArrayBuffer): void;
  reject(error: unknown): void }[] = [];
let rendering = false;

export function setActiveSnowGrid(grid: SnowGrid | null): void {
  if (active === grid) return;
  active = grid;
  revision += 1;
  cache.clear();
}

export function snowProtocolUrl(mode: SnowDisplayMode): string {
  return `${RESORT_SNOW_PROTOCOL}://snow/{z}/{x}/{y}?mode=${mode}&rev=${revision}`;
}

function parse(url: string): { z: number; x: number; y: number; mode: SnowDisplayMode } {
  const match = url.match(/^resort-snow:\/\/snow\/(\d+)\/(\d+)\/(\d+)/);
  if (!match) throw new Error(`Invalid snow tile URL: ${url}`);
  const mode = new URL(url).searchParams.get('mode');
  return { z: Number(match[1]), x: Number(match[2]), y: Number(match[3]),
    mode: mode === 'conditions' ? 'conditions' : 'depth' };
}

function pixelLngLat(z: number, x: number, y: number, px: number, py: number): [number, number] {
  const n = 2 ** z;
  const xf = (x + (px + 0.5) / 256) / n;
  const yf = (y + (py + 0.5) / 256) / n;
  return [xf * 360 - 180, Math.atan(Math.sinh(Math.PI * (1 - 2 * yf))) * 180 / Math.PI];
}

async function canvasPng(write: (data: Uint8ClampedArray) => void): Promise<ArrayBuffer> {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = 256;
  const context = canvas.getContext('2d')!;
  const image = context.createImageData(256, 256);
  write(image.data);
  context.putImageData(image, 0, 0);
  const blob = await new Promise<Blob>((resolve, reject) => canvas.toBlob((value) =>
    value ? resolve(value) : reject(new Error('Unable to encode snow tile')), 'image/png'));
  return blob.arrayBuffer();
}

async function render(grid: SnowGrid, url: string): Promise<ArrayBuffer> {
  const { z, x, y, mode } = parse(url);
  return canvasPng((data) => {
    for (let py = 0; py < 256; py++) for (let px = 0; px < 256; px++) {
      const [lng, lat] = pixelLngLat(z, x, y, px, py);
      const sample = sampleSnowGrid(grid, lng, lat);
      const rgba = sample ? snowRgba(sample.depthM, sample.surface, mode) : [0, 0, 0, 0];
      const index = (py * 256 + px) * 4;
      data[index] = rgba[0]; data[index + 1] = rgba[1];
      data[index + 2] = rgba[2]; data[index + 3] = rgba[3];
    }
  });
}

function pumpRenderQueue(): void {
  if (rendering || renderQueue.length === 0) return;
  const task = renderQueue.shift()!;
  rendering = true;
  window.setTimeout(() => {
    void render(task.grid, task.url).then(task.resolve, task.reject).finally(() => {
      rendering = false;
      pumpRenderQueue();
    });
  }, 8);
}

function cached(url: string): Promise<ArrayBuffer> {
  let promise = cache.get(url);
  if (!promise) {
    const grid = active;
    if (!grid) return Promise.reject(new Error('The resort snow grid is not loaded.'));
    promise = new Promise<ArrayBuffer>((resolve, reject) => {
      renderQueue.push({ grid, url, resolve, reject });
      pumpRenderQueue();
    });
    cache.set(url, promise);
    if (cache.size > CACHE_MAX) cache.delete(cache.keys().next().value!);
  }
  return promise;
}

export function registerSnowProtocol(): void {
  if (registered) return;
  registered = true;
  maplibregl.addProtocol(RESORT_SNOW_PROTOCOL, (params) =>
    cached(params.url).then((data) => ({ data: data.slice(0) })));
}

export function refreshSnowSource(map: maplibregl.Map | null, mode: SnowDisplayMode): void {
  const source = map?.getSource('snow') as { setTiles?(tiles: string[]): void } | undefined;
  source?.setTiles?.([snowProtocolUrl(mode)]);
  if (map?.getLayer('snow')) map.setPaintProperty('snow', 'raster-resampling',
    mode === 'conditions' ? 'nearest' : 'linear');
}
