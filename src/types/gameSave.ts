import type { SavedLift } from './lifts';
import type { SavedRoad } from './roads';
import type { SavedDam, SavedPond, SavedSnowgun, SavedSnowmakingNode, SavedSnowmakingPipe,
  SnowmakingNodeNextNumbers } from './snowmaking';
import type { SavedJunction, SavedNode, SavedPath } from './topology';
import type { SavedTrail } from './trails';
import type { SavedSnowGrid } from './snow';

export interface SavedSiteBox {
  bounds: [[number, number], [number, number]];
  widthKm: number;
  heightKm: number;
  areaKm2: number;
}

/** A player's persisted resort design. This is a compatibility boundary. */
export interface GameSave {
  schemaVersion: 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 | 12 | 13;
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
  createdAt: string;
  updatedAt: string;
  lastPlayedAt?: string;
}

export type GameSaveSummary = Pick<
  GameSave,
  'key' | 'name' | 'mountainId' | 'terrainKey' | 'createdAt' | 'updatedAt' | 'lastPlayedAt'
>;
