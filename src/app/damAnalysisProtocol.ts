import type { LatLonBounds } from '../types/geo';
import type { DamAnalysisResult } from '../damAnalysis';
import type { WaterLineFeature } from '../types';
import type { EarthworkTerrainPatch } from '../earthwork';

export interface DamAnalysisRequest {
  id: number;
  heights: Float32Array;
  gridSize: number;
  bounds: LatLonBounds;
  points: [[number, number], [number, number]];
  crestElevationM: number;
  streams: WaterLineFeature[];
  contourGridSize: number;
  contourIntervalM: number;
  baseElevationChecksum: string;
}

export type DamAnalysisResponse = { id: number; ok: true; result: DamAnalysisResult;
  grade: EarthworkTerrainPatch } |
  { id: number; ok: false; error: string };
