// Renderer-side wrapper over the terrain filesystem storage. Uses the Electron
// desktop bridge (electron/preload.ts) when present; falls back to localStorage
// when running as a plain web page (e.g. the GitHub Pages demo or `vite preview`)
// so the app doesn't crash. That fallback is dev/demo convenience only, not a
// production storage guarantee.
import { desktop } from './desktopBridge';
import type {
  TerrainSaveResponse,
  TerrainLoadResponse,
  TerrainListResponse,
  TerrainDeleteResponse,
} from './ipcContract';
import type { TerrainRecord, TerrainSummary } from './types';

const LOCAL_STORAGE_PREFIX = 'terrain-fallback:';
const LOCAL_STORAGE_INDEX_KEY = 'terrain-fallback-index';

function localList(): TerrainSummary[] {
  try {
    return JSON.parse(localStorage.getItem(LOCAL_STORAGE_INDEX_KEY) || '[]');
  } catch {
    return [];
  }
}

function localWriteIndex(summaries: TerrainSummary[]): void {
  localStorage.setItem(LOCAL_STORAGE_INDEX_KEY, JSON.stringify(summaries));
}

function toSummary(record: TerrainRecord): TerrainSummary {
  return {
    key: record.key,
    mountainName: record.mountainName,
    latitude: record.latitude,
    longitude: record.longitude,
    areaSizeMeters: record.areaSizeMeters,
    sourceType: record.sourceType,
    createdAt: record.createdAt,
    updatedAt: record.updatedAt,
  };
}

export async function saveTerrain(record: TerrainRecord): Promise<TerrainSaveResponse> {
  if (desktop) return desktop.terrain.save(record);

  try {
    localStorage.setItem(LOCAL_STORAGE_PREFIX + record.key, JSON.stringify(record));
    localWriteIndex([...localList().filter((s) => s.key !== record.key), toSummary(record)]);
    return { ok: true, key: record.key };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : 'Unknown error saving terrain' };
  }
}

export async function loadTerrain(key: string): Promise<TerrainLoadResponse> {
  if (desktop) return desktop.terrain.load(key);

  const raw = localStorage.getItem(LOCAL_STORAGE_PREFIX + key);
  return raw ? JSON.parse(raw) : null;
}

export async function listTerrains(): Promise<TerrainListResponse> {
  if (desktop) return desktop.terrain.list();
  return localList();
}

export async function deleteTerrain(key: string): Promise<TerrainDeleteResponse> {
  if (desktop) return desktop.terrain.delete(key);

  localStorage.removeItem(LOCAL_STORAGE_PREFIX + key);
  localWriteIndex(localList().filter((s) => s.key !== key));
  return { ok: true };
}
