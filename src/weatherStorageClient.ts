import { desktop } from './desktopBridge';
import type {
  WeatherPackageStorageInstall,
  WeatherPackageStorageManifest,
  WeatherSourceChunkDescriptor,
  WeatherStorageChunk,
  WeatherStorageChunkDescriptor,
} from './ipcContract';
import { isWeatherDataPackage, type HistoricalWeatherYear, type WeatherDataPackage } from './weather/weatherModel';

const DB_NAME = 'mountain-planner-weather';
const DB_VERSION = 3;
/** v1 store retained only long enough to migrate an installed legacy package. */
const LEGACY_PACKAGES_STORE = 'packages';
const MANIFESTS_STORE = 'manifests';
const CHUNKS_STORE = 'chunks';
const ACTIVE_STORE = 'active';
const GAME_SAVE_INDEX_KEY = 'gamesave-index';
const GAME_SAVE_PREFIX = 'gamesave:';

type StorageRecord = Record<string, unknown>;

interface ActiveWeatherPointer {
  terrainKey: string;
  terrainBinding: string;
  contentHash: string;
  updatedAt: string;
}

interface BrowserChunkRecord {
  id: string;
  contentHash: string;
  key: string;
  data: Uint8Array;
}

export type WeatherPackageLoadStatus = 'ready' | 'not-found' | 'binding-mismatch' | 'corrupt';

export type WeatherPackageLoadResult =
  | {
    status: 'ready';
    weatherPackage: WeatherDataPackage;
    storageManifest?: WeatherPackageStorageManifest;
  }
  | { status: Exclude<WeatherPackageLoadStatus, 'ready'>; error?: string };

function isRecord(value: unknown): value is StorageRecord {
  return !!value && typeof value === 'object' && !Array.isArray(value);
}

function isChunkDescriptor(value: unknown): value is WeatherStorageChunkDescriptor {
  return isRecord(value) && typeof value.key === 'string' && value.key.length > 0 &&
    (value.encoding === 'gzip-json' || value.encoding === 'binary') &&
    Number.isSafeInteger(value.byteLength) && (value.byteLength as number) >= 0 &&
    typeof value.checksum === 'string' && /^[a-f0-9]{64}$/i.test(value.checksum as string);
}

function isSourceManifest(value: unknown): value is WeatherDataPackage['manifest'] {
  return isRecord(value) && typeof value.terrainKey === 'string' &&
    typeof value.terrainBinding === 'string' && typeof value.contentHash === 'string';
}

function isSourceChunkDescriptor(value: unknown): value is WeatherSourceChunkDescriptor {
  return isRecord(value) && typeof value.id === 'string' && value.id.length > 0 &&
    Number.isInteger(value.year) && typeof value.startsAt === 'string' && typeof value.endsAt === 'string' &&
    (value.encoding === 'gzip' || value.encoding === 'identity') && value.format === 'weather-hour-v2' &&
    typeof value.checksumSha256 === 'string' && /^[a-f0-9]{64}$/i.test(value.checksumSha256 as string) &&
    Number.isSafeInteger(value.byteLength) && (value.byteLength as number) >= 0 &&
    Number.isSafeInteger(value.uncompressedByteLength) && (value.uncompressedByteLength as number) >= 0 &&
    Number.isSafeInteger(value.recordCount) && (value.recordCount as number) >= 0 &&
    'fieldProvenance' in value;
}

export function isWeatherPackageStorageManifest(value: unknown): value is WeatherPackageStorageManifest {
  return isRecord(value) && value.storageSchemaVersion === 2 &&
    typeof value.contentHash === 'string' && value.contentHash.length > 0 &&
    typeof value.terrainKey === 'string' && value.terrainKey.length > 0 &&
    typeof value.terrainBinding === 'string' && value.terrainBinding.length > 0 &&
    typeof value.payloadHash === 'string' && /^[a-f0-9]{64}$/i.test(value.payloadHash as string) &&
    (value.payloadFormat === 'legacy-weather-data-package-v1' ||
      value.payloadFormat === 'weather-package-chunks-v1') &&
    isSourceManifest(value.sourceManifest) && Array.isArray(value.chunks) &&
    value.chunks.every(isChunkDescriptor) &&
    (value.sourceChunks === undefined || (Array.isArray(value.sourceChunks) && value.sourceChunks.every(isSourceChunkDescriptor))) &&
    value.complete === true &&
    typeof value.createdAt === 'string';
}

