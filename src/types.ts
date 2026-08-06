// Mountain Planner compatibility type facade. Authoritative models live in
// src/types/<domain>.ts; implementation modules should import those directly.
import type { AnchorRef } from './types/anchors';
import type { LiftStatus, TrailStatus } from './types/construction';
import type { EarthworkEstimate } from './types/earthwork';
import type { SavedNode, SavedPath } from './skiNodes';
import type { SavedSnowmakingNode } from './snowmakingNodes';

export type { AnchorRef } from './types/anchors';
export type { ConstructionStatus, LiftStatus, TrailStatus } from './types/construction';
export type { EarthworkEstimate } from './types/earthwork';
export type {
  CoverClassCode,
  CoverDisplayMetadata,
  CoverGeometryMetadata,
  CoverGrid,
  CoverGridData,
  CoverMetadata,
  LandCoverClass,
  OriginalCoverMetadata,
  SiteCoverGrid,
  TerrainCoverClass,
  TerrainCoverCode,
  TerrainCoverGrid,
  TerrainCoverProvenance,
  WorldCoverClassCode,
} from './types/cover';
export type {
  AreaSizeMeters,
  ClimateMonth,
  ClimateProfile,
  ContourMetadata,
  LocalImageryMetadata,
  SurroundElevation,
  TerrainPackageManifest,
  TerrainPackagePhase,
  TerrainPackageProgress,
  TerrainPackageValidation,
  TerrainRecord,
  TerrainSummary,
} from './types/terrain';
export type {
  LandCoverFeature,
  OsmLandCoverClass,
  PeakFeature,
  RoadClass,
  RoadFeature,
  VectorFeatureSet,
  WaterLineClass,
  WaterLineFeature,
  WaterPolygonFeature,
} from './types/vectorFeatures';

// Serialized [[west, south], [east, north]] site rectangle. Mirrors SiteBox in
// src/app/sitePicker.ts without depending on a renderer-only module.
export interface SavedSiteBox {
  bounds: [[number, number], [number, number]];
  widthKm: number;
  heightKm: number;
  areaKm2: number;
}

export type LiftClass = 'fixed-grip';
export type ChairSize = 2 | 3 | 4;

interface SavedLiftBase {
  id: string;
  name: string;
  liftClass: LiftClass;
  points: [[number, number], [number, number]];
  endpointElevM: [number | null, number | null];
  lengthM: number;
  verticalM: number | null;
  status: LiftStatus;
  closed?: boolean;
  createdAt: string;
}

export interface SavedFixedGripLift extends SavedLiftBase {
  liftClass: 'fixed-grip';
  chairSize: ChairSize;
}

export type SavedLift = SavedFixedGripLift;

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

export type TrailDifficulty = 'green' | 'blue' | 'black' | 'red';

export interface SavedTrailPart {
  polygon: [number, number][][];
  segments?: SavedTrailSegment[];
  centerline: [number, number][];
  centerlineElevM: number[];
}

export interface SavedTrailSegment {
  id: string;
  centerline: [number, number][];
  centerlineElevM: number[];
  fromJunctionId: string;
  toJunctionId: string;
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

/** A player's persisted resort design. This is a compatibility boundary. */
export interface GameSave {
  schemaVersion: 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11;
  key: string;
  name: string;
  mountainId?: string;
  terrainKey?: string;
  center: [number, number];
  zoom: number;
  bearing: number;
  pitch: number;
  is3D: boolean;
  site: SavedSiteBox | null;
  lifts: SavedLift[];
  trails: SavedTrail[];
  roads?: SavedRoad[];
  dams?: SavedDam[];
  ponds?: SavedPond[];
  nodes?: SavedNode[];
  paths?: SavedPath[];
  junctions?: import('./skiNodes').SavedJunction[];
  snowmakingNodes?: SavedSnowmakingNode[];
  lakeDepthOverrides?: Record<string, number>;
  lakeNameOverrides?: Record<string, string>;
  streamWidthOverrides?: Record<string, number>;
  createdAt: string;
  updatedAt: string;
  lastPlayedAt?: string;
}

export type GameSaveSummary = Pick<
  GameSave,
  'key' | 'name' | 'mountainId' | 'terrainKey' | 'createdAt' | 'updatedAt' | 'lastPlayedAt'
>;
