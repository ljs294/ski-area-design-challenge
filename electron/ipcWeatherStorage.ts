import { app, ipcMain } from 'electron';
import { createHash } from 'crypto';
import fs from 'fs';
import path from 'path';
import { promisify } from 'util';
import { gunzip as gunzipCallback, gzip as gzipCallback } from 'zlib';
import {
  WEATHER_DELETE_CHANNEL,
  WEATHER_LOAD_BY_CONTENT_HASH_CHANNEL,
  WEATHER_LOAD_CHANNEL,
  WEATHER_LOAD_INSTALL_BY_CONTENT_HASH_CHANNEL,
  WEATHER_SAVE_CHANNEL,
  type WeatherDeleteRequest,
  type WeatherDeleteResponse,
  type WeatherLoadByContentHashRequest,
  type WeatherLoadByContentHashResponse,
  type WeatherLoadInstallByContentHashResponse,
  type WeatherLoadRequest,
  type WeatherLoadResponse,
  type WeatherPackageStorageInstall,
  type WeatherPackageStorageManifest,
  type WeatherSourceChunkDescriptor,
  type WeatherSaveRequest,
  type WeatherSaveResponse,
  type WeatherStorageChunk,
  type WeatherStorageChunkDescriptor,
} from '../src/ipcContract';
import { isSavedWeatherRun } from '../src/types/gameSave';
import { isWeatherDataPackage, type HistoricalWeatherYear, type WeatherDataPackage } from '../src/weather/weatherModel';

const fsp = fs.promises;
const gzip = promisify(gzipCallback);
const gunzip = promisify(gunzipCallback);

type StorageRecord = Record<string, unknown>;

interface ActiveWeatherPointer {
  terrainKey: string;
  terrainBinding: string;
  contentHash: string;
  updatedAt: string;
}

function isRecord(value: unknown): value is StorageRecord {
  return !!value && typeof value === 'object' && !Array.isArray(value);
}

