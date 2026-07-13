// Renderer-side access to the Electron desktop API exposed by electron/preload.ts
// via contextBridge. In the web-demo build (GitHub Pages) `window.desktop` is
// absent, so callers must handle `null` and fall back to browser equivalents.
import type {
  TerrainSaveResponse,
  TerrainLoadResponse,
  TerrainListResponse,
  TerrainDeleteResponse,
  GameSaveSaveResponse,
  GameSaveLoadResponse,
  GameSaveListResponse,
  GameSaveDeleteResponse,
  WindowMode,
} from './ipcContract';
import type { TerrainRecord } from './types';
import type { GameSave } from './types';

export interface DesktopApi {
  isDesktop: true;
  terrain: {
    save(record: TerrainRecord): Promise<TerrainSaveResponse>;
    load(key: string): Promise<TerrainLoadResponse>;
    list(): Promise<TerrainListResponse>;
    delete(key: string): Promise<TerrainDeleteResponse>;
  };
  games: {
    save(save: GameSave): Promise<GameSaveSaveResponse>;
    load(key: string): Promise<GameSaveLoadResponse>;
    list(): Promise<GameSaveListResponse>;
    delete(key: string): Promise<GameSaveDeleteResponse>;
  };
  window: {
    getMode(): Promise<WindowMode>;
    setMode(mode: WindowMode): Promise<WindowMode>;
  };
  exit(): void;
}

declare global {
  interface Window {
    desktop?: DesktopApi;
  }
}

/** The desktop bridge, or null when running as a plain web page. */
export const desktop: DesktopApi | null =
  typeof window !== 'undefined' && window.desktop ? window.desktop : null;

export const isDesktop = desktop !== null;
