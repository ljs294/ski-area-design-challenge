import type { SavedLift } from './lifts';
import type { SavedRoad } from './roads';
import type { SavedDam, SavedPond, SavedSnowgun, SavedSnowmakingNode, SavedSnowmakingPipe,
  SnowmakingNodeNextNumbers } from './snowmaking';
import type { SavedJunction, SavedNode, SavedPath } from './topology';
import type { SavedTrail } from './trails';
import type { SavedSnowGrid } from './snow';
import type { SavedBuilding } from './buildings';

export interface SavedSiteBox {
  bounds: [[number, number], [number, number]];
  widthKm: number;
  heightKm: number;
  areaKm2: number;
}

/**
 * A deterministic weather session pinned to an immutable offline package.
 * It is deliberately optional: saves written before weather integration, and
 * design-only saves written after it, remain weather-unprepared.
 */
export interface SavedWeatherRun {
  packageContentHash: string;
  terrainBinding: string;
  seed: string;
  generatorVersion: number;
  configurationVersion: number;
  /** ISO local-calendar anchor, including its UTC offset. */
  localStartAt: string;
  /** Whole simulated hours elapsed from localStartAt. */
  cursorHour: number;
}

export function isSavedWeatherRun(value: unknown): value is SavedWeatherRun {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const run = value as Partial<SavedWeatherRun>;
  return typeof run.packageContentHash === 'string' && run.packageContentHash.length > 0 &&
    typeof run.terrainBinding === 'string' && run.terrainBinding.length > 0 &&
    typeof run.seed === 'string' &&
    Number.isSafeInteger(run.generatorVersion) && (run.generatorVersion as number) >= 1 &&
    Number.isSafeInteger(run.configurationVersion) && (run.configurationVersion as number) >= 1 &&
    typeof run.localStartAt === 'string' && run.localStartAt.length > 0 &&
    Number.isSafeInteger(run.cursorHour) && (run.cursorHour as number) >= 0;
}

/** A player's persisted resort design. This is a compatibility boundary. */
export interface GameSave {
  schemaVersion: 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 | 12 | 13 | 14 | 15;
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
  junctions?: SavedJunction[];
  snowmakingNodes?: SavedSnowmakingNode[];
  snowmakingPipes?: SavedSnowmakingPipe[];
  snowguns?: SavedSnowgun[];
  snowmakingNodeNextNumbers?: SnowmakingNodeNextNumbers;
  lakeDepthOverrides?: Record<string, number>;
  lakeNameOverrides?: Record<string, string>;
  /** Imported standing-water feature IDs designated for snowmaking. */
  snowmakingLakeIds?: string[];
  streamWidthOverrides?: Record<string, number>;
  /** Added in schema 13. Older saves derive a deterministic baseline at load. */
  snow?: SavedSnowGrid;
  /** Added in schema 15. Never triggers a provider request while loading a save. */
  weatherRun?: SavedWeatherRun;
  /** Added in schema 15. Hydration defaults absent collections to an empty list. */
  buildings?: SavedBuilding[];
  createdAt: string;
  updatedAt: string;
  lastPlayedAt?: string;
}

export type GameSaveSummary = Pick<
  GameSave,
  'key' | 'name' | 'mountainId' | 'terrainKey' | 'createdAt' | 'updatedAt' | 'lastPlayedAt'
>;