function toBytes(value: unknown): Uint8Array | null {
  if (value instanceof Uint8Array) return new Uint8Array(value);
  if (value instanceof ArrayBuffer) return new Uint8Array(value.slice(0));
  return null;
}

function base64ToBytes(value: string): Uint8Array {
  if (typeof atob !== 'function') throw new Error('This runtime cannot decode binary weather chunks.');
  try {
    const binary = atob(value);
    const bytes = new Uint8Array(binary.length);
    for (let index = 0; index < binary.length; index += 1) bytes[index] = binary.charCodeAt(index);
    return bytes;
  } catch {
    throw new Error('Weather service supplied an invalid binary chunk.');
  }
}

function bytesToBase64(bytes: Uint8Array): string {
  if (typeof btoa !== 'function') throw new Error('This runtime cannot encode binary weather chunks.');
  let binary = '';
  for (let offset = 0; offset < bytes.length; offset += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(offset, Math.min(offset + 0x8000, bytes.length)));
  }
  return btoa(binary);
}

interface ProviderChunkTransport {
  descriptor: WeatherSourceChunkDescriptor;
  dataBase64: string;
}

function providerChunkTransport(value: unknown): ProviderChunkTransport[] | null {
  if (!isRecord(value) || !Array.isArray(value.chunks) || value.chunks.length === 0) return null;
  const chunks: ProviderChunkTransport[] = [];
  for (const candidate of value.chunks) {
    if (!isRecord(candidate) || !isSourceChunkDescriptor(candidate.descriptor) ||
      typeof candidate.dataBase64 !== 'string') return null;
    chunks.push({ descriptor: candidate.descriptor, dataBase64: candidate.dataBase64 });
  }
  return chunks;
}

function chunkPayloadIdentity(
  sourceManifest: WeatherDataPackage['manifest'],
  sourceChunks: readonly WeatherSourceChunkDescriptor[],
): string {
  return JSON.stringify({ sourceManifest, sourceChunks });
}

function chunkId(contentHash: string, key: string): string {
  return `${contentHash}\u0000${key}`;
}

function descriptorsMatch(
  left: readonly WeatherStorageChunkDescriptor[],
  right: readonly WeatherStorageChunkDescriptor[],
): boolean {
  if (left.length !== right.length) return false;
  return left.every((descriptor, index) => {
    const other = right[index];
    return descriptor.key === other?.key && descriptor.encoding === other.encoding &&
      descriptor.byteLength === other.byteLength && descriptor.checksum === other.checksum;
  });
}

async function sha256Hex(bytes: Uint8Array): Promise<string> {
  if (!globalThis.crypto?.subtle) throw new Error('This runtime cannot verify offline weather package checksums.');
  const copy = new Uint8Array(bytes);
  const digest = await globalThis.crypto.subtle.digest('SHA-256', copy);
  return [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, '0')).join('');
}

async function gzip(bytes: Uint8Array): Promise<Uint8Array> {
  if (typeof CompressionStream === 'undefined') {
    throw new Error('This browser cannot create compressed offline weather packages.');
  }
  const stream = new CompressionStream('gzip');
  const writer = stream.writable.getWriter();
  await writer.write(ownedBytes(bytes));
  await writer.close();
  return new Uint8Array(await new Response(stream.readable).arrayBuffer());
}

async function gunzip(bytes: Uint8Array): Promise<Uint8Array> {
  if (typeof DecompressionStream === 'undefined') {
    throw new Error('This browser cannot read compressed offline weather packages.');
  }
  const stream = new DecompressionStream('gzip');
  const writer = stream.writable.getWriter();
  await writer.write(ownedBytes(bytes));
  await writer.close();
  return new Uint8Array(await new Response(stream.readable).arrayBuffer());
}

function ownedBytes(bytes: Uint8Array): Uint8Array<ArrayBuffer> {
  const copy = new Uint8Array(bytes.byteLength);
  copy.set(bytes);
  return copy;
}

async function jsonChunk(key: string, value: unknown): Promise<WeatherStorageChunk> {
  const json = JSON.stringify(value);
  if (typeof json !== 'string') throw new Error(`Unable to serialize offline weather chunk ${key}.`);
  const data = await gzip(new TextEncoder().encode(json));
  return {
    key,
    encoding: 'gzip-json',
    byteLength: data.byteLength,
    checksum: await sha256Hex(data),
    data,
  };
}

