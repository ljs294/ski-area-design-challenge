import type { SavedLift } from './lifts';
import type { SavedJunction, SavedNode, SavedPath } from './topology';
import type { SavedTrail } from './trails';
import type { SavedSiteBox } from './gameSave';
import type { TerrainRecord } from './terrain';

export const DESIGN_SAVE_FORMAT = 'mountain-planner-three-design' as const;
export const DESIGN_SAVE_SCHEMA_VERSION = 1 as const;

export interface DesignCameraState {
  center: [number, number];
  zoom: number;
  bearing: number;
  pitch: number;
  is3D: boolean;
}

export interface DesignRevisionSet {
  design: number;
  terrain: number;
  topology: number;
  lifts: number;
}

export interface DesignTerrainReference {
  logicalKey: string;
  generationId: string;
  sourceRevision: number;
  updatedAt: string;
  elevationChecksum?: string;
  coverChecksum?: string;
}

export interface DesignSaveDocument {
  format: typeof DESIGN_SAVE_FORMAT;
  schemaVersion: typeof DESIGN_SAVE_SCHEMA_VERSION;
  manifestId: string;
  key: string;
  name: string;
  createdAt: string;
  updatedAt: string;
  site: SavedSiteBox | null;
  camera: DesignCameraState;
  terrain: DesignTerrainReference;
  revisions: DesignRevisionSet;
  lifts: SavedLift[];
  trails: SavedTrail[];
  nodes: SavedNode[];
  paths: SavedPath[];
  junctions: SavedJunction[];
}

export interface DesignSaveDraft extends Omit<DesignSaveDocument, 'manifestId' | 'terrain'> {
  terrainRecord: TerrainRecord;
}

export interface DesignTerrainGeneration {
  format: 'mountain-planner-three-terrain-generation';
  schemaVersion: 1;
  generationId: string;
  logicalKey: string;
  sourceRevision: number;
  createdAt: string;
  record: TerrainRecord;
}

export type DesignSaveSummary = Pick<
  DesignSaveDocument,
  'key' | 'name' | 'createdAt' | 'updatedAt' | 'revisions'
> & { terrain: DesignTerrainReference };

export interface DesignSaveBundle {
  save: DesignSaveDocument;
  terrain: TerrainRecord;
}

export interface DesignSaveReceipt {
  key: string;
  manifestId: string;
  terrainGenerationId: string;
  revisions: DesignRevisionSet;
  warnings: string[];
}

export type DesignSaveResponse =
  | { ok: true; receipt: DesignSaveReceipt }
  | { ok: false; error: string };

export type DesignLoadResponse =
  | { ok: true; bundle: DesignSaveBundle }
  | { ok: false; error: string }
  | null;
