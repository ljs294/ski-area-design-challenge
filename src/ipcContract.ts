// Shared IPC channel names + payload/response types for terrain filesystem
// storage. Imported by both the renderer (src/) and the Electron main
// process (electron/) — tsconfig.json already includes both directories.
import type {
  CoverDisplayMetadata,
  CoverGrid,
  CoverMetadata,
  GameSave,
  GameSaveSummary,
  TerrainPackageManifest,
  TerrainRecord,
  TerrainSummary,
  VectorFeatureSet,
} from './types';
import type { WeatherDataPackage } from './weather/weatherModel';

export const TERRAIN_SAVE_CHANNEL = 'terrain:save';
export const TERRAIN_SAVE_COVER_CHANNEL = 'terrain:save-cover';
export const TERRAIN_SAVE_CONTEXT_CHANNEL = 'terrain:save-context';
export const TERRAIN_LOAD_CHANNEL = 'terrain:load';
export const TERRAIN_LIST_CHANNEL = 'terrain:list';
export const TERRAIN_DELETE_CHANNEL = 'terrain:delete';
export const WEATHER_SAVE_CHANNEL = 'weather:save';
export const WEATHER_LOAD_CHANNEL = 'weather:load';
export const WEATHER_DELETE_CHANNEL = 'weather:delete';

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

/**
 * Compact package update used after an infrastructure cover edit. The
 * Electron writer merges these fields into the existing metadata document and
 * leaves every unrelated binary sidecar untouched.
 */
export interface TerrainCoverSaveRequest {
  key: string;
  coverGrid: CoverGrid;
  coverMetadata: CoverMetadata;
  coverDisplayGeometry: number[] | Float32Array;
  coverDisplayMetadata: CoverDisplayMetadata;
  packageManifest: TerrainPackageManifest;
  updatedAt: string;
}
export type TerrainCoverSaveResponse = TerrainSaveResponse;

/** Metadata-only update for optional OSM context. Binary terrain assets are
 * deliberately excluded so a slow provider response cannot overwrite a grade
 * or cover edit that completed while the request was in flight. */
export interface TerrainMapContextSaveRequest {
  key: string;
  vectorFeatures: VectorFeatureSet;
  updatedAt: string;
}
export type TerrainMapContextSaveResponse = TerrainSaveResponse;

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

export interface WeatherSaveRequest { weatherPackage: WeatherDataPackage; }
export type WeatherSaveResponse = { ok: true } | { ok: false; error: string };
export interface WeatherLoadRequest { terrainKey: string; }
export type WeatherLoadResponse = WeatherDataPackage | null;
export interface WeatherDeleteRequest { terrainKey: string; }
export type WeatherDeleteResponse = { ok: true } | { ok: false; error: string };
