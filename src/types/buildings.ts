import type { AssetEconomics } from './economics';
import type { EarthworkEstimate } from './earthwork';

export type BuildingTypeId = 'snowmaking-pump-house';

export type BuildingFoundationKind = 'flattened' | 'slope';

/** Eight samples clockwise: SW, S, SE, E, NE, N, NW, W. */
export type BuildingPerimeterElevationsM = number[];

export interface FlattenedBuildingFoundation {
  kind: 'flattened';
  /** Optional authoring alias; canonical saved output uses `kind`. */
  mode?: 'flattened';
  finishedFloorElevationM: number;
  terrainGraded: true;
  earthwork: EarthworkEstimate;
}

export interface SlopeBuildingFoundation {
  kind: 'slope';
  /** Optional authoring alias; canonical saved output uses `kind`. */
  mode?: 'slope';
  finishedFloorElevationM: number;
  terrainGraded: false;
  perimeterGroundElevationsM: BuildingPerimeterElevationsM;
}

export type BuildingFoundation = FlattenedBuildingFoundation | SlopeBuildingFoundation;

export interface GableRoofParameters {
  kind: 'gable';
  pitchRise: 4;
  pitchRun: 12;
}

export interface SnowmakingPumpConnection {
  kind: 'snowmaking-pump';
  nodeId: string;
}

/** Persisted, resolved geometry parameters for a player-built building. */
export interface SavedBuilding {
  id: string;
  name: string;
  buildingTypeId: BuildingTypeId;
  generatorVersion: 1;
  center: [number, number];
  /** Clockwise degrees from true north, normalized to [0, 360). */
  bearingDeg: number;
  dimensions: {
    lengthM: number;
    widthM: number;
    eaveHeightM: number;
  };
  roof: GableRoofParameters;
  foundation: BuildingFoundation;
  connection: SnowmakingPumpConnection;
  economics: AssetEconomics;
  createdAt: string;
}

export type BuildingFoundationMode = BuildingFoundationKind;

/** The editable inputs before terrain analysis resolves a foundation. */
export interface BuildingDraftParameters {
  buildingTypeId: BuildingTypeId;
  name: string;
  center: [number, number];
  bearingDeg: number;
  dimensions: {
    lengthM: number;
    widthM: number;
    eaveHeightM: number;
  };
  foundationMode: BuildingFoundationMode;
}
