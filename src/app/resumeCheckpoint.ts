import type { GameSave } from '../types';

export interface ResumeCamera {
  center: [number, number];
  zoom: number;
  bearing: number;
  pitch: number;
}

const finiteOr = (value: unknown, fallback: number): number =>
  typeof value === 'number' && Number.isFinite(value) ? value : fallback;

const clamp = (value: number, min: number, max: number): number =>
  Math.min(max, Math.max(min, value));

/** Hydrate camera data defensively without changing any valid saved pose. */
export function resumeCameraOf(
  save: GameSave | null | undefined,
  fallback: ResumeCamera
): ResumeCamera {
  if (!save) return fallback;
  return {
    center: [
      clamp(finiteOr(save.center?.[0], fallback.center[0]), -180, 180),
      clamp(finiteOr(save.center?.[1], fallback.center[1]), -85.051129, 85.051129),
    ],
    zoom: clamp(finiteOr(save.zoom, fallback.zoom), 0, 24),
    bearing: clamp(finiteOr(save.bearing, fallback.bearing), -360, 360),
    pitch: clamp(finiteOr(save.pitch, fallback.pitch), 0, 75),
  };
}

/**
 * Update only the resume checkpoint. Resort design arrays deliberately come
 * from the last explicitly persisted save rather than the live editor state.
 */
export function withResumeCheckpoint(
  persisted: GameSave,
  camera: ResumeCamera,
  is3D: boolean,
  lastPlayedAt = new Date().toISOString()
): GameSave {
  return {
    ...persisted,
    center: [...camera.center],
    zoom: camera.zoom,
    bearing: camera.bearing,
    pitch: camera.pitch,
    is3D,
    lastPlayedAt,
  };
}
