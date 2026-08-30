import type { WeatherLabResultV2 } from '../../weather-engine/src/index.ts';

const DATABASE_NAME = 'mountain-planner-weather-lab';
const DATABASE_VERSION = 1;
const STORE_NAME = 'pinned-baselines';
const PIN_KEY = 'active';

export interface PinnedWeatherBaselineV1 {
  version: 1;
  pinnedAt: string;
  result: WeatherLabResultV2;
}

function database(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DATABASE_NAME, DATABASE_VERSION);
    request.onupgradeneeded = () => {
      if (!request.result.objectStoreNames.contains(STORE_NAME)) request.result.createObjectStore(STORE_NAME);
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error ?? new Error('Unable to open pinned-baseline storage.'));
  });
}

async function transaction<T>(mode: IDBTransactionMode, operation: (store: IDBObjectStore) => IDBRequest<T>): Promise<T> {
  const db = await database();
  try {
    return await new Promise<T>((resolve, reject) => {
      const tx = db.transaction(STORE_NAME, mode);
      const request = operation(tx.objectStore(STORE_NAME));
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error ?? new Error('Pinned-baseline storage failed.'));
      tx.onabort = () => reject(tx.error ?? new Error('Pinned-baseline transaction was aborted.'));
    });
  } finally {
    db.close();
  }
}

export async function loadPinnedBaseline(): Promise<PinnedWeatherBaselineV1 | null> {
  if (typeof indexedDB === 'undefined') return null;
  const value = await transaction<unknown>('readonly', (store) => store.get(PIN_KEY));
  if (value == null || typeof value !== 'object') return null;
  const record = value as Partial<PinnedWeatherBaselineV1>;
  return record.version === 1 && typeof record.pinnedAt === 'string' && record.result?.version === 2
    ? record as PinnedWeatherBaselineV1 : null;
}

export async function storePinnedBaseline(result: WeatherLabResultV2): Promise<PinnedWeatherBaselineV1> {
  const record: PinnedWeatherBaselineV1 = { version: 1, pinnedAt: new Date().toISOString(), result };
  await transaction<IDBValidKey>('readwrite', (store) => store.put(record, PIN_KEY));
  return record;
}

export async function deletePinnedBaseline(): Promise<void> {
  if (typeof indexedDB === 'undefined') return;
  await transaction<undefined>('readwrite', (store) => store.delete(PIN_KEY));
}

