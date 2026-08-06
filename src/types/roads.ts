import type { EarthworkEstimate } from './earthwork';

export type RoadType = 'two-lane';

export interface SavedRoad {
  id: string;
  name: string;
  roadType: RoadType;
  widthM: number;
  points: [number, number][];
  lengthM: number;
  terrainGraded?: boolean;
  earthwork?: EarthworkEstimate;
  createdAt: string;
}