function hash(value: string | Uint8Array): string {
  return createHash('sha256').update(value).digest('hex');
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

function isStorageManifest(value: unknown): value is WeatherPackageStorageManifest {
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
  if (Array.isArray(value) && value.every((entry) => Number.isInteger(entry) && entry >= 0 && entry <= 255)) {
    return new Uint8Array(value);
  }
  return null;
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

async function validateInstall(value: unknown): Promise<WeatherPackageStorageInstall> {
  if (!isRecord(value) || !isStorageManifest(value.manifest) || !Array.isArray(value.chunks) ||
    value.chunks.length !== value.manifest.chunks.length) {
    throw new Error('Invalid offline weather package artifact.');
  }
  const manifest = value.manifest;
  if (manifest.sourceManifest.terrainKey !== manifest.terrainKey ||
    manifest.sourceManifest.terrainBinding !== manifest.terrainBinding ||
    manifest.sourceManifest.contentHash !== manifest.contentHash) {
    throw new Error('Offline weather package binding does not match its manifest.');
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
  const expected = new Map<string, WeatherStorageChunkDescriptor>();
  for (const descriptor of manifest.chunks) {
    if (expected.has(descriptor.key)) throw new Error(`Offline weather package repeats chunk ${descriptor.key}.`);
    expected.set(descriptor.key, descriptor);
  }
  const chunks: WeatherStorageChunk[] = [];
  for (const candidate of value.chunks) {
    if (!isRecord(candidate) || !isChunkDescriptor(candidate)) {
      throw new Error('Offline weather package has an invalid chunk descriptor.');
    }
    const descriptor = expected.get(candidate.key);
    const data = toBytes(candidate.data);
    if (!descriptor || !data || candidate.encoding !== descriptor.encoding ||
      candidate.byteLength !== descriptor.byteLength || candidate.checksum !== descriptor.checksum ||
      data.byteLength !== descriptor.byteLength || hash(data).toLowerCase() !== descriptor.checksum.toLowerCase()) {
      throw new Error(`Offline weather package chunk ${candidate.key} failed validation.`);
    }
    expected.delete(candidate.key);
    chunks.push({ ...candidate, data });
  }
  if (expected.size > 0) throw new Error('Offline weather package chunks are incomplete.');
  return { manifest, chunks };
}

async function jsonChunk(key: string, value: unknown): Promise<WeatherStorageChunk> {
  const json = JSON.stringify(value);
  if (typeof json !== 'string') throw new Error(`Unable to serialize offline weather chunk ${key}.`);
  const data = new Uint8Array(await gzip(Buffer.from(json, 'utf8')));
  return { key, encoding: 'gzip-json', byteLength: data.byteLength, checksum: hash(data), data };
}

async function createLegacyInstall(weatherPackage: WeatherDataPackage): Promise<WeatherPackageStorageInstall> {
  if (!isWeatherDataPackage(weatherPackage) || !weatherPackage.historicalYears) {
    throw new Error('Invalid offline weather package.');
  }
  const years = [...weatherPackage.historicalYears].sort((left, right) => left.year - right.year);
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
  return {
    manifest: {
      storageSchemaVersion: 2,
      contentHash: weatherPackage.manifest.contentHash,
      terrainKey: weatherPackage.manifest.terrainKey,
      terrainBinding: weatherPackage.manifest.terrainBinding,
      payloadHash: hash(JSON.stringify(weatherPackage)),
      payloadFormat: 'legacy-weather-data-package-v1',
      sourceManifest: weatherPackage.manifest,
      chunks: chunks.map(({ data: _data, ...descriptor }) => descriptor),
      complete: true,
      createdAt: weatherPackage.manifest.createdAt,
    },
    chunks,
  };
}

async function createNativeChunkInstall(weatherPackage: WeatherDataPackage): Promise<WeatherPackageStorageInstall | null> {
  const sourceManifest = weatherPackage.manifest;
  const transport = providerChunkTransport(weatherPackage);
  if (!isWeatherDataPackage(weatherPackage) || !isSourceManifest(sourceManifest) || !transport) return null;
  const chunks: WeatherStorageChunk[] = transport.map(({ descriptor, dataBase64 }) => {
    let data: Uint8Array;
    try {
      data = new Uint8Array(Buffer.from(dataBase64, 'base64'));
    } catch {
      throw new Error('Weather service supplied an invalid binary chunk.');
    }
    return {
      key: descriptor.id,
      encoding: 'binary',
      byteLength: data.byteLength,
      checksum: descriptor.checksumSha256,
      data,
    };
  });
  const sourceChunks = transport.map(({ descriptor }) => descriptor);
  const install: WeatherPackageStorageInstall = {
    manifest: {
      storageSchemaVersion: 2,
      contentHash: sourceManifest.contentHash,
      terrainKey: sourceManifest.terrainKey,
      terrainBinding: sourceManifest.terrainBinding,
      payloadHash: hash(chunkPayloadIdentity(sourceManifest, sourceChunks)),
      payloadFormat: 'weather-package-chunks-v1',
      sourceManifest,
      sourceChunks,
      chunks: chunks.map(({ data: _data, ...descriptor }) => descriptor),
      complete: true,
      createdAt: typeof sourceManifest.createdAt === 'string' ? sourceManifest.createdAt : new Date().toISOString(),
    },
    chunks,
  };
  return validateInstall(install);
}

async function installFromWeatherPackage(weatherPackage: WeatherDataPackage): Promise<WeatherPackageStorageInstall> {
  return await createNativeChunkInstall(weatherPackage) ?? createLegacyInstall(weatherPackage);
}

async function parseJsonChunk(chunk: WeatherStorageChunk): Promise<unknown> {
  if (chunk.encoding !== 'gzip-json') throw new Error(`Weather chunk ${chunk.key} is not JSON.`);
  try {
    return JSON.parse((await gunzip(Buffer.from(chunk.data))).toString('utf8'));
  } catch {
    throw new Error(`Weather chunk ${chunk.key} could not be decoded.`);
  }
}

async function decodeLegacyInstall(install: WeatherPackageStorageInstall): Promise<WeatherDataPackage> {
  if (install.manifest.payloadFormat === 'weather-package-chunks-v1') {
    const sourceChunks = install.manifest.sourceChunks;
    if (!sourceChunks) throw new Error('Offline weather package is missing source chunk metadata.');
    const chunks = new Map(install.chunks.map((chunk) => [chunk.key, chunk]));
    const weatherPackage = {
      manifest: install.manifest.sourceManifest,
      chunks: sourceChunks.map((descriptor) => {
        const chunk = chunks.get(descriptor.id);
        if (!chunk) throw new Error(`Offline weather package chunk ${descriptor.id} is missing.`);
        return { descriptor, dataBase64: Buffer.from(chunk.data).toString('base64') };
      }),
      historicalYears: [],
    } as unknown;
    if (!isWeatherDataPackage(weatherPackage) ||
      hash(chunkPayloadIdentity(install.manifest.sourceManifest, sourceChunks)).toLowerCase() !==
        install.manifest.payloadHash.toLowerCase()) {
      throw new Error('Offline weather package payload does not match its storage manifest.');
    }
    return weatherPackage;
  }
  if (install.manifest.payloadFormat !== 'legacy-weather-data-package-v1') {
    throw new Error('This weather package requires the current weather runtime to decode its binary chunks.');
  }
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
  if (!isWeatherDataPackage(weatherPackage)) throw new Error('Offline weather package payload is invalid.');
  if (weatherPackage.manifest.contentHash !== install.manifest.contentHash ||
    weatherPackage.manifest.terrainKey !== install.manifest.terrainKey ||
    weatherPackage.manifest.terrainBinding !== install.manifest.terrainBinding ||
    hash(JSON.stringify(weatherPackage)).toLowerCase() !== install.manifest.payloadHash.toLowerCase()) {
    throw new Error('Offline weather package payload does not match its storage manifest.');
  }
  return weatherPackage;
}

function weatherRoot(): string {
  const dir = path.join(app.getPath('userData'), 'weather');
  fs.mkdirSync(dir, { recursive: true });
  return dir;
}

function safeChild(parent: string, segment: string): string {
  const resolvedParent = path.resolve(parent);
  const resolved = path.resolve(resolvedParent, segment);
  const relative = path.relative(resolvedParent, resolved);
  if (!relative || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative)) {
    throw new Error('Invalid offline weather storage path.');
  }
  return resolved;
}

function packageDirectory(contentHash: string): string {
  const packages = path.join(weatherRoot(), 'packages');
  fs.mkdirSync(packages, { recursive: true });
  return safeChild(packages, hash(contentHash));
}

function activeDirectory(): string {
  const active = path.join(weatherRoot(), 'active');
  fs.mkdirSync(active, { recursive: true });
  return active;
}

function pointerFile(terrainKey: string): string {
  return safeChild(activeDirectory(), `${hash(terrainKey)}.json`);
}

function legacyFile(terrainKey: string): string | null {
  try {
    return safeChild(weatherRoot(), `${terrainKey}.json`);
  } catch {
    return null;
  }
}

function manifestFile(contentHash: string): string {
  return safeChild(packageDirectory(contentHash), 'manifest.json');
}

function chunkFile(contentHash: string, key: string): string {
  return safeChild(safeChild(packageDirectory(contentHash), 'chunks'), `${hash(key)}.bin`);
}

async function atomicWrite(file: string, data: string | Uint8Array): Promise<void> {
  const directory = path.dirname(file);
  await fsp.mkdir(directory, { recursive: true });
  const temporary = path.join(directory, `.${path.basename(file)}.${process.pid}-${Date.now()}-${Math.random().toString(36).slice(2)}.tmp`);
  try {
    await fsp.writeFile(temporary, data);
    await fsp.rename(temporary, file);
  } catch (error) {
    try { await fsp.rm(temporary, { force: true }); } catch { /* best effort cleanup */ }
    throw error;
  }
}

function isActivePointer(value: unknown): value is ActiveWeatherPointer {
  return isRecord(value) && typeof value.terrainKey === 'string' && typeof value.terrainBinding === 'string' &&
    typeof value.contentHash === 'string' && typeof value.updatedAt === 'string';
}

async function readActivePointer(terrainKey: string): Promise<ActiveWeatherPointer | null> {
  const file = pointerFile(terrainKey);
  try {
    const parsed: unknown = JSON.parse(await fsp.readFile(file, 'utf8'));
    return isActivePointer(parsed) && parsed.terrainKey === terrainKey ? parsed : null;
  } catch {
    return null;
  }
}

async function readStorageManifest(contentHash: string): Promise<WeatherPackageStorageManifest | null> {
  try {
    const parsed: unknown = JSON.parse(await fsp.readFile(manifestFile(contentHash), 'utf8'));
    return isStorageManifest(parsed) && parsed.contentHash === contentHash ? parsed : null;
  } catch {
    return null;
  }
}

async function readInstall(contentHash: string): Promise<WeatherPackageStorageInstall | null> {
  const manifest = await readStorageManifest(contentHash);
  if (!manifest) return null;
  const chunks: WeatherStorageChunk[] = [];
  for (const descriptor of manifest.chunks) {
    let data: Uint8Array;
    try {
      data = new Uint8Array(await fsp.readFile(chunkFile(contentHash, descriptor.key)));
    } catch {
      throw new Error(`Offline weather package chunk ${descriptor.key} is missing.`);
    }
    if (data.byteLength !== descriptor.byteLength || hash(data).toLowerCase() !== descriptor.checksum.toLowerCase()) {
      throw new Error(`Offline weather package chunk ${descriptor.key} failed checksum validation.`);
    }
    chunks.push({ ...descriptor, data });
  }
  return validateInstall({ manifest, chunks });
}

async function writeInstall(install: WeatherPackageStorageInstall): Promise<void> {
  const validated = await validateInstall(install);
  const target = packageDirectory(validated.manifest.contentHash);
  const existing = await readStorageManifest(validated.manifest.contentHash);
  if (existing) {
    if (existing.payloadHash !== validated.manifest.payloadHash ||
      !descriptorsMatch(existing.chunks, validated.manifest.chunks)) {
      throw new Error('A different offline weather package already uses this content hash.');
    }
    // Reuse is allowed only after every existing binary chunk has been
    // checksummed again. A damaged cache is never silently activated.
    if (!(await readInstall(validated.manifest.contentHash))) {
      throw new Error('Offline weather package cache disappeared while it was being validated.');
    }
    return;
  }
  if (fs.existsSync(target)) {
    throw new Error('Offline weather package storage is incomplete or corrupt; it cannot be replaced automatically.');
  }
  const parent = path.dirname(target);
  const temporary = safeChild(parent, `.${path.basename(target)}.${process.pid}-${Date.now()}-${Math.random().toString(36).slice(2)}.tmp`);
  try {
    await fsp.mkdir(path.join(temporary, 'chunks'), { recursive: true });
    for (const chunk of validated.chunks) {
      const file = path.join(temporary, 'chunks', `${hash(chunk.key)}.bin`);
      await fsp.writeFile(file, chunk.data);
    }
    // A package directory only gets a manifest after every chunk has been
    // written. The directory rename then publishes it atomically.
    await fsp.writeFile(path.join(temporary, 'manifest.json'), JSON.stringify(validated.manifest, null, 2), 'utf8');
    await fsp.rename(temporary, target);
  } catch (error) {
    try { await fsp.rm(temporary, { recursive: true, force: true }); } catch { /* best effort cleanup */ }
    if (fs.existsSync(target)) {
      const raced = await readStorageManifest(validated.manifest.contentHash);
      if (raced && raced.payloadHash === validated.manifest.payloadHash &&
        descriptorsMatch(raced.chunks, validated.manifest.chunks)) return;
    }
    throw error;
  }
}

async function activateInstall(manifest: WeatherPackageStorageManifest): Promise<string | null> {
  const previous = await readActivePointer(manifest.terrainKey);
  await atomicWrite(pointerFile(manifest.terrainKey), JSON.stringify({
    terrainKey: manifest.terrainKey,
    terrainBinding: manifest.terrainBinding,
    contentHash: manifest.contentHash,
    updatedAt: new Date().toISOString(),
  } satisfies ActiveWeatherPointer));
  return previous?.contentHash ?? null;
}

async function hasActiveReference(contentHash: string): Promise<boolean> {
  let files: string[];
  try {
    files = await fsp.readdir(activeDirectory());
  } catch {
    return false;
  }
  for (const file of files) {
    if (!file.endsWith('.json')) continue;
    try {
      const parsed: unknown = JSON.parse(await fsp.readFile(safeChild(activeDirectory(), file), 'utf8'));
      if (isActivePointer(parsed) && parsed.contentHash === contentHash) return true;
    } catch { /* ignore malformed inactive pointer */ }
  }
  return false;
}

async function isPinnedByGameSave(contentHash: string): Promise<boolean> {
  const saves = path.join(app.getPath('userData'), 'saves');
  let files: string[];
  try {
    files = await fsp.readdir(saves);
  } catch {
    return false;
  }
  for (const file of files) {
    if (file === 'index.json' || !file.endsWith('.json')) continue;
    const source = safeChild(saves, file);
    try {
      const parsed: unknown = JSON.parse(await fsp.readFile(source, 'utf8'));
      if (isRecord(parsed) && isSavedWeatherRun(parsed.weatherRun) &&
        parsed.weatherRun.packageContentHash === contentHash) return true;
    } catch {
      // A damaged save may still refer to any artifact; retain rather than
      // potentially breaking an otherwise recoverable saved game.
      return true;
    }
  }
  return false;
}

async function garbageCollect(contentHash: string): Promise<void> {
  if (await hasActiveReference(contentHash) || await isPinnedByGameSave(contentHash)) return;
  const target = packageDirectory(contentHash);
  // `target` is a SHA-256-derived direct child of the dedicated weather
  // sidecar directory, not a user-supplied path.
  await fsp.rm(target, { recursive: true, force: true });
}

async function migrateLegacyPackage(terrainKey: string): Promise<WeatherDataPackage | null> {
  const source = legacyFile(terrainKey);
  if (!source) return null;
  let parsed: unknown;
  try {
    parsed = JSON.parse(await fsp.readFile(source, 'utf8'));
  } catch {
    return null;
  }
  if (!isWeatherDataPackage(parsed)) return null;
  const install = await createLegacyInstall(parsed);
  await writeInstall(install);
  await activateInstall(install.manifest);
  await fsp.rm(source, { force: true });
  return parsed;
}

async function loadActivePackage(terrainKey: string): Promise<WeatherDataPackage | null> {
  let pointer = await readActivePointer(terrainKey);
  if (!pointer) {
    const migrated = await migrateLegacyPackage(terrainKey);
    if (migrated) return migrated;
    pointer = await readActivePointer(terrainKey);
  }
  if (!pointer) return null;
  const install = await readInstall(pointer.contentHash);
  if (!install || install.manifest.terrainKey !== terrainKey ||
    install.manifest.terrainBinding !== pointer.terrainBinding) return null;
  return decodeLegacyInstall(install);
}

export function registerWeatherStorageHandlers(): void {
  ipcMain.handle(WEATHER_SAVE_CHANNEL, async (_event, request: WeatherSaveRequest): Promise<WeatherSaveResponse> => {
    try {
      const install = request?.install ? await validateInstall(request.install) :
        request?.weatherPackage ? await installFromWeatherPackage(request.weatherPackage) : null;
      if (!install) return { ok: false, error: 'Invalid offline weather package.' };
      await writeInstall(install);
      const replaced = await activateInstall(install.manifest);
      if (replaced && replaced !== install.manifest.contentHash) await garbageCollect(replaced);
      return { ok: true };
    } catch (error) {
      return { ok: false, error: error instanceof Error ? error.message : 'Unable to save offline weather package.' };
    }
  });

  ipcMain.handle(WEATHER_LOAD_CHANNEL, async (_event, request: WeatherLoadRequest): Promise<WeatherLoadResponse> => {
    if (!request || typeof request.terrainKey !== 'string' || !request.terrainKey) return null;
    try {
      return await loadActivePackage(request.terrainKey);
    } catch {
      return null;
    }
  });

  ipcMain.handle(
    WEATHER_LOAD_BY_CONTENT_HASH_CHANNEL,
    async (_event, request: WeatherLoadByContentHashRequest): Promise<WeatherLoadByContentHashResponse> => {
      if (!request || typeof request.contentHash !== 'string' || !request.contentHash) return null;
      try {
        const install = await readInstall(request.contentHash);
        return install ? await decodeLegacyInstall(install) : null;
      } catch {
        return null;
      }
    },
  );

  ipcMain.handle(
    WEATHER_LOAD_INSTALL_BY_CONTENT_HASH_CHANNEL,
    async (_event, request: WeatherLoadByContentHashRequest): Promise<WeatherLoadInstallByContentHashResponse> => {
      if (!request || typeof request.contentHash !== 'string' || !request.contentHash) return null;
      try {
        return await readInstall(request.contentHash);
      } catch {
        return null;
      }
    },
  );

  ipcMain.handle(WEATHER_DELETE_CHANNEL, async (_event, request: WeatherDeleteRequest): Promise<WeatherDeleteResponse> => {
    if (!request || typeof request.terrainKey !== 'string' || !request.terrainKey) {
      return { ok: false, error: 'Invalid terrain key.' };
    }
    try {
      const pointer = await readActivePointer(request.terrainKey);
      await fsp.rm(pointerFile(request.terrainKey), { force: true });
      const legacy = legacyFile(request.terrainKey);
      if (legacy) await fsp.rm(legacy, { force: true });
      if (pointer) await garbageCollect(pointer.contentHash);
      return { ok: true };
    } catch (error) {
      return { ok: false, error: error instanceof Error ? error.message : 'Unable to remove offline weather package.' };
    }
  });
}
