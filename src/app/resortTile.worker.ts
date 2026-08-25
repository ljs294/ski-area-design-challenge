/// <reference lib="webworker" />
import { renderResortTilePixels, type RasterTerrainRecord, type ResortTileKind } from './resortTileEngine';

type Request = { type: 'init'; generation: number; record: RasterTerrainRecord } |
  { type: 'render'; id: number; generation: number; kind: ResortTileKind; z: number; x: number; y: number };
const scope: DedicatedWorkerGlobalScope = self as unknown as DedicatedWorkerGlobalScope;
let generation = 0;
let record: RasterTerrainRecord | null = null;

scope.onmessage = (event: MessageEvent<Request>) => {
  const request = event.data;
  if (request.type === 'init') {
    generation = request.generation;
    record = request.record;
    return;
  }
  if (!record || request.generation !== generation) return;
  void (async () => {
    try {
      const pixels = renderResortTilePixels(record!, request.kind, request.z, request.x, request.y);
      const canvas = new OffscreenCanvas(256, 256);
      const context = canvas.getContext('2d');
      if (!context) throw new Error('Offscreen 2D canvas is unavailable.');
      const image = new ImageData(256, 256);
      image.data.set(pixels);
      context.putImageData(image, 0, 0);
      const blob = await canvas.convertToBlob({ type: 'image/png' });
      const data = await blob.arrayBuffer();
      scope.postMessage({ id: request.id, generation, ok: true, data }, [data]);
    } catch (error) {
      scope.postMessage({ id: request.id, generation, ok: false,
        error: error instanceof Error ? error.message : String(error) });
    }
  })();
};