async function parseJsonChunk(chunk: WeatherStorageChunk): Promise<unknown> {
  if (chunk.encoding !== 'gzip-json') throw new Error(`Weather chunk ${chunk.key} is not JSON.`);
  try {
    return JSON.parse(new TextDecoder().decode(await gunzip(chunk.data)));
  } catch {
    throw new Error(`Weather chunk ${chunk.key} could not be decoded.`);
  }
}

/** Legacy public validation remains stable for callers receiving v1 service responses. */
export function validateWeatherPackage(value: unknown): value is WeatherDataPackage {
  return isWeatherDataPackage(value);
}

/**
 * Verify structural consistency and every chunk checksum before an artifact is
 * made visible through an active terrain pointer.
 */
export async function validateWeatherPackageStorageInstall(
  install: WeatherPackageStorageInstall,
): Promise<void> {
  if (!isWeatherPackageStorageManifest(install?.manifest)) {
    throw new Error('Offline weather storage manifest is invalid.');
  }
  if (!Array.isArray(install.chunks) || install.chunks.length !== install.manifest.chunks.length) {
    throw new Error('Offline weather package chunks are incomplete.');
  }
  const keys = new Set<string>();
  for (const descriptor of install.manifest.chunks) {
    if (keys.has(descriptor.key)) throw new Error(`Offline weather package repeats chunk ${descriptor.key}.`);
    keys.add(descriptor.key);
  }
  const chunksByKey = new Map<string, WeatherStorageChunk>();
  for (const candidate of install.chunks) {
    const data = toBytes(candidate?.data);
    if (!data || !isChunkDescriptor(candidate)) {
      throw new Error('Offline weather package has an invalid binary chunk.');
    }
    if (chunksByKey.has(candidate.key)) throw new Error(`Offline weather package repeats chunk ${candidate.key}.`);
    chunksByKey.set(candidate.key, { ...candidate, data });
  }
  for (const descriptor of install.manifest.chunks) {
    const chunk = chunksByKey.get(descriptor.key);
    if (!chunk || chunk.encoding !== descriptor.encoding || chunk.byteLength !== descriptor.byteLength ||
      chunk.checksum !== descriptor.checksum || chunk.data.byteLength !== descriptor.byteLength) {
      throw new Error(`Offline weather package chunk ${descriptor.key} does not match its manifest.`);
    }
    if ((await sha256Hex(chunk.data)).toLowerCase() !== descriptor.checksum.toLowerCase()) {
      throw new Error(`Offline weather package chunk ${descriptor.key} failed checksum validation.`);
    }
  }
  const manifest = install.manifest;
  if (manifest.sourceManifest.terrainKey !== manifest.terrainKey ||
    manifest.sourceManifest.terrainBinding !== manifest.terrainBinding ||
    manifest.sourceManifest.contentHash !== manifest.contentHash) {
    throw new Error('Offline weather package binding does not match its source manifest.');
  }
  if (manifest.payloadFormat === 'weather-package-chunks-v1') {
    if (!manifest.sourceChunks || manifest.sourceChunks.length !== manifest.chunks.length) {
      throw new Error('Offline weather package is missing source chunk metadata.');
    }
    const sourceChunks = new Map(manifest.sourceChunks.map((descriptor) => [descriptor.id, descriptor]));
    if (sourceChunks.size !== manifest.sourceChunks.length) {
      throw new Error('Offline weather package repeats a source chunk identifier.');
    }
    for (const descriptor of manifest.chunks) {
      const source = sourceChunks.get(descriptor.key);
      if (!source || descriptor.encoding !== 'binary' || source.byteLength !== descriptor.byteLength ||
        source.checksumSha256.toLowerCase() !== descriptor.checksum.toLowerCase()) {
        throw new Error(`Offline weather package source chunk ${descriptor.key} does not match storage metadata.`);
      }
    }
  }
}

async function createNativeChunkInstall(
  weatherPackage: WeatherDataPackage,
): Promise<WeatherPackageStorageInstall | null> {
  const sourceManifest = weatherPackage.manifest;
  const transport = providerChunkTransport(weatherPackage);
  if (!validateWeatherPackage(weatherPackage) || !isSourceManifest(sourceManifest) || !transport) return null;
  const chunks: WeatherStorageChunk[] = transport.map(({ descriptor, dataBase64 }) => {
    const data = base64ToBytes(dataBase64);
    return {
      key: descriptor.id,
      encoding: 'binary',
      byteLength: data.byteLength,
      checksum: descriptor.checksumSha256,
      data,
    };
  });
  const sourceChunks = transport.map(({ descriptor }) => descriptor);
  const manifest: WeatherPackageStorageManifest = {
    storageSchemaVersion: 2,
    contentHash: sourceManifest.contentHash,
    terrainKey: sourceManifest.terrainKey,
    terrainBinding: sourceManifest.terrainBinding,
    payloadHash: await sha256Hex(new TextEncoder().encode(chunkPayloadIdentity(sourceManifest, sourceChunks))),
    payloadFormat: 'weather-package-chunks-v1',
    sourceManifest,
    sourceChunks,
    chunks: chunks.map(({ data: _data, ...descriptor }) => descriptor),
    complete: true,
    createdAt: typeof sourceManifest.createdAt === 'string' ? sourceManifest.createdAt : new Date().toISOString(),
  };
  const install = { manifest, chunks };
  await validateWeatherPackageStorageInstall(install);
  return install;
}

