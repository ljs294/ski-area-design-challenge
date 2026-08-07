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

export const SNOWMAKING_PIPE_DIAMETERS_IN = [
  4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24,
] as const;

export type SnowmakingPipeDiameterIn = (typeof SNOWMAKING_PIPE_DIAMETERS_IN)[number];

export type NumberedSnowmakingNodeKind = Exclude<SnowmakingNodeKind, 'intake'>;

export interface SnowmakingNodeNextNumbers {
  hydrant: number;
  junction: number;
  pump: number;
}

export type SnowmakingSourceRef =
  | { kind: 'dam'; damId: string }
  | { kind: 'pond'; pondId: string }
  | { kind: 'lake'; lakeId: string };

/** An imported standing-water feature selected as a snowmaking source. */
export interface SnowmakingLakeSource {
  id: string;
  name: string;
  boundary: [number, number][];
  surfaceElevationM: number | null;
  capacityM3: number | null;
}

export interface SavedSnowmakingNode {
  id: string;
  name: string;
  kind: SnowmakingNodeKind;
  /** Stable per-kind asset number. Intakes retain their source-derived label. */
  labelNumber?: number;
  point: [number, number];
  elevM: number | null;
  source?: SnowmakingSourceRef;
  createdAt: string;
}

export interface SavedSnowmakingPipeVertex {
  point: [number, number];
  /** Planning-terrain elevation sampled when the pipe was installed. */
  elevM: number | null;
  /** Connectivity is explicit; geometric crossings never imply a connection. */
  nodeId: string | null;
}

export interface SavedSnowmakingPipe {
  id: string;
  name: string;
  diameterIn: SnowmakingPipeDiameterIn;
  vertices: SavedSnowmakingPipeVertex[];
  /** Recomputed from vertices while hydrating. */
  lengthM: number;
  /** Highest minus lowest sampled station, or null while elevation is unresolved. */
  verticalM: number | null;
  createdAt: string;
}
