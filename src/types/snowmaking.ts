import type { EarthworkEstimate } from './earthwork';

/** A player-built dam and the full-pool snowmaking pond analyzed at build time. */
export interface SavedDam {
  id: string;
  name: string;
  points: [[number, number], [number, number]];
  crestElevationM: number;
  streamId: string;
  streamName: string;
  sourceWidthM: number;
  inflowM3s: number;
  pondRings: [number, number][][];
  areaM2: number;
  averageDepthM: number;
  capacityM3: number;
  averageDamHeightM?: number;
  maxDamHeightM: number;
  damCrestElevationM?: number;
  crestRing?: [number, number][];
  footprintRings?: [number, number][][];
  builtLengthM?: number;
  disturbedAreaM2?: number;
  earthwork?: EarthworkEstimate;
  terrainGraded?: boolean;
  createdAt: string;
}

/** A player-drawn standalone pond. It has no natural inflow. */
export interface SavedPond {
  id: string;
  name: string;
  boundary: [number, number][];
  topElevationM: number;
  areaM2: number;
  averageDepthM: number;
  maxDepthM: number;
  capacityM3: number;
  isSnowmaking?: boolean;
  excavationDepthM?: number;
  crestElevationM?: number;
  maxBermHeightM?: number;
  bermLengthM?: number;
  maxCutDepthM?: number;
  disturbedAreaM2?: number;
  terrainGraded?: boolean;
  earthwork?: EarthworkEstimate;
  createdAt: string;
}

export type SnowmakingNodeKind = 'intake' | 'pump' | 'junction' | 'hydrant';

export type SnowmakingSourceRef =
  | { kind: 'dam'; damId: string }
  | { kind: 'pond'; pondId: string };

export interface SavedSnowmakingNode {
  id: string;
  name: string;
  kind: SnowmakingNodeKind;
  point: [number, number];
  elevM: number | null;
  source?: SnowmakingSourceRef;
  createdAt: string;
}
