import type { EarthworkTerrainPatch } from '../earthwork';
import type { PondEarthworkDesign } from '../pondEarthwork';
import type { LatLonBounds } from '../types/geo';

export interface PondEarthworkRequest {
  type: 'pond-earthwork';
  jobId: number;
  terrainRevision: number;
  profileRevision: number;
  heights: Float32Array;
  gridSize: number;
  bounds: LatLonBounds;
  contourGridSize: number;
  contourIntervalM: number;
  baseElevationChecksum: string;
  boundary: [number, number][];
  topElevationM: number;
  excavationDepthM: number;
  poolAreaM2: number;
}

export type PondEarthworkResponse =
  | { type: 'result'; jobId: number; terrainRevision: number; profileRevision: number;
      design: PondEarthworkDesign; grade: EarthworkTerrainPatch }
  | { type: 'error'; jobId: number; terrainRevision: number; profileRevision: number; error: string };
