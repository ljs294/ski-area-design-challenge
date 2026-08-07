import type { LatLonBounds } from '../types/geo';
import type { DamAnalysisResult } from '../damAnalysis';
import type { WaterLineFeature } from '../types';

export interface DamAnalysisRequest {
  id: number;
  heights: Float32Array;
  gridSize: number;
  bounds: LatLonBounds;
  points: [[number, number], [number, number]];
  crestElevationM: number;
  streams: WaterLineFeature[];
}

export type DamAnalysisResponse = { id: number; ok: true; result: DamAnalysisResult } |
  { id: number; ok: false; error: string };
