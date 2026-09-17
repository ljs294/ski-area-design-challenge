import type { DesignStorageAdapter } from './designSaveRepository';
import type { DesignSaveDocument, DesignSaveSummary, DesignTerrainGeneration } from './types/designSave';

const DB_NAME = 'mountain-planner-three-design';
const DB_VERSION = 1;
const TERRAIN_STORE = 'terrain-generations';
const MANIFEST_STORE = 'design-manifests';
const HEAD_STORE = 'design-heads';
const SUMMARY_STORE = 'design-summaries';

interface DesignHead { saveKey: string; manifestId: string }

function openDatabase(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, DB_VERSION);
    request.onupgradeneeded = () => {
      const database = request.result;
      if (!database.objectStoreNames.contains(TERRAIN_STORE)) {
        database.createObjectStore(TERRAIN_STORE, { keyPath: 'generationId' });
      }
      if (!database.objectStoreNames.contains(MANIFEST_STORE)) {
        database.createObjectStore(MANIFEST_STORE, { keyPath: 'manifestId' });
      }
      if (!database.objectStoreNames.contains(HEAD_STORE)) {
        database.createObjectStore(HEAD_STORE, { keyPath: 'saveKey' });
      }
      if (!database.objectStoreNames.contains(SUMMARY_STORE)) {
        database.createObjectStore(SUMMARY_STORE, { keyPath: 'key' });
      }
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error ?? new Error('Unable to open design storage.'));
  });
}

async function oneStore<T>(storeName: string, mode: IDBTransactionMode,
  operation: (store: IDBObjectStore) => IDBRequest<T>): Promise<T> {
  const database = await openDatabase();
  try {
    return await new Promise<T>((resolve, reject) => {
      const transaction = database.transaction(storeName, mode);
      const request = operation(transaction.objectStore(storeName));
      let result: T;
      request.onsuccess = () => { result = request.result; };
      request.onerror = () => reject(request.error ?? new Error('Design storage operation failed.'));
      transaction.oncomplete = () => resolve(result);
      transaction.onerror = () => reject(transaction.error ?? new Error('Design storage transaction failed.'));
      transaction.onabort = transaction.onerror;
    });
  } finally { database.close(); }
}

export class BrowserDesignStorage implements DesignStorageAdapter {
  async readCurrentManifest(saveKey: string): Promise<DesignSaveDocument | null> {
    const head = await oneStore(HEAD_STORE, 'readonly', (store) => store.get(saveKey)) as DesignHead | undefined;
    if (!head) return null;
    return (await oneStore(MANIFEST_STORE, 'readonly', (store) => store.get(head.manifestId)) as
      DesignSaveDocument | undefined) ?? null;
  }

  async readTerrainGeneration(generationId: string): Promise<DesignTerrainGeneration | null> {
    return (await oneStore(TERRAIN_STORE, 'readonly', (store) => store.get(generationId)) as
      DesignTerrainGeneration | undefined) ?? null;
  }

  async writeTerrainGeneration(generation: DesignTerrainGeneration): Promise<void> {
    await oneStore(TERRAIN_STORE, 'readwrite', (store) => store.add(generation));
  }

  async publishManifestAndHead(manifest: DesignSaveDocument): Promise<void> {
    const database = await openDatabase();
    try {
      await new Promise<void>((resolve, reject) => {
        const transaction = database.transaction([MANIFEST_STORE, HEAD_STORE], 'readwrite');
        transaction.objectStore(MANIFEST_STORE).add(manifest);
        transaction.objectStore(HEAD_STORE).put({ saveKey: manifest.key, manifestId: manifest.manifestId });
        transaction.oncomplete = () => resolve();
        transaction.onerror = () => reject(transaction.error ?? new Error('Unable to publish the design save.'));
        transaction.onabort = transaction.onerror;
      });
    } finally { database.close(); }
  }

  async writeSummary(summary: DesignSaveSummary): Promise<void> {
    await oneStore(SUMMARY_STORE, 'readwrite', (store) => store.put(summary));
  }

  async listSummaries(): Promise<DesignSaveSummary[]> {
    const database = await openDatabase();
    try {
      const transaction = database.transaction([SUMMARY_STORE, HEAD_STORE, MANIFEST_STORE], 'readonly');
      const summariesRequest = transaction.objectStore(SUMMARY_STORE).getAll() as IDBRequest<DesignSaveSummary[]>;
      const headsRequest = transaction.objectStore(HEAD_STORE).getAll() as IDBRequest<DesignHead[]>;
      const manifestsRequest = transaction.objectStore(MANIFEST_STORE).getAll() as IDBRequest<DesignSaveDocument[]>;
      await new Promise<void>((resolve, reject) => {
        transaction.oncomplete = () => resolve();
        transaction.onerror = () => reject(transaction.error ?? new Error('Unable to list design saves.'));
      });
      const summaries = new Map(summariesRequest.result.map((summary) => [summary.key, summary]));
      const manifests = new Map(manifestsRequest.result.map((manifest) => [manifest.manifestId, manifest]));
      for (const head of headsRequest.result) {
        const save = manifests.get(head.manifestId);
        if (save) summaries.set(save.key, { key: save.key, name: save.name,
          createdAt: save.createdAt, updatedAt: save.updatedAt,
          revisions: save.revisions, terrain: save.terrain });
      }
      return [...summaries.values()];
    } finally { database.close(); }
  }
}
