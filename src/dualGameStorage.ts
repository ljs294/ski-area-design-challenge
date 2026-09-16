import type { GameSave } from './types/gameSave';

function openStore(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open('mountain-planner-dual-saves', 1);
    request.onupgradeneeded = () => request.result.createObjectStore('games', { keyPath: 'key' });
    request.onerror = () => reject(request.error);
    request.onsuccess = () => resolve(request.result);
  });
}
/** IndexedDB avoids localStorage's small quota for precise snow and worker state. */
export async function writeDualGame(save: GameSave): Promise<void> {
  const db = await openStore();
  try { await new Promise<void>((resolve, reject) => {
    const tx = db.transaction('games', 'readwrite'); tx.objectStore('games').put(save);
    tx.oncomplete = () => resolve(); tx.onerror = () => reject(tx.error); tx.onabort = () => reject(tx.error);
  }); } finally { db.close(); }
}
export async function readDualGame(key: string): Promise<GameSave | null> {
  if (typeof indexedDB === 'undefined') return null;
  const db = await openStore();
  try { return await new Promise<GameSave | null>((resolve, reject) => {
    const request = db.transaction('games').objectStore('games').get(key);
    request.onsuccess = () => resolve(request.result ?? null); request.onerror = () => reject(request.error);
  }); } finally { db.close(); }
}
export async function deleteDualGame(key: string): Promise<void> {
  if (typeof indexedDB === 'undefined') return;
  const db = await openStore();
  try { await new Promise<void>((resolve, reject) => {
    const tx = db.transaction('games', 'readwrite'); tx.objectStore('games').delete(key);
    tx.oncomplete = () => resolve(); tx.onerror = () => reject(tx.error); tx.onabort = () => reject(tx.error);
  }); } finally { db.close(); }
}
