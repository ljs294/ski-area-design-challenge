import type { LatLonBounds } from '../elevation';
import type { SavedTrailPart } from '../types';

export interface TerrainGradeRequest {
  id: number;
  heights: Float32Array;
  gridSize: number;
  bounds: LatLonBounds;
  parts: SavedTrailPart[];
  brushWidthM: number;
  baseElevationChecksum: string;
  trailGeometryKey: string;
  contourGridSize?: number;
  contourIntervalM?: number;
}

export type TerrainGradeResponse =
  | { id: number; ok: true; patchIndices: Uint32Array; patchHeights: Float32Array;
      contourSegments: Float32Array; contourGridSize: number; contourIntervalM: number;
      gradedElevations: number[][]; baseElevationChecksum: string; trailGeometryKey: string }
  | { id: number; ok: false; error: string };

/** Stable review identity used to keep a completed worker response from being
 * applied to a different footprint or brush width. */
export function terrainGradeGeometryKey(parts: SavedTrailPart[], brushWidthM: number): string {
  const value = JSON.stringify([brushWidthM, parts.map((part) => [
    part.polygon, part.centerline, part.centerlineElevM,
  ])]);
  let hash = 0x811c9dc5;
  for (let i = 0; i < value.length; i++) {
    hash ^= value.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return `grade-${hash.toString(16).padStart(8, '0')}`;
}