/**
 * Encode an existing WeatherDataPackage as immutable gzip JSON chunks. The
 * shell and every historical year are separate chunks so corruption and future
 * lazy-year loading are bounded to one calendar year.
 */
export async function createWeatherPackageStorageInstall(
  weatherPackage: WeatherDataPackage,
): Promise<WeatherPackageStorageInstall> {
  const nativeInstall = await createNativeChunkInstall(weatherPackage);
  if (nativeInstall) return nativeInstall;
  if (!validateWeatherPackage(weatherPackage)) throw new Error('Offline weather package is invalid or incomplete.');
  const historicalYears = weatherPackage.historicalYears;
  if (!historicalYears) throw new Error('Offline weather package has no historical year cache.');
  const years = [...historicalYears].sort((left, right) => left.year - right.year);
  const seenYears = new Set<number>();
  for (const year of years) {
    if (seenYears.has(year.year)) throw new Error(`Offline weather package repeats historical year ${year.year}.`);
    seenYears.add(year.year);
  }
  const shell = Object.fromEntries(Object.entries(weatherPackage).filter(([key]) => key !== 'historicalYears'));
  const chunks = await Promise.all([
    jsonChunk('package-shell', shell),
    ...years.map((year) => jsonChunk(`historical-year-${year.year}`, year)),
  ]);
  const packageJson = JSON.stringify(weatherPackage);
  const manifest: WeatherPackageStorageManifest = {
    storageSchemaVersion: 2,
    contentHash: weatherPackage.manifest.contentHash,
    terrainKey: weatherPackage.manifest.terrainKey,
    terrainBinding: weatherPackage.manifest.terrainBinding,
    payloadHash: await sha256Hex(new TextEncoder().encode(packageJson)),
    payloadFormat: 'legacy-weather-data-package-v1',
    sourceManifest: weatherPackage.manifest,
    chunks: chunks.map(({ data: _data, ...descriptor }) => descriptor),
    complete: true,
    createdAt: weatherPackage.manifest.createdAt,
  };
  const install = { manifest, chunks };
  await validateWeatherPackageStorageInstall(install);
  return install;
}

