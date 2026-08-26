// Renderer-side wrapper over the terrain filesystem storage. Uses the Electron
// desktop bridge (electron/preload.ts) when present; falls back to IndexedDB
// when running as a plain web page. Legacy localStorage records are migrated
// once on read, but no large package is ever written there.
import { desktop } from './desktopBridge';
import { deleteWeatherPackage } from './weatherStorageClient';
import type {
  TerrainSaveResponse,
  TerrainCoverSaveRequest,
  TerrainMapContextSaveRequest,
  TerrainLoadResponse,
  TerrainListResponse,
  TerrainDeleteResponse,
} from './ipcContract';
import type {
  RuntimeTerrainPackage,
  TerrainAssetMask,
  TerrainAssetRevision,
  TerrainAssetStore,
  TerrainRecord,
  TerrainSummary,
} from './types/terrain';

const LEGACY_PREFIX = 'terrain-fallback:';
const LEGACY_INDEX_KEY = 'terrain-fallback-index';
const DB_NAME = 'mountain-planner-terrain';
const LEGACY_STORE = 'terrains';
const SUMMARY_STORE = 'terrain-summaries';
const METADATA_STORE = 'terrain-metadata';
const ASSET_STORE = 'terrain-assets';

type TerrainMetadata = Omit<
  TerrainRecord,
  | 'sampleHeights'
  | 'coverGrid'
  | 'originalCoverGrid'
  | 'coverBoundarySegments'
  | 'coverDisplayGeometry'
  | 'contourSegments'
  | 'localImagery'
> & { key: string };

interface BrowserTerrainAssets {
  key: string;
  sampleHeights: Float32Array;
  coverGrid?: TerrainRecord['coverGrid'];
  originalCoverGrid?: TerrainRecord['originalCoverGrid'];
  coverBoundarySegments?: Float32Array;
  coverDisplayGeometry?: Float32Array;
  contourSegments?: Float32Array;
  localImagery?: Uint8Array;
}

function legacyList(): TerrainSummary[] {
  try {
    return JSON.parse(localStorage.getItem(LEGACY_INDEX_KEY) || '[]');
  } catch {
    return [];
  }
}

function openTerrainDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, 2);
    req.onupgradeneeded = () => {
      for (const name of [LEGACY_STORE, SUMMARY_STORE, METADATA_STORE, ASSET_STORE]) {
        if (!req.result.objectStoreNames.contains(name)) req.result.createObjectStore(name, { keyPath: 'key' });
      }
    };
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error ?? new Error('Unable to open terrain database'));
  });
}

async function dbRequest<T>(
  storeName: string,
  mode: IDBTransactionMode,
  run: (store: IDBObjectStore) => IDBRequest<T>,
): Promise<T> {
  const db = await openTerrainDb();
  return new Promise((resolve, reject) => {
    const tx = db.transaction(storeName, mode);
    const req = run(tx.objectStore(storeName));
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error ?? new Error('Terrain database operation failed'));
    tx.oncomplete = () => db.close();
    tx.onerror = () => reject(tx.error ?? new Error('Terrain database transaction failed'));
  });
}

function splitRecord(record: TerrainRecord): { metadata: TerrainMetadata; assets: BrowserTerrainAssets } {
  const {
    sampleHeights,
    coverGrid,
    originalCoverGrid,
    coverBoundarySegments,
    coverDisplayGeometry,
    contourSegments,
    localImagery,
    ...metadata
  } = record;
  return {
    metadata,
    assets: {
      key: record.key,
      sampleHeights: sampleHeights instanceof Float32Array ? sampleHeights : Float32Array.from(sampleHeights),
      ...(coverGrid ? { coverGrid: { ...coverGrid,
        data: coverGrid.data instanceof Uint8Array ? coverGrid.data : Uint8Array.from(coverGrid.data) } } : {}),
      ...(originalCoverGrid ? { originalCoverGrid: { ...originalCoverGrid,
        data: originalCoverGrid.data instanceof Uint8Array
          ? originalCoverGrid.data
          : Uint8Array.from(originalCoverGrid.data) } } : {}),
      ...(coverBoundarySegments ? { coverBoundarySegments: coverBoundarySegments instanceof Float32Array
        ? coverBoundarySegments : Float32Array.from(coverBoundarySegments) } : {}),
      ...(coverDisplayGeometry ? { coverDisplayGeometry: coverDisplayGeometry instanceof Float32Array
        ? coverDisplayGeometry : Float32Array.from(coverDisplayGeometry) } : {}),
      ...(contourSegments ? { contourSegments: contourSegments instanceof Float32Array
        ? contourSegments : Float32Array.from(contourSegments) } : {}),
      ...(localImagery ? { localImagery: localImagery instanceof Uint8Array
        ? localImagery : Uint8Array.from(localImagery) } : {}),
    },
  };
}

