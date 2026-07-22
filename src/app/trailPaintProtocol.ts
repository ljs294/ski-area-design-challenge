import type { PaintMode } from './trailPaintEngine';
import type { SavedTrailPart } from '../types';

export type TrailPaintRequestPayload =
  | { type: 'init'; origin: [number, number]; brushWidthM: number }
  | { type: 'stroke'; mode: PaintMode; coordinates: Float64Array }
  | { type: 'undo' | 'clear' | 'finish' };
export type TrailPaintRequest = TrailPaintRequestPayload & { id: number };

export type TrailPaintResponse =
  | { id: number; ok: true; type: 'ready' }
  | { id: number; ok: true; type: 'preview'; polygons: [number, number][][][]; areaM2: number; canUndo: boolean }
  | { id: number; ok: true; type: 'analysis'; parts: SavedTrailPart[]; areaM2: number }
  | { id: number; ok: false; error: string };
