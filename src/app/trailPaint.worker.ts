/// <reference lib="webworker" />
import { TrailPaintEngine } from './trailPaintEngine';
import type { TrailPaintRequest, TrailPaintResponse } from './trailPaintProtocol';

let engine: TrailPaintEngine | null = null;

self.onmessage = (event: MessageEvent<TrailPaintRequest>) => {
  const req = event.data;
  try {
    if (req.type === 'init') {
      engine = new TrailPaintEngine(req.origin, req.brushWidthM);
      post({ id: req.id, ok: true, type: 'ready' });
      return;
    }
    if (!engine) throw new Error('Trail painter is not initialized.');
    if (req.type === 'finish') {
      const result = engine.analyze();
      post({ id: req.id, ok: true, type: 'analysis', parts: result.parts, areaM2: result.areaM2 });
      return;
    }
    const result = req.type === 'stroke'
      ? engine.apply(toPath(req.coordinates), req.mode)
      : req.type === 'undo' ? engine.undo() : engine.clear();
    post({ id: req.id, ok: true, type: 'preview', polygons: result.polygons,
      areaM2: result.areaM2, canUndo: engine.canUndo() });
  } catch (error) {
    post({ id: req.id, ok: false, error: error instanceof Error ? error.message : 'Trail analysis failed.' });
  }
};

function toPath(values: Float64Array): [number, number][] {
  const points: [number, number][] = [];
  for (let i = 0; i + 1 < values.length; i += 2) points.push([values[i], values[i + 1]]);
  return points;
}

function post(message: TrailPaintResponse) { self.postMessage(message); }
