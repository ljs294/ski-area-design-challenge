import { desktop } from './desktopBridge';
import { isWeatherDataPackage, type WeatherDataPackage } from './weather/weatherModel';

const DB_NAME = 'mountain-planner-weather';
const STORE = 'packages';

export function validateWeatherPackage(value: unknown): value is WeatherDataPackage {
  return isWeatherDataPackage(value);
}

function openDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, 1);
    request.onupgradeneeded = () => {
      if (!request.result.objectStoreNames.contains(STORE)) request.result.createObjectStore(STORE, { keyPath: 'terrainKey' });
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error ?? new Error('Unable to open offline weather storage.'));
  });
}

async function browserLoad(terrainKey: string): Promise<WeatherDataPackage | null> {
  const db = await openDb();
  return new Promise<WeatherDataPackage | null>((resolve, reject) => {
    const tx = db.transaction(STORE, 'readonly');
    const request = tx.objectStore(STORE).get(terrainKey) as IDBRequest<WeatherDataPackage | undefined>;
    tx.oncomplete = () => { db.close(); resolve(validateWeatherPackage(request.result) ? request.result : null); };
    tx.onerror = () => { db.close(); reject(tx.error ?? new Error('Unable to read offline weather package.')); };
  });
}

async function browserSave(weatherPackage: WeatherDataPackage): Promise<void> {
  const db = await openDb();
  await new Promise<void>((resolve, reject) => {
    const tx = db.transaction(STORE, 'readwrite');
    tx.objectStore(STORE).put({ ...weatherPackage, terrainKey: weatherPackage.manifest.terrainKey });
    tx.oncomplete = () => { db.close(); resolve(); };
    tx.onerror = () => { db.close(); reject(tx.error ?? new Error('Unable to save offline weather package.')); };
  });
}

async function browserDelete(terrainKey: string): Promise<void> {
  const db = await openDb();
  await new Promise<void>((resolve, reject) => {
    const tx = db.transaction(STORE, 'readwrite');
    tx.objectStore(STORE).delete(terrainKey);
    tx.oncomplete = () => { db.close(); resolve(); };
    tx.onerror = () => { db.close(); reject(tx.error ?? new Error('Unable to remove offline weather package.')); };
  });
}

export async function loadWeatherPackage(terrainKey: string): Promise<WeatherDataPackage | null> {
  if (desktop) return desktop.weather.load(terrainKey);
  return browserLoad(terrainKey);
}

export async function saveWeatherPackage(weatherPackage: WeatherDataPackage): Promise<void> {
  if (!validateWeatherPackage(weatherPackage)) throw new Error('Offline weather package is invalid or incomplete.');
  if (desktop) {
    const result = await desktop.weather.save(weatherPackage);
    if (!result.ok) throw new Error(result.error);
    return;
  }
  await browserSave(weatherPackage);
}

export async function deleteWeatherPackage(terrainKey: string): Promise<void> {
  if (desktop) {
    const result = await desktop.weather.delete(terrainKey);
    if (!result.ok) throw new Error(result.error);
    return;
  }
  await browserDelete(terrainKey);
}
