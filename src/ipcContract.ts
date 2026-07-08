// Shared IPC channel names + payload/response types for terrain filesystem
// storage. Imported by both the renderer (src/) and the Electron main
// process (electron/) — tsconfig.json already includes both directories.
import type { TerrainRecord, TerrainSummary } from './types';

export const TERRAIN_SAVE_CHANNEL = 'terrain:save';
export const TERRAIN_LOAD_CHANNEL = 'terrain:load';
export const TERRAIN_LIST_CHANNEL = 'terrain:list';
export const TERRAIN_DELETE_CHANNEL = 'terrain:delete';

export interface TerrainSaveRequest {
  record: TerrainRecord;
}
export type TerrainSaveResponse =
  | { ok: true; key: string }
  | { ok: false; error: string };

export interface TerrainLoadRequest {
  key: string;
}
export type TerrainLoadResponse = TerrainRecord | null;

export type TerrainListResponse = TerrainSummary[];

export interface TerrainDeleteRequest {
  key: string;
}
export interface TerrainDeleteResponse {
  ok: boolean;
}