/** Decode a v1-compatible artifact after checksum validation. */
export async function decodeWeatherPackageStorageInstall(
  install: WeatherPackageStorageInstall,
): Promise<WeatherDataPackage> {
  await validateWeatherPackageStorageInstall(install);
  if (install.manifest.payloadFormat === 'weather-package-chunks-v1') {
    const sourceChunks = install.manifest.sourceChunks;
    if (!sourceChunks) throw new Error('Offline weather package is missing source chunk metadata.');
    const chunksByKey = new Map(install.chunks.map((chunk) => [chunk.key, chunk]));
    const weatherPackage = {
      manifest: install.manifest.sourceManifest,
      chunks: sourceChunks.map((descriptor) => {
        const chunk = chunksByKey.get(descriptor.id);
        if (!chunk) throw new Error(`Offline weather package chunk ${descriptor.id} is missing.`);
        return { descriptor, dataBase64: bytesToBase64(chunk.data) };
      }),
      historicalYears: [],
    } as unknown;
    if (!validateWeatherPackage(weatherPackage)) throw new Error('Offline weather package payload is invalid.');
    const payloadHash = await sha256Hex(new TextEncoder().encode(
      chunkPayloadIdentity(install.manifest.sourceManifest, sourceChunks),
    ));
    if (payloadHash.toLowerCase() !== install.manifest.payloadHash.toLowerCase()) {
      throw new Error('Offline weather package payload failed checksum validation.');
    }
    return weatherPackage;
  }
  if (install.manifest.payloadFormat !== 'legacy-weather-data-package-v1') throw new Error('Unsupported weather package format.');
  const chunks = new Map(install.chunks.map((chunk) => [chunk.key, chunk]));
  const shellChunk = chunks.get('package-shell');
  if (!shellChunk) throw new Error('Offline weather package has no package shell.');
  const shell = await parseJsonChunk(shellChunk);
  if (!isRecord(shell)) throw new Error('Offline weather package shell is invalid.');
  const years: HistoricalWeatherYear[] = [];
  for (const descriptor of install.manifest.chunks) {
    if (!descriptor.key.startsWith('historical-year-')) continue;
    const decoded = await parseJsonChunk(chunks.get(descriptor.key)!);
    if (!isRecord(decoded) || !Number.isInteger(decoded.year) || !Array.isArray(decoded.hours)) {
      throw new Error(`Offline weather package historical chunk ${descriptor.key} is invalid.`);
    }
    years.push(decoded as unknown as HistoricalWeatherYear);
  }
  years.sort((left, right) => left.year - right.year);
  const weatherPackage = { ...shell, historicalYears: years } as unknown;
  if (!validateWeatherPackage(weatherPackage)) throw new Error('Offline weather package payload is invalid.');
  if (weatherPackage.manifest.contentHash !== install.manifest.contentHash ||
    weatherPackage.manifest.terrainKey !== install.manifest.terrainKey ||
    weatherPackage.manifest.terrainBinding !== install.manifest.terrainBinding) {
    throw new Error('Offline weather package payload does not match its storage manifest.');
  }
  const actualPayloadHash = await sha256Hex(new TextEncoder().encode(JSON.stringify(weatherPackage)));
  if (actualPayloadHash.toLowerCase() !== install.manifest.payloadHash.toLowerCase()) {
    throw new Error('Offline weather package payload failed checksum validation.');
  }
  return weatherPackage;
}

function browserAvailable(): boolean {
  return typeof indexedDB !== 'undefined';
}

function openDb(): Promise<IDBDatabase> {
  if (!browserAvailable()) return Promise.reject(new Error('IndexedDB is unavailable for offline weather storage.'));
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, DB_VERSION);
    request.onupgradeneeded = () => {
      const database = request.result;
      if (!database.objectStoreNames.contains(LEGACY_PACKAGES_STORE)) {
        database.createObjectStore(LEGACY_PACKAGES_STORE, { keyPath: 'terrainKey' });
      }
      if (!database.objectStoreNames.contains(MANIFESTS_STORE)) {
        database.createObjectStore(MANIFESTS_STORE, { keyPath: 'contentHash' });
      }
      if (!database.objectStoreNames.contains(CHUNKS_STORE)) {
        const chunks = database.createObjectStore(CHUNKS_STORE, { keyPath: 'id' });
        chunks.createIndex('contentHash', 'contentHash', { unique: false });
      } else {
        const chunks = request.transaction?.objectStore(CHUNKS_STORE);
        if (chunks && !chunks.indexNames.contains('contentHash')) {
          chunks.createIndex('contentHash', 'contentHash', { unique: false });
        }
      }
      if (!database.objectStoreNames.contains(ACTIVE_STORE)) {
        database.createObjectStore(ACTIVE_STORE, { keyPath: 'terrainKey' });
      }
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error ?? new Error('Unable to open offline weather storage.'));
  });
}

function closeAfter<T>(database: IDBDatabase, operation: Promise<T>): Promise<T> {
  return operation.finally(() => database.close());
}

async function browserReadManifest(contentHash: string): Promise<WeatherPackageStorageManifest | null> {
  const database = await openDb();
  return closeAfter(database, new Promise((resolve, reject) => {
    const transaction = database.transaction(MANIFESTS_STORE, 'readonly');
    const request = transaction.objectStore(MANIFESTS_STORE).get(contentHash);
    transaction.oncomplete = () => resolve(isWeatherPackageStorageManifest(request.result) ? request.result : null);
    transaction.onerror = () => reject(transaction.error ?? new Error('Unable to read offline weather manifest.'));
  }));
}

async function browserReadPointer(terrainKey: string): Promise<ActiveWeatherPointer | null> {
  const database = await openDb();
  return closeAfter(database, new Promise((resolve, reject) => {
    const transaction = database.transaction(ACTIVE_STORE, 'readonly');
    const request = transaction.objectStore(ACTIVE_STORE).get(terrainKey);
    transaction.oncomplete = () => {
      const pointer = request.result;
      resolve(isRecord(pointer) && pointer.terrainKey === terrainKey &&
        typeof pointer.terrainBinding === 'string' && typeof pointer.contentHash === 'string' &&
        typeof pointer.updatedAt === 'string' ? pointer as unknown as ActiveWeatherPointer : null);
    };
    transaction.onerror = () => reject(transaction.error ?? new Error('Unable to read offline weather pointer.'));
  }));
}

