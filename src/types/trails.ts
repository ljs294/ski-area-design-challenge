import type { AnchorRef } from './anchors';
import type { ConstructionStatus } from './construction';
import type { EarthworkEstimate } from './earthwork';
import type { SavedTrailSegment } from './topology';

export type TrailDifficulty = 'green' | 'blue' | 'black' | 'red';
export type TrailStatus = ConstructionStatus;

export interface SavedTrailPart {
  polygon: [number, number][][];
  segments?: SavedTrailSegment[];
  centerline: [number, number][];
  centerlineElevM: number[];
}

export interface SavedTrail {
  id: string;
  name: string;
  parts: SavedTrailPart[];
  brushWidthM: number;
  areaM2: number;
  lengthM: number;
  verticalM: number | null;
  avgSlopeDeg: number;
  maxSlopeDeg: number;
  difficulty: TrailDifficulty;
  terrainGraded?: boolean;
  earthwork?: EarthworkEstimate;
  status: TrailStatus;
  closed?: boolean;
  anchor?: AnchorRef;
  createdAt: string;
}
