// Shared IPC channel names + payload/response types for terrain filesystem
// storage. Imported by both the renderer (src/) and the Electron main
// process (electron/) — tsconfig.json already includes both directories.
import type { TerrainRecord, TerrainSummary, GameSave, GameSaveSummary } from './types';

export const TERRAIN_SAVE_CHANNEL = 'terrain:save';
export const TERRAIN_LOAD_CHANNEL = 'terrain:load';
export const TERRAIN_LIST_CHANNEL = 'terrain:list';
export const TERRAIN_DELETE_CHANNEL = 'terrain:delete';

// --- Game saves (resort designs). Distinct from raw terrain records. ---
export const GAMESAVE_SAVE_CHANNEL = 'gamesave:save';
export const GAMESAVE_LOAD_CHANNEL = 'gamesave:load';
export const GAMESAVE_LIST_CHANNEL = 'gamesave:list';
export const GAMESAVE_DELETE_CHANNEL = 'gamesave:delete';
export const GAMESAVE_CAPTURE_PREVIEW_CHANNEL = 'gamesave:capture-preview';
export const GAMESAVE_LOAD_PREVIEW_CHANNEL = 'gamesave:load-preview';

// --- Window / shell control ---
export const WINDOW_GET_MODE_CHANNEL = 'window:get-mode';
export const WINDOW_SET_MODE_CHANNEL = 'window:set-mode';
export const EXIT_CHANNEL = 'exit-game';
export const WINDOW_REQUEST_CLOSE_CHECKPOINT_CHANNEL = 'window:request-close-checkpoint';
export const WINDOW_CLOSE_CHECKPOINT_COMPLETE_CHANNEL = 'window:close-checkpoint-complete';

export type WindowMode = 'windowed' | 'fullscreen' | 'borderless';

export interface GameSaveSaveRequest {
  save: GameSave;
}
export type GameSaveSaveResponse =
  | { ok: true; key: string }
  | { ok: false; error: string };

export interface GameSaveLoadRequest {
  key: string;
}
export type GameSaveLoadResponse = GameSave | null;

export type GameSaveListResponse = GameSaveSummary[];

export interface GameSaveDeleteRequest {
  key: string;
}
export interface GameSaveDeleteResponse {
  ok: boolean;
}

export interface GameSavePreviewRequest {
  key: string;
}
export type GameSavePreviewCaptureResponse =
  | { ok: true }
  | { ok: false; error: string };
export type GameSavePreviewLoadResponse =
  | { ok: true; dataUrl: string | null }
  | { ok: false; error: string };

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
