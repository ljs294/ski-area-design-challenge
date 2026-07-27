// Renderer-side wrapper over the terrain filesystem storage. Uses the Electron
// desktop bridge (electron/preload.ts) when present; falls back to IndexedDB
// when running as a plain web page. Legacy localStorage records are migrated
// once on read, but no large package is ever written there.
import { desktop } from './desktopBridge';
import type {
  TerrainSaveResponse,
  TerrainLoadResponse,
  TerrainListResponse,
  TerrainDeleteResponse,
} from './ipcContract';
import type { TerrainRecord, TerrainSummary } from './types';

const LEGACY_PREFIX = 'terrain-fallback:';
const LEGACY_INDEX_KEY = 'terrain-fallback-index';
const DB_NAME = 'mountain-planner-terrain';
const STORE = 'terrains';

function legacyList(): TerrainSummary[] {
  try {
    return JSON.parse(localStorage.getItem(LEGACY_INDEX_KEY) || '[]');
  } catch {
    return [];
  }
}

function openTerrainDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, 1);
    req.onupgradeneeded = () => {
      if (!req.result.objectStoreNames.contains(STORE)) req.result.createObjectStore(STORE, { keyPath: 'key' });
    };
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error ?? new Error('Unable to open terrain database'));
  });
}

async function dbRequest<T>(mode: IDBTransactionMode, run: (store: IDBObjectStore) => IDBRequest<T>): Promise<T> {
  const db = await openTerrainDb();
  return new Promise((resolve, reject) => {
    const tx = db.transaction(STORE, mode);
    const req = run(tx.objectStore(STORE));
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error ?? new Error('Terrain database operation failed'));
    tx.oncomplete = () => db.close();
    tx.onerror = () => reject(tx.error ?? new Error('Terrain database transaction failed'));
  });
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
  if (desktop) return desktop.terrain.repairPackage(record);

  try {
    await dbRequest('readwrite', (store) => store.put(record));
    return { ok: true, key: record.key };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : 'Unknown error saving terrain' };
  }
}

export async function loadTerrain(key: string): Promise<TerrainLoadResponse> {
  if (desktop) return desktop.terrain.loadPackage(key);

  const stored = await dbRequest<TerrainRecord | undefined>('readonly', (store) => store.get(key));
  if (stored) return stored;
  // One-way compatibility for records created by the old demo fallback.
  const raw = localStorage.getItem(LEGACY_PREFIX + key);
  if (!raw) return null;
  const legacy = JSON.parse(raw) as TerrainRecord;
  await dbRequest('readwrite', (store) => store.put(legacy));
  return legacy;
}

export async function listTerrains(): Promise<TerrainListResponse> {
  if (desktop) return desktop.terrain.list();
  const records = await dbRequest<TerrainRecord[]>('readonly', (store) => store.getAll());
  const summaries = records.map(toSummary);
  for (const legacy of legacyList()) if (!summaries.some((s) => s.key === legacy.key)) summaries.push(legacy);
  return summaries;
}

export async function deleteTerrain(key: string): Promise<TerrainDeleteResponse> {
  if (desktop) return desktop.terrain.delete(key);

  await dbRequest('readwrite', (store) => store.delete(key));
  localStorage.removeItem(LEGACY_PREFIX + key);
  return { ok: true };
}
