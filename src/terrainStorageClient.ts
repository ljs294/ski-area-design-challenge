// Renderer-side wrapper over the terrain filesystem IPC. Falls back to
// localStorage when running outside Electron (e.g. `vite preview` during
// dev) so the app doesn't crash — that path is for local dev convenience
// only, not a production storage guarantee.
import {
  TERRAIN_SAVE_CHANNEL,
  TERRAIN_LOAD_CHANNEL,
  TERRAIN_LIST_CHANNEL,
  TERRAIN_DELETE_CHANNEL,
} from './ipcContract';
import type {
  TerrainSaveRequest,
  TerrainSaveResponse,
  TerrainLoadRequest,
  TerrainLoadResponse,
  TerrainListResponse,
  TerrainDeleteRequest,
  TerrainDeleteResponse,
} from './ipcContract';
import type { TerrainRecord, TerrainSummary } from './types';

let ipcRenderer: any = null;
try {
  ipcRenderer = require('electron').ipcRenderer;
} catch {
  // Running in browser dev mode — fall back to localStorage below.
}

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
  if (ipcRenderer) {
    const req: TerrainSaveRequest = { record };
    return ipcRenderer.invoke(TERRAIN_SAVE_CHANNEL, req);
  }

  try {
    localStorage.setItem(LOCAL_STORAGE_PREFIX + record.key, JSON.stringify(record));
    localWriteIndex([...localList().filter((s) => s.key !== record.key), toSummary(record)]);
    return { ok: true, key: record.key };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : 'Unknown error saving terrain' };
  }
}

export async function loadTerrain(key: string): Promise<TerrainLoadResponse> {
  if (ipcRenderer) {
    const req: TerrainLoadRequest = { key };
    return ipcRenderer.invoke(TERRAIN_LOAD_CHANNEL, req);
  }

  const raw = localStorage.getItem(LOCAL_STORAGE_PREFIX + key);
  return raw ? JSON.parse(raw) : null;
}

export async function listTerrains(): Promise<TerrainListResponse> {
  if (ipcRenderer) {
    return ipcRenderer.invoke(TERRAIN_LIST_CHANNEL);
  }
  return localList();
}

export async function deleteTerrain(key: string): Promise<TerrainDeleteResponse> {
  if (ipcRenderer) {
    const req: TerrainDeleteRequest = { key };
    return ipcRenderer.invoke(TERRAIN_DELETE_CHANNEL, req);
  }

  localStorage.removeItem(LOCAL_STORAGE_PREFIX + key);
  localWriteIndex(localList().filter((s) => s.key !== key));
  return { ok: true };
}