function joinRecord(metadata: TerrainMetadata, assets: BrowserTerrainAssets): TerrainRecord {
  return { ...metadata, ...assets };
}

async function putSplitRecord(record: TerrainRecord): Promise<void> {
  const db = await openTerrainDb();
  const { metadata, assets } = splitRecord(record);
  await new Promise<void>((resolve, reject) => {
    const tx = db.transaction([SUMMARY_STORE, METADATA_STORE, ASSET_STORE], 'readwrite');
    tx.objectStore(SUMMARY_STORE).put(toSummary(record));
    tx.objectStore(METADATA_STORE).put(metadata);
    tx.objectStore(ASSET_STORE).put(assets);
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error ?? new Error('Unable to save terrain package'));
    tx.onabort = tx.onerror;
  }).finally(() => db.close());
}

async function loadSplitRecord(key: string): Promise<TerrainRecord | null> {
  const db = await openTerrainDb();
  const result = await new Promise<TerrainRecord | null>((resolve, reject) => {
    const tx = db.transaction([METADATA_STORE, ASSET_STORE], 'readonly');
    const metadataRequest = tx.objectStore(METADATA_STORE).get(key) as IDBRequest<TerrainMetadata | undefined>;
    const assetRequest = tx.objectStore(ASSET_STORE).get(key) as IDBRequest<BrowserTerrainAssets | undefined>;
    tx.oncomplete = () => resolve(metadataRequest.result && assetRequest.result
      ? joinRecord(metadataRequest.result, assetRequest.result)
      : null);
    tx.onerror = () => reject(tx.error ?? new Error('Unable to load terrain package'));
    tx.onabort = tx.onerror;
  }).finally(() => db.close());
  return result;
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
    await putSplitRecord(record);
    return { ok: true, key: record.key };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : 'Unknown error saving terrain' };
  }
}

/**
 * Persist an infrastructure ground-cover edit without rewriting immutable
 * terrain assets on desktop. IndexedDB stores a single record, so the browser
 * fallback intentionally retains its full-record put.
 */
export async function saveTerrainCover(record: TerrainRecord): Promise<TerrainSaveResponse> {
  if (
    !record.coverGrid
    || !record.coverMetadata
    || !record.coverDisplayGeometry
    || !record.coverDisplayMetadata
    || !record.packageManifest
  ) {
    return { ok: false, error: 'Edited terrain is missing prepared ground-cover assets' };
  }

  if (desktop) {
    const request: TerrainCoverSaveRequest = {
      key: record.key,
      coverGrid: record.coverGrid,
      coverMetadata: record.coverMetadata,
      coverDisplayGeometry: record.coverDisplayGeometry,
      coverDisplayMetadata: record.coverDisplayMetadata,
      packageManifest: record.packageManifest,
      updatedAt: record.updatedAt,
    };
    return desktop.terrain.saveCover(request);
  }

  try {
    await putSplitRecord(record);
    return { ok: true, key: record.key };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : 'Unknown error saving terrain cover' };
  }
}

/** Persist only OSM context. Desktop merges JSON metadata in place; the browser
 * performs the equivalent read-modify-write in one IndexedDB transaction. */
export async function saveTerrainMapContext(
  request: TerrainMapContextSaveRequest,
): Promise<TerrainSaveResponse> {
  if (desktop) return desktop.terrain.saveMapContext(request);
  const record = await loadTerrain(request.key);
  if (!record) return { ok: false, error: 'Terrain package is missing' };
  try {
    await putSplitRecord({ ...record, vectorFeatures: request.vectorFeatures, updatedAt: request.updatedAt });
    return { ok: true, key: request.key };
  } catch (error) {
    return { ok: false, error: error instanceof Error ? error.message : 'Unable to save map context' };
  }
}

export async function loadTerrain(key: string): Promise<TerrainLoadResponse> {
  if (desktop) return desktop.terrain.loadPackage(key);

  const split = await loadSplitRecord(key);
  if (split) return split;
  const stored = await dbRequest<TerrainRecord | undefined>(LEGACY_STORE, 'readonly', (store) => store.get(key));
  if (stored) {
    await putSplitRecord(stored);
    return (await loadSplitRecord(key)) ?? stored;
  }
  // One-way compatibility for records created by the old demo fallback.
  const raw = localStorage.getItem(LEGACY_PREFIX + key);
  if (!raw) return null;
  const legacy = JSON.parse(raw) as TerrainRecord;
  await putSplitRecord(legacy);
  return (await loadSplitRecord(key)) ?? legacy;
}

