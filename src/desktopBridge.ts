// Renderer-side access to the Electron desktop API exposed by electron/preload.ts
// via contextBridge. In the web-demo build (GitHub Pages) `window.desktop` is
// absent, so callers must handle `null` and fall back to browser equivalents.
import type {
  TerrainSaveResponse,
  TerrainCoverSaveRequest,
  TerrainCoverSaveResponse,
  TerrainMapContextSaveRequest,
  TerrainMapContextSaveResponse,
  TerrainLoadResponse,
  TerrainListResponse,
  TerrainDeleteResponse,
  WeatherSaveResponse,
  WeatherLoadResponse,
  WeatherDeleteResponse,
  GameSaveSaveResponse,
  GameSaveLoadResponse,
  GameSaveListResponse,
  GameSaveDeleteResponse,
  GameSavePreviewCaptureResponse,
  GameSavePreviewLoadResponse,
  WindowMode,
} from './ipcContract';
import type { TerrainRecord } from './types';
import type { GameSave } from './types';
import type { WeatherDataPackage } from './weather/weatherModel';

export interface DesktopApi {
  isDesktop: true;
  terrain: {
    save(record: TerrainRecord): Promise<TerrainSaveResponse>;
    load(key: string): Promise<TerrainLoadResponse>;
    /** Package-named aliases used by resort preparation and repair flows. */
    loadPackage(key: string): Promise<TerrainLoadResponse>;
    repairPackage(record: TerrainRecord): Promise<TerrainSaveResponse>;
    /** Writes only edited cover assets and their merged package metadata. */
    saveCover(request: TerrainCoverSaveRequest): Promise<TerrainCoverSaveResponse>;
    /** Merges OSM vectors into metadata without rewriting terrain binaries. */
    saveMapContext(request: TerrainMapContextSaveRequest): Promise<TerrainMapContextSaveResponse>;
    list(): Promise<TerrainListResponse>;
    delete(key: string): Promise<TerrainDeleteResponse>;
  };
  weather: {
    save(weatherPackage: WeatherDataPackage): Promise<WeatherSaveResponse>;
    load(terrainKey: string): Promise<WeatherLoadResponse>;
    delete(terrainKey: string): Promise<WeatherDeleteResponse>;
  };
  games: {
    save(save: GameSave): Promise<GameSaveSaveResponse>;
    load(key: string): Promise<GameSaveLoadResponse>;
    list(): Promise<GameSaveListResponse>;
    delete(key: string): Promise<GameSaveDeleteResponse>;
    capturePreview(key: string): Promise<GameSavePreviewCaptureResponse>;
    loadPreview(key: string): Promise<GameSavePreviewLoadResponse>;
  };
  window: {
    getMode(): Promise<WindowMode>;
    setMode(mode: WindowMode): Promise<WindowMode>;
  };
  lifecycle: {
    onCloseCheckpointRequested(listener: () => void): () => void;
    completeCloseCheckpoint(): void;
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
