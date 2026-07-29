import type { LatLonBounds } from '../elevation';
import type { SavedTrailPart } from '../types';

/** How far grading may stray from the drawn feature. Trails must stay inside
 * the painted footprint — widening the run is the player's lever for making a
 * steep traverse buildable. Roads have no painted shoulder, so they alone get a
 * bounded envelope outside the pavement. */
export type GradeEnvelope = 'footprint' | 'expand';

export interface TerrainGradePolicy {
  envelope?: GradeEnvelope;
  /** Envelope multiplier for 'expand' features only. */
  maxWidthMultiplier?: number;
}

export interface TerrainGradeRequest extends TerrainGradePolicy {
  id: number;
  kind?: 'trail' | 'road';
  heights: Float32Array;
  gridSize: number;
  bounds: LatLonBounds;
  parts: SavedTrailPart[];
  brushWidthM: number;
  /** Existing trail polygons that must remain bit-for-bit untouched. */
  protectedPolygons?: [number, number][][][];
  baseElevationChecksum: string;
  trailGeometryKey: string;
  contourGridSize?: number;
  contourIntervalM?: number;
}

export type TerrainGradeResponse =
  | { id: number; ok: true; patchIndices: Uint32Array; patchHeights: Float32Array;
      contourSegments: Float32Array; editedContourSegments: Float32Array;
      contourGridSize: number; contourIntervalM: number;
      gradedElevations: number[][]; expandedPolygons: [number, number][][][];
      disturbancePolygons: [number, number][][][];
      cutM3: number; fillM3: number; balanceM3: number;
      maxGroundCrossSlopePct: number; maxFaceSlopePct: number;
      ungradedLengthM: number; maxDisturbedWidthM: number;
      infeasibleLines: [number, number][][];
      baseElevationChecksum: string; trailGeometryKey: string }
  | { id: number; ok: false; error: string };

/** Stable review identity used to keep a completed worker response from being
 * applied to a different footprint, brush width, or grading policy. */
export function terrainGradeGeometryKey(
  parts: SavedTrailPart[],
  brushWidthM: number,
  protectedPolygons: [number, number][][][] = [],
  kind: 'trail' | 'road' = 'trail',
  policy: TerrainGradePolicy = {}
): string {
  const value = JSON.stringify([kind, brushWidthM, parts.map((part) => [
    part.polygon, part.centerline, part.centerlineElevM,
  ]), protectedPolygons, policy]);
  let hash = 0x811c9dc5;
  for (let i = 0; i < value.length; i++) {
    hash ^= value.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return `grade-${hash.toString(16).padStart(8, '0')}`;
}