export async function listTerrains(): Promise<TerrainListResponse> {
  if (desktop) return desktop.terrain.list();
  const summaries = await dbRequest<TerrainSummary[]>(SUMMARY_STORE, 'readonly', (store) => store.getAll());
  const known = new Set(summaries.map((summary) => summary.key));
  const legacyKeys = await dbRequest<IDBValidKey[]>(LEGACY_STORE, 'readonly', (store) => store.getAllKeys());
  for (const legacyKey of legacyKeys) {
    const key = String(legacyKey);
    if (known.has(key)) continue;
    const record = await dbRequest<TerrainRecord | undefined>(
      LEGACY_STORE, 'readonly', (store) => store.get(key),
    );
    if (!record) continue;
    await putSplitRecord(record);
    summaries.push(toSummary(record));
    known.add(key);
  }
  for (const legacy of legacyList()) if (!summaries.some((s) => s.key === legacy.key)) summaries.push(legacy);
  return summaries;
}

export async function deleteTerrain(key: string): Promise<TerrainDeleteResponse> {
  if (desktop) {
    const result = await desktop.terrain.delete(key);
    if (result.ok) await deleteWeatherPackage(key);
    return result;
  }

  const db = await openTerrainDb();
  await new Promise<void>((resolve, reject) => {
    const tx = db.transaction([SUMMARY_STORE, METADATA_STORE, ASSET_STORE, LEGACY_STORE], 'readwrite');
    for (const store of [SUMMARY_STORE, METADATA_STORE, ASSET_STORE, LEGACY_STORE]) {
      tx.objectStore(store).delete(key);
    }
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error ?? new Error('Unable to delete terrain package'));
    tx.onabort = tx.onerror;
  }).finally(() => db.close());
  localStorage.removeItem(LEGACY_PREFIX + key);
  await deleteWeatherPackage(key);
  return { ok: true };
}

/** Partial-loading facade used by new runtime consumers. Desktop methods will
 * move behind the same contract as its versioned sidecars are introduced. */
export const browserTerrainAssetStore: TerrainAssetStore = {
  listSummaries: listTerrains,
  async loadMetadata(key) {
    if (desktop) {
      const record = await loadTerrain(key);
      if (!record) return null;
      const {
        sampleHeights: _sampleHeights,
        coverGrid: _coverGrid,
        originalCoverGrid: _originalCoverGrid,
        coverBoundarySegments: _coverBoundarySegments,
        coverDisplayGeometry: _coverDisplayGeometry,
        contourSegments: _contourSegments,
        localImagery: _localImagery,
        ...metadata
      } = record;
      return metadata;
    }
    return (await dbRequest<TerrainMetadata | undefined>(
      METADATA_STORE,
      'readonly',
      (store) => store.get(key),
    )) ?? null;
  },
  async loadAssets(key: string, assetMask: TerrainAssetMask) {
    const record = desktop ? await loadTerrain(key) : null;
    const assets = record ? splitRecord(record).assets : await dbRequest<BrowserTerrainAssets | undefined>(
      ASSET_STORE,
      'readonly',
      (store) => store.get(key),
    );
    if (!assets) return null;
    const selected: Partial<RuntimeTerrainPackage> = {};
    if (assetMask.has('elevation')) selected.sampleHeights = assets.sampleHeights;
    if (assetMask.has('cover')) selected.coverGrid = assets.coverGrid;
    if (assetMask.has('originalCover')) selected.originalCoverGrid = assets.originalCoverGrid;
    if (assetMask.has('coverGeometry')) selected.coverBoundarySegments = assets.coverBoundarySegments;
    if (assetMask.has('coverDisplay')) selected.coverDisplayGeometry = assets.coverDisplayGeometry;
    if (assetMask.has('contours')) selected.contourSegments = assets.contourSegments;
    if (assetMask.has('imagery')) selected.localImagery = assets.localImagery;
    return selected;
  },
  async commitAssets(
    key: string,
    expectedRevision: string,
    changedAssets: Partial<RuntimeTerrainPackage>,
  ): Promise<TerrainAssetRevision> {
    const current = await loadTerrain(key);
    if (!current) throw new Error('Terrain package is missing');
    if (current.updatedAt !== expectedRevision) throw new Error('Terrain package revision is stale');
    const terrainRevision = changedAssets.updatedAt ?? new Date().toISOString();
    const next = { ...current, ...changedAssets, key, updatedAt: terrainRevision } as TerrainRecord;
    const result = await saveTerrain(next);
    if (!result.ok) throw new Error(result.error);
    return {
      terrainRevision,
      integrityReceipt: next.packageManifest?.elevationChecksum,
    };
  },
};
