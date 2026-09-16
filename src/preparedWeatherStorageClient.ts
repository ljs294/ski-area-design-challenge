import { desktop } from './desktopBridge';
import { decodePreparedWeather, encodePreparedWeather } from './weather/preparedWeatherCodec';
import { isPreparedWeatherIdentity, preparedWeatherIdentityKey,
  type PreparedAnnualWeather, type PreparedWeatherIdentity } from './weather/preparedWeatherModel';

const DB_NAME = 'mountain-planner-prepared-weather-v1';
const STORE_NAME = 'prepared-annual-weather';
const DB_VERSION = 1;

function openDatabase(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = globalThis.indexedDB.open(DB_NAME, DB_VERSION);
    request.onupgradeneeded = () => {
      if (!request.result.objectStoreNames.contains(STORE_NAME)) request.result.createObjectStore(STORE_NAME);
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error ?? new Error('Unable to open prepared weather storage.'));
  });
}

function asBytes(value: unknown): Uint8Array | null {
  if (value instanceof Uint8Array) return value;
  if (value instanceof ArrayBuffer) return new Uint8Array(value);
  return null;
}

export async function readPreparedWeather(identity: PreparedWeatherIdentity): Promise<PreparedAnnualWeather | null> {
  if (!isPreparedWeatherIdentity(identity)) return null;
  try {
    if (desktop) return await decodePreparedWeather(await desktop.preparedWeather.read(identity) ?? new Uint8Array(), identity);
    if (typeof globalThis.indexedDB === 'undefined') return null;
    const db = await openDatabase();
    const value = await new Promise<unknown>((resolve, reject) => {
      const transaction = db.transaction(STORE_NAME, 'readonly');
      const request = transaction.objectStore(STORE_NAME).get(preparedWeatherIdentityKey(identity));
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    });
    db.close();
    const bytes = asBytes(value);
    return bytes ? await decodePreparedWeather(bytes, identity) : null;
  } catch {
    return null;
  }
}

export async function writePreparedWeather(prepared: PreparedAnnualWeather): Promise<boolean> {
  if (!prepared || !isPreparedWeatherIdentity(prepared.identity)) return false;
  try {
    const bytes = await encodePreparedWeather(prepared);
    return writePreparedWeatherBytes(prepared.identity, bytes);
  } catch {
    return false;
  }
}

export async function writePreparedWeatherBytes(identity: PreparedWeatherIdentity, bytes: Uint8Array): Promise<boolean> {
  if (!isPreparedWeatherIdentity(identity) || !(bytes instanceof Uint8Array) || !bytes.byteLength) return false;
  try {
    if (desktop) return await desktop.preparedWeather.write(identity, bytes);
    if (typeof globalThis.indexedDB === 'undefined') return false;
    const db = await openDatabase();
    await new Promise<void>((resolve, reject) => {
      const transaction = db.transaction(STORE_NAME, 'readwrite');
      transaction.objectStore(STORE_NAME).put(bytes, preparedWeatherIdentityKey(identity));
      transaction.oncomplete = () => resolve();
      transaction.onerror = () => reject(transaction.error);
      transaction.onabort = () => reject(transaction.error ?? new Error('Prepared weather write aborted.'));
    });
    db.close();
    return true;
  } catch { return false; }
}
