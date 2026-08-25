import maplibregl from 'maplibre-gl';
import type { SnowGrid } from '../types/snow';
import type { SnowDisplayMode } from './snowStyle';
import { renderSnowTilePixels } from './snowTileEngine';
import { SnowTileWorkerClient } from './snowTileWorkerClient';
import { renderProfileFor, type RenderQuality } from './renderProfile';

export const RESORT_SNOW_PROTOCOL = 'resort-snow';

let active: SnowGrid | null = null;
let revision = 0;
let registered = false;
const cache = new Map<string, Promise<ArrayBuffer>>();
const workerClient = new SnowTileWorkerClient();
let activeQuality: RenderQuality = 'standard';
const renderQueue: { grid: SnowGrid; url: string; resolve(data: ArrayBuffer): void;
  reject(error: unknown): void }[] = [];
let rendering = false;

export function setActiveSnowGrid(grid: SnowGrid | null): void {
  if (active === grid) return;
  active = grid;
  revision += 1;
  cache.clear();
  workerClient.configure(grid, activeQuality);
}

export function setSnowRenderQuality(quality: RenderQuality): void {
  if (activeQuality === quality) return;
  activeQuality = quality;
  revision += 1;
  cache.clear();
  workerClient.configure(active, quality);
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
  if (workerClient.supported) return workerClient.render(z, x, y, mode);
  return canvasPng((data) => data.set(renderSnowTilePixels(grid, z, x, y, mode)));
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
    promise.catch(() => { if (cache.get(url) === promise) cache.delete(url); });
    const maxEntries = Math.max(16, Math.floor(renderProfileFor(activeQuality).derivedCacheBytes / 65_536));
    if (cache.size > maxEntries) cache.delete(cache.keys().next().value!);
  } else {
    cache.delete(url);
    cache.set(url, promise);
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
