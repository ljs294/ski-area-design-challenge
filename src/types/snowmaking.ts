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
  /** Set only for the reciprocal center pump owned by a player building. */
  ownerBuildingId?: string;
  /** Fixed for building-owned pumps; omitted for legacy/manual pumps. */
  pumpRating?: {
    horsepowerHp: number;
    efficiency: number;
  };
  createdAt: string;
}

export interface SavedSnowmakingPipeVertex {
  point: [number, number];
  /** Planning-terrain elevation sampled when the pipe was installed. */
  elevM: number | null;
  /** Connectivity is explicit; geometric crossings never imply a connection. */
  nodeId: string | null;
}

export type SnowmakingPumpPort = 'suction' | 'discharge';

/**
 * Stable metadata for one node-bounded portion of an editable pipe route.
 * Geometry and connectivity remain authoritative in the parent `vertices`;
 * these records provide durable hydraulic edge identity and classify the two
 * half-edges that can meet pump nodes.
 */
export interface SavedSnowmakingPipeSegment {
  id: string;
  /** Inclusive index into the parent pipe's vertices. */
  startVertexIndex: number;
  /** Inclusive index into the parent pipe's vertices. */
  endVertexIndex: number;
  startPumpPort: SnowmakingPumpPort | null;
  endPumpPort: SnowmakingPumpPort | null;
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
  /**
   * Added in save schema 12. Optional at the compatibility boundary so saves
   * from schema 11 and earlier remain representable; hydration always fills it.
   */
  segments?: SavedSnowmakingPipeSegment[];
  createdAt: string;
}

export type SnowgunVariantId =
  | 'HKD_ImpulseR5_10s'
  | 'HKD_ImpulseR5_10t'
  | 'HKD_ImpulseR5_20t'
  | 'HKD_ImpulseR5_30t';

/** Installed snowmaking equipment. Guns consume at hydrants but are not pipe-topology nodes. */
export interface SavedSnowgun {
  id: string;
  variantId: SnowgunVariantId;
  point: [number, number];
  elevM: number | null;
  /** Null means installed but currently disconnected. */
  hydrantId: string | null;
  createdAt: string;
}
