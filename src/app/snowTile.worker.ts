/// <reference lib="webworker" />
import type { SnowGrid } from '../types/snow';
import type { SnowDisplayMode } from './snowStyle';
import { renderSnowTilePixels } from './snowTileEngine';

type Request =
  | { type: 'init'; generation: number; grid: SnowGrid }
  | { type: 'render'; generation: number; taskId: number; z: number; x: number; y: number;
      mode: SnowDisplayMode };
type Response = { type: 'result'; generation: number; taskId: number; data: ArrayBuffer }
  | { type: 'error'; generation: number; taskId: number; error: string };

let generation = 0;
let grid: SnowGrid | null = null;
const scope = self as DedicatedWorkerGlobalScope;
scope.onmessage = async (event: MessageEvent<Request>) => {
  const request = event.data;
  if (request.type === 'init') {
    generation = request.generation;
    grid = request.grid;
    return;
  }
  if (!grid || request.generation !== generation) return;
  try {
    const pixels = renderSnowTilePixels(grid, request.z, request.x, request.y, request.mode);
    const canvas = new OffscreenCanvas(256, 256);
    const context = canvas.getContext('2d');
    if (!context) throw new Error('Snow tile canvas is unavailable');
    const image = new ImageData(256, 256);
    image.data.set(pixels);
    context.putImageData(image, 0, 0);
    const data = await (await canvas.convertToBlob({ type: 'image/png' })).arrayBuffer();
    const response: Response = { type: 'result', generation, taskId: request.taskId, data };
    scope.postMessage(response, [data]);
  } catch (error) {
    const response: Response = { type: 'error', generation, taskId: request.taskId,
      error: error instanceof Error ? error.message : 'Snow tile rendering failed' };
    scope.postMessage(response);
  }
};