async function browserReadInstall(contentHash: string): Promise<WeatherPackageStorageInstall | null> {
  const database = await openDb();
  const result = await closeAfter(database, new Promise<{
    manifest: unknown;
    chunks: BrowserChunkRecord[];
  }>((resolve, reject) => {
    const transaction = database.transaction([MANIFESTS_STORE, CHUNKS_STORE], 'readonly');
    const manifestRequest = transaction.objectStore(MANIFESTS_STORE).get(contentHash);
    const chunks = transaction.objectStore(CHUNKS_STORE).index('contentHash').getAll(contentHash) as IDBRequest<BrowserChunkRecord[]>;
    transaction.oncomplete = () => resolve({ manifest: manifestRequest.result, chunks: chunks.result ?? [] });
    transaction.onerror = () => reject(transaction.error ?? new Error('Unable to read offline weather package chunks.'));
  }));
  if (!isWeatherPackageStorageManifest(result.manifest)) return null;
  const chunksByKey = new Map(result.chunks.map((chunk) => [chunk.key, chunk]));
  const chunks: WeatherStorageChunk[] = [];
  for (const descriptor of result.manifest.chunks) {
    const stored = chunksByKey.get(descriptor.key);
    const data = stored && toBytes(stored.data);
    if (!data) throw new Error(`Offline weather package chunk ${descriptor.key} is missing.`);
    chunks.push({ ...descriptor, data });
  }
  const install = { manifest: result.manifest, chunks };
  await validateWeatherPackageStorageInstall(install);
  return install;
}

async function browserInstall(install: WeatherPackageStorageInstall): Promise<void> {
  await validateWeatherPackageStorageInstall(install);
  const existing = await browserReadManifest(install.manifest.contentHash);
  if (existing && (existing.payloadHash !== install.manifest.payloadHash ||
    !descriptorsMatch(existing.chunks, install.manifest.chunks))) {
    throw new Error('A different offline weather package already uses this content hash.');
  }
  const database = await openDb();
  let priorContentHash: string | null = null;
  await closeAfter(database, new Promise<void>((resolve, reject) => {
    const transaction = database.transaction([MANIFESTS_STORE, CHUNKS_STORE, ACTIVE_STORE], 'readwrite');
    const active = transaction.objectStore(ACTIVE_STORE);
    const prior = active.get(install.manifest.terrainKey);
    prior.onsuccess = () => {
      const pointer = prior.result;
      priorContentHash = isRecord(pointer) && typeof pointer.contentHash === 'string' ? pointer.contentHash : null;
    };
    const chunkStore = transaction.objectStore(CHUNKS_STORE);
    for (const chunk of install.chunks) {
      chunkStore.put({
        id: chunkId(install.manifest.contentHash, chunk.key),
        contentHash: install.manifest.contentHash,
        key: chunk.key,
        data: new Uint8Array(chunk.data),
      } satisfies BrowserChunkRecord);
    }
    // The manifest is written only after all chunks are queued, then the active
    // pointer makes the package visible as the final transaction operation.
    transaction.objectStore(MANIFESTS_STORE).put(install.manifest);
    active.put({
      terrainKey: install.manifest.terrainKey,
      terrainBinding: install.manifest.terrainBinding,
      contentHash: install.manifest.contentHash,
      updatedAt: new Date().toISOString(),
    } satisfies ActiveWeatherPointer);
    transaction.oncomplete = () => resolve();
    transaction.onerror = () => reject(transaction.error ?? new Error('Unable to install offline weather package.'));
    transaction.onabort = () => reject(transaction.error ?? new Error('Offline weather package installation was aborted.'));
  }));
  if (priorContentHash && priorContentHash !== install.manifest.contentHash) {
    await browserGarbageCollect(priorContentHash);
  }
}

async function browserReadLegacyPackage(terrainKey: string): Promise<WeatherDataPackage | null> {
  const database = await openDb();
  return closeAfter(database, new Promise((resolve, reject) => {
    const transaction = database.transaction(LEGACY_PACKAGES_STORE, 'readonly');
    const request = transaction.objectStore(LEGACY_PACKAGES_STORE).get(terrainKey);
    transaction.oncomplete = () => resolve(validateWeatherPackage(request.result) ? request.result : null);
    transaction.onerror = () => reject(transaction.error ?? new Error('Unable to read legacy offline weather package.'));
  }));
}

async function browserDeleteLegacyPackage(terrainKey: string): Promise<void> {
  const database = await openDb();
  await closeAfter(database, new Promise<void>((resolve, reject) => {
    const transaction = database.transaction(LEGACY_PACKAGES_STORE, 'readwrite');
    transaction.objectStore(LEGACY_PACKAGES_STORE).delete(terrainKey);
    transaction.oncomplete = () => resolve();
    transaction.onerror = () => reject(transaction.error ?? new Error('Unable to remove legacy offline weather package.'));
  }));
}

function isWeatherPackagePinnedByBrowserSave(contentHash: string): boolean {
  try {
    if (typeof localStorage === 'undefined') return false;
    const index: unknown = JSON.parse(localStorage.getItem(GAME_SAVE_INDEX_KEY) ?? '[]');
    if (!Array.isArray(index)) return false;
    for (const summary of index) {
      if (!isRecord(summary) || typeof summary.key !== 'string') return true;
      const raw = localStorage.getItem(`${GAME_SAVE_PREFIX}${summary.key}`);
      if (!raw) continue;
      try {
        const save: unknown = JSON.parse(raw);
        if (isRecord(save) && isRecord(save.weatherRun) && save.weatherRun.packageContentHash === contentHash) return true;
      } catch {
        // Do not collect any artifact while a listed save cannot be inspected.
        return true;
      }
    }
    return false;
  } catch {
    // Storage access failure is not proof that no save pins this package.
    return true;
  }
}

async function browserHasActiveReference(contentHash: string): Promise<boolean> {
  const database = await openDb();
  return closeAfter(database, new Promise((resolve, reject) => {
    const transaction = database.transaction(ACTIVE_STORE, 'readonly');
    const request = transaction.objectStore(ACTIVE_STORE).getAll();
    transaction.oncomplete = () => resolve((request.result as unknown[]).some((pointer) =>
      isRecord(pointer) && pointer.contentHash === contentHash));
    transaction.onerror = () => reject(transaction.error ?? new Error('Unable to inspect offline weather pointers.'));
  }));
}

async function browserGarbageCollect(contentHash: string): Promise<void> {
  if (await browserHasActiveReference(contentHash) || isWeatherPackagePinnedByBrowserSave(contentHash)) return;
  const manifest = await browserReadManifest(contentHash);
  if (!manifest) return;
  const database = await openDb();
  await closeAfter(database, new Promise<void>((resolve, reject) => {
    const transaction = database.transaction([MANIFESTS_STORE, CHUNKS_STORE], 'readwrite');
    const chunks = transaction.objectStore(CHUNKS_STORE);
    for (const descriptor of manifest.chunks) chunks.delete(chunkId(contentHash, descriptor.key));
    transaction.objectStore(MANIFESTS_STORE).delete(contentHash);
    transaction.oncomplete = () => resolve();
    transaction.onerror = () => reject(transaction.error ?? new Error('Unable to remove unreferenced offline weather package.'));
  }));
}

async function browserDelete(terrainKey: string): Promise<void> {
  const database = await openDb();
  let contentHash: string | null = null;
  await closeAfter(database, new Promise<void>((resolve, reject) => {
    const transaction = database.transaction(ACTIVE_STORE, 'readwrite');
    const active = transaction.objectStore(ACTIVE_STORE);
    const request = active.get(terrainKey);
    request.onsuccess = () => {
      const pointer = request.result;
      contentHash = isRecord(pointer) && typeof pointer.contentHash === 'string' ? pointer.contentHash : null;
      active.delete(terrainKey);
    };
    transaction.oncomplete = () => resolve();
    transaction.onerror = () => reject(transaction.error ?? new Error('Unable to remove offline weather pointer.'));
  }));
  await browserDeleteLegacyPackage(terrainKey);
  if (contentHash) await browserGarbageCollect(contentHash);
}

async function browserLoadForTerrain(
  terrainKey: string,
  expectedTerrainBinding?: string,
): Promise<WeatherPackageLoadResult> {
  let pointer = await browserReadPointer(terrainKey);
  if (!pointer) {
    // v1 browser packages were keyed directly by terrain. Convert the first
    // time they are opened; a failed conversion leaves the legacy value intact.
    const legacy = await browserReadLegacyPackage(terrainKey);
    if (legacy) {
      await browserInstall(await createWeatherPackageStorageInstall(legacy));
      await browserDeleteLegacyPackage(terrainKey);
      pointer = await browserReadPointer(terrainKey);
    }
  }
  if (!pointer) return { status: 'not-found' };
  if (expectedTerrainBinding && pointer.terrainBinding !== expectedTerrainBinding) {
    return { status: 'binding-mismatch', error: 'The prepared weather package belongs to a different terrain revision.' };
  }
  try {
    const install = await browserReadInstall(pointer.contentHash);
    if (!install) return { status: 'corrupt', error: 'The active weather package manifest is missing.' };
    const weatherPackage = await decodeWeatherPackageStorageInstall(install);
    if (weatherPackage.manifest.terrainKey !== terrainKey ||
      weatherPackage.manifest.terrainBinding !== pointer.terrainBinding) {
      return { status: 'corrupt', error: 'The active weather package does not match its terrain pointer.' };
    }
    return { status: 'ready', weatherPackage, storageManifest: install.manifest };
  } catch (error) {
    return { status: 'corrupt', error: error instanceof Error ? error.message : 'Unable to read offline weather package.' };
  }
}

async function browserLoadByContentHash(contentHash: string): Promise<WeatherDataPackage | null> {
  try {
    const install = await browserReadInstall(contentHash);
    return install ? await decodeWeatherPackageStorageInstall(install) : null;
  } catch {
    return null;
  }
}

/** Load an immutable artifact, including binary chunks, for a pinned runtime. */
export async function loadWeatherPackageStorageInstallByContentHash(
  contentHash: string,
): Promise<WeatherPackageStorageInstall | null> {
  if (!contentHash) return null;
  if (desktop) return desktop.weather.loadInstallByContentHash(contentHash);
  try {
    return await browserReadInstall(contentHash);
  } catch {
    return null;
  }
}

/** Store a pre-built, checksummed package artifact. */
export async function saveWeatherPackageStorageInstall(
  install: WeatherPackageStorageInstall,
): Promise<void> {
  await validateWeatherPackageStorageInstall(install);
  if (desktop) {
    const result = await desktop.weather.install(install);
    if (!result.ok) throw new Error(result.error);
    return;
  }
  await browserInstall(install);
}

/** Store a legacy weather response as a content-addressed package artifact. */
export async function saveWeatherPackage(weatherPackage: WeatherDataPackage): Promise<void> {
  await saveWeatherPackageStorageInstall(await createWeatherPackageStorageInstall(weatherPackage));
}

/**
 * Load the active package for a terrain map. An optional binding makes stale
 * map/package pairings distinguishable from a missing package.
 */
export async function loadWeatherPackageResult(
  terrainKey: string,
  expectedTerrainBinding?: string,
): Promise<WeatherPackageLoadResult> {
  if (desktop) {
    try {
      const weatherPackage = await desktop.weather.load(terrainKey);
      if (!weatherPackage) return { status: 'not-found' };
      if (expectedTerrainBinding && weatherPackage.manifest.terrainBinding !== expectedTerrainBinding) {
        return { status: 'binding-mismatch', error: 'The prepared weather package belongs to a different terrain revision.' };
      }
      return { status: 'ready', weatherPackage };
    } catch (error) {
      return { status: 'corrupt', error: error instanceof Error ? error.message : 'Unable to read offline weather package.' };
    }
  }
  try {
    return await browserLoadForTerrain(terrainKey, expectedTerrainBinding);
  } catch (error) {
    return { status: 'corrupt', error: error instanceof Error ? error.message : 'Unable to read offline weather package.' };
  }
}

export async function loadWeatherPackage(terrainKey: string): Promise<WeatherDataPackage | null> {
  const result = await loadWeatherPackageResult(terrainKey);
  return result.status === 'ready' ? result.weatherPackage : null;
}

/** Resolve a package pinned by GameSave.weatherRun without consulting a terrain pointer. */
export async function loadWeatherPackageByContentHash(contentHash: string): Promise<WeatherDataPackage | null> {
  if (!contentHash) return null;
  if (desktop) {
    try {
      return await desktop.weather.loadByContentHash(contentHash);
    } catch {
      return null;
    }
  }
  return browserLoadByContentHash(contentHash);
}

/** Remove a terrain's active pointer, retaining packages still pinned by a game save. */
export async function deleteWeatherPackage(terrainKey: string): Promise<void> {
  if (desktop) {
    const result = await desktop.weather.delete(terrainKey);
    if (!result.ok) throw new Error(result.error);
    return;
  }
  await browserDelete(terrainKey);
}
